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

        private readonly FontCache _fontCache;
        private readonly SKPaintCache _paintCache = new SKPaintCache(_antiAliasing, _minimumLineWidth);

        private readonly AnnotationProvider _annotationProvider;

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
            AnnotationProvider annotationProvider,
            FontCache fontCache)
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
            _annotationProvider = annotationProvider;

            _annotations = new Lazy<Annotation[]>(() => _annotationProvider.GetAnnotations().ToArray());

            _fontCache = fontCache;

            _width = (float)cropBox.Bounds.Width;
            _height = (float)cropBox.Bounds.Height;

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
                    if (_renderAnnotations)
                    {
                        DrawAnnotations(true);
                    }

                    ProcessOperations(operations);

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
        }

        public override void PushState()
        {
            base.PushState();
            _canvas.Save();
        }

        public override void ModifyClippingIntersect(FillingRule clippingRule)
        {
            if (_currentPath == null)
            {
                return;
            }

            _currentPath.FillType = clippingRule.ToSKPathFillType();
            _canvas.ClipPath(_currentPath, SKClipOperation.Intersect);
        }

        /// <inheritdoc/>
        public override void BeginMarkedContent(NameToken name, NameToken propertyDictionaryName, DictionaryToken properties)
        {
            // No op
        }

        /// <inheritdoc/>
        public override void EndMarkedContent()
        {
            // No op
        }

        private float GetScaledLineWidth()
        {
            // https://stackoverflow.com/questions/25690496/how-does-pdf-line-width-interact-with-the-ctm-in-both-horizontal-and-vertical-di
            // TODO - a hack but works so far

            var currentState = GetCurrentState();

            /*
            double A = currentState.CurrentTransformationMatrix.A;
            double B = currentState.CurrentTransformationMatrix.B;
            double C = currentState.CurrentTransformationMatrix.C;
            double D = currentState.CurrentTransformationMatrix.D;

            double x = -(double)currentState.LineWidth/2;
            double y = -x/2;

            double scaleX = A * x + C * y;
            double scaleY = B * x + D * y;

            return (float)Math.Sqrt(scaleX * scaleX + scaleY * scaleY);
            */

            return (float)(currentState.LineWidth * currentState.CurrentTransformationMatrix.A);
        }
    }
}
