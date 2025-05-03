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
using SkiaSharp;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Geometry;
using UglyToad.PdfPig.Graphics.Operations;
using UglyToad.PdfPig.Outline.Destinations;
using UglyToad.PdfPig.Parser;
using UglyToad.PdfPig.Rendering.Skia.Helpers;
using UglyToad.PdfPig.Tokenization.Scanner;
using UglyToad.PdfPig.Tokens;

namespace UglyToad.PdfPig.Rendering.Skia
{
    /// <summary>
    /// The Skia page factory to render pages as images.
    /// </summary>
    public sealed class SkiaPageFactory : BasePageFactory<IAsyncEnumerable<SKPicture>>, IDisposable
    {
        private readonly SkiaFontCache _fontCache;

        /// <summary>
        /// <see cref="SkiaPageFactory"/> constructor.
        /// </summary>
        public SkiaPageFactory(
            IPdfTokenScanner pdfScanner,
            IResourceStore resourceStore,
            ILookupFilterProvider filterProvider,
            IPageContentParser pageContentParser,
            ParsingOptions parsingOptions)
            : base(pdfScanner, resourceStore, filterProvider, pageContentParser, parsingOptions)
        {
            _fontCache = new SkiaFontCache();
        }

        /// <inheritdoc/>
        protected override IAsyncEnumerable<SKPicture> ProcessPage(int pageNumber, DictionaryToken dictionary,
            NamedDestinations namedDestinations, MediaBox mediaBox, CropBox cropBox, UserSpaceUnit userSpaceUnit,
            PageRotationDegrees rotation, TransformationMatrix initialMatrix,
            IReadOnlyList<IGraphicsStateOperation> operations)
        {
            var annotationProvider = new AnnotationProvider(PdfScanner,
                dictionary,
                initialMatrix,
                namedDestinations,
                ParsingOptions.Logger);

            // Special case where cropbox is outside mediabox: use cropbox instead of intersection
            var effectiveCropBox = new CropBox(mediaBox.Bounds.Intersect(cropBox.Bounds) ?? cropBox.Bounds);

            var context = new SkiaStreamProcessor(pageNumber, ResourceStore, PdfScanner, PageContentParser,
                FilterProvider, effectiveCropBox, userSpaceUnit, rotation, initialMatrix, ParsingOptions,
                annotationProvider, _fontCache);

            return context.Process(pageNumber, operations);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _fontCache.Dispose();
        }
    }
}
