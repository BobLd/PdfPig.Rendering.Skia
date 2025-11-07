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
        private readonly bool _renderAnnotations = true; // TODO - param
        private const bool _antiAliasing = true;

        private readonly float _height;
        private readonly float _width;

        private SKCanvas _canvas;

        private const float _minimumLineWidth = 0.25f;

        /// <summary>
        /// Inverse direction of y-axis
        /// </summary>
        private readonly SKMatrix _yAxisFlipMatrix;

        private readonly SkiaFontCache _fontCache;
        private readonly SKPaintCache _paintCache = new SKPaintCache(_antiAliasing, _minimumLineWidth);

        // Stack to keep track of original transforms for nested form XObjects
        private readonly Stack<SKMatrix> _currentStreamOriginalTransforms = new Stack<SKMatrix>();

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

            _yAxisFlipMatrix = SKMatrix.CreateScale(1, -1, 0, _height / 2f);
        }

        public override SKPicture Process(int pageNumberCurrent, IReadOnlyList<IGraphicsStateOperation> operations)
        {
            try
            {
                // https://github.com/apache/pdfbox/blob/94b3d15fd24b9840abccece261173593625ff85c/pdfbox/src/main/java/org/apache/pdfbox/rendering/PDFRenderer.java#L274

                CloneAllStates();

                using (var recorder = new SKPictureRecorder())
                using (_canvas = recorder.BeginRecording(SKRect.Create(_width, _height)))
                {
                    _canvas.SetMatrix(_yAxisFlipMatrix);
                    _canvas.Concat(CurrentTransformationMatrix.ToSkMatrix());

                    if (_renderAnnotations)
                    {
                        DrawAnnotations(true);
                    }

                    PushState();

                    ProcessOperations(operations);

                    PopState();

                    if (_renderAnnotations)
                    {
                        DrawAnnotations(false);
                    }

                    _canvas.Flush();

                    return recorder.EndRecording();
                }
            }
            finally
            {
                _paintCache.Dispose();
            }
        }

        public override void PopState()
        {
            base.PopState();
            _canvas.Restore();
            EndPath();
        }

        public override void PushState()
        {
            base.PushState();
            _canvas.Save();
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
            // Indicate that we want to update the original transform for form XObject
            _updateCurrentStreamOriginalTransform = true;

            base.ProcessFormXObject(formStream, xObjectName);

            // Restore previous original transform
            _currentStreamOriginalTransforms.Pop();
        }

        private bool _updateCurrentStreamOriginalTransform;
    }
}
