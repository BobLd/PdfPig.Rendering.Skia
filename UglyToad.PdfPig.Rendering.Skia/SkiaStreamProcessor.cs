// Copyright 2024 BobLd
//
// Licensed under the Apache License, Version 2.0 (the "License").
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Geometry;
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Graphics.Operations;
using UglyToad.PdfPig.Parser;
using UglyToad.PdfPig.Rendering.Skia.Helpers;
using UglyToad.PdfPig.Tokenization.Scanner;
using UglyToad.PdfPig.Tokens;

namespace UglyToad.PdfPig.Rendering.Skia
{
    internal partial class SkiaStreamProcessor : BaseStreamProcessor<SKPicture>
    {
        private const float _minimumLineWidth = 0.25f;
        private const bool _antiAliasing = true;

        private readonly bool _renderAnnotations;
        private readonly float _height;
        private readonly float _width;

        private readonly SkiaFontCache _fontCache;
        private readonly SKPaintCache _paintCache = new SKPaintCache(_antiAliasing, _minimumLineWidth);

        // Stack to keep track of original transforms for nested form XObjects
        private readonly Stack<SKMatrix> _currentStreamOriginalTransforms = new();

        // Stack to track layer paints for transparency groups (null means regular Save was used)
        private readonly Stack<SKPaint?> _transparencyLayerPaints = new();

        // Pending layer paint to be used in the next PushState call
        private SKPaint? _pendingTransparencyLayerPaint;

        private SKCanvas _canvas;

        private bool _updateCurrentStreamOriginalTransform;

        public SkiaStreamProcessor(
            int pageNumber,
            IResourceStore resourceStore,
            IPdfTokenScanner pdfScanner,
            IPageContentParser pageContentParser,
            ILookupFilterProvider filterProvider,
            CropBox cropBox,
            UserSpaceUnit userSpaceUnit,
            PageRotationDegrees rotation,
            TransformationMatrix initialMatrix,
            ParsingOptions parsingOptions,
            AnnotationProvider? annotationProvider,
            SkiaFontCache fontCache)
            : base(pageNumber,
                resourceStore,
                pdfScanner,
                pageContentParser,
                filterProvider,
                cropBox,
                userSpaceUnit,
                rotation,
                initialMatrix,
                parsingOptions)
        {
            _renderAnnotations = annotationProvider is not null;
            _annotations = new Lazy<Annotation[]>(() => annotationProvider?.GetAnnotations().ToArray() ?? []);

            _fontCache = fontCache;

            _width = (float)cropBox.Bounds.Width;
            _height = (float)cropBox.Bounds.Height;

            _currentStreamOriginalTransforms.Push(initialMatrix.ToSkMatrix());
        }

        public override SKPicture Process(int pageNumberCurrent, IReadOnlyList<IGraphicsStateOperation> operations)
        {
            try
            {
                // https://github.com/apache/pdfbox/blob/94b3d15fd24b9840abccece261173593625ff85c/pdfbox/src/main/java/org/apache/pdfbox/rendering/PDFRenderer.java#L274

                CloneAllStates();

                using var recorder = new SKPictureRecorder();
                _canvas = recorder.BeginRecording(SKRect.Create(_width, _height), true);

                // Inverse direction of y-axis
                _canvas.SetMatrix(SKMatrix.CreateScale(1, -1, 0, _height / 2f));
                _canvas.Concat(CurrentTransformationMatrix.ToSkMatrix());

                PushState();

                ProcessOperations(operations);

                PopState();

                if (_renderAnnotations)
                {
                    DrawAnnotations();
                }

                _canvas.Flush();

                return recorder.EndRecording();
            }
            finally
            {
                Cleanup();
            }
        }

        public override void PopState()
        {
            base.PopState();
            _canvas.Restore();

            // Dispose layer paint if one was used for this state
            var layerPaint = _transparencyLayerPaints.Pop();
            layerPaint?.Dispose();

            EndPath();
        }

        public override void PushState()
        {
            base.PushState();

            if (_pendingTransparencyLayerPaint != null)
            {
                // Use SaveLayer for transparency group - creates offscreen buffer
                _canvas.SaveLayer(_pendingTransparencyLayerPaint);
                _transparencyLayerPaints.Push(_pendingTransparencyLayerPaint);
                _pendingTransparencyLayerPaint = null;
            }
            else
            {
                _canvas.Save();
                _transparencyLayerPaints.Push(null);
            }

            EndPath();
        }

        public override void ModifyClippingIntersect(FillingRule clippingRule)
        {
            if (_currentPath is null)
            {
                return;
            }

            _currentPath.FillType = clippingRule.ToSKPathFillType();
            _canvas.ClipPath(_currentPath, SKClipOperation.Intersect);
        }

        protected override void ClipToRectangle(PdfRectangle rectangle, FillingRule clippingRule)
        {
            //_canvas.DrawRect(rectangle.ToSKRect(_height), _paintCache.GetImageDebug());
            _canvas.ClipRect(rectangle.ToSKRect(), SKClipOperation.Intersect);
        }

        /// <inheritdoc/>
        public override void BeginMarkedContent(NameToken name, NameToken? propertyDictionaryName, DictionaryToken? properties)
        {
            // No op
        }

        /// <inheritdoc/>
        public override void EndMarkedContent()
        {
            // No op
        }

        public override void ModifyCurrentTransformationMatrix(TransformationMatrix value)
        {
            base.ModifyCurrentTransformationMatrix(value);

            _canvas.Concat(value.ToSkMatrix());

            if (_updateCurrentStreamOriginalTransform)
            {
                // Update the original transform for form XObject
                _currentStreamOriginalTransforms.Push(CurrentTransformationMatrix.ToSkMatrix());

                _updateCurrentStreamOriginalTransform = false;
            }
        }

        // Note that recursive calls are possible here for nested form XObjects
        protected override void ProcessFormXObject(StreamToken formStream, NameToken xObjectName)
        {
            // Check if this is a Transparency Group XObject
            bool isTransparencyGroup = formStream.StreamDictionary.TryGet(NameToken.Group, PdfScanner, out DictionaryToken? formGroupToken)
                && formGroupToken.TryGet<NameToken>(NameToken.S, PdfScanner, out var sToken)
                && sToken == NameToken.Transparency;

            if (isTransparencyGroup)
            {
                // Capture parent state's blend mode and alpha before PushState
                var parentState = GetCurrentState();

                // Set pending layer paint - will be used in PushState called by base.ProcessFormXObject
                // The transparency group will be composited with parent's blend mode and alpha when the layer is restored
                _pendingTransparencyLayerPaint = new SKPaint
                {
                    IsAntialias = _antiAliasing,
                    BlendMode = parentState.BlendMode.ToSKBlendMode(),
                    Color = SKColors.White.WithAlpha((byte)(parentState.AlphaConstantNonStroking * 255))
                };
            }

            // Indicate that we want to update the original transform for form XObject
            _updateCurrentStreamOriginalTransform = true;

            base.ProcessFormXObject(formStream, xObjectName);

            // Restore previous original transform
            _currentStreamOriginalTransforms.Pop();
        }

        private void Cleanup()
        {
            _paintCache.Dispose();
            _pendingTransparencyLayerPaint?.Dispose();
            _pendingTransparencyLayerPaint = null;
            foreach (var paint in _transparencyLayerPaints)
            {
                paint?.Dispose();
            }
            _transparencyLayerPaints.Clear();
        }
    }
}
