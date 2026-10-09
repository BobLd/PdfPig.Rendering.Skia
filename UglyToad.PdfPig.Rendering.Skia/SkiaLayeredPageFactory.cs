// Copyright BobLd
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
using System.Threading;
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

namespace UglyToad.PdfPig.Rendering.Skia;

/// <summary>
/// The Skia page factory to record pages as <see cref="SkiaLayeredPage"/>s: optional content (layers) kept apart,
/// so the page can be composed for any visibility state without being processed again.
/// </summary>
public sealed class SkiaLayeredPageFactory : BasePageFactory<SkiaLayeredPage>, IDisposable
{
    private readonly SkiaFontCache _fontCache;

    /// <summary>
    /// <see cref="SkiaLayeredPageFactory"/> constructor.
    /// </summary>
    public SkiaLayeredPageFactory(
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
    protected override SkiaLayeredPage ProcessPage(int pageNumber, DictionaryToken dictionary,
        NamedDestinations namedDestinations, MediaBox mediaBox, CropBox cropBox, UserSpaceUnit userSpaceUnit,
        PageRotationDegrees rotation, TransformationMatrix initialMatrix,
        IReadOnlyList<IGraphicsStateOperation> operations)
    {
        var annotationProvider = new AnnotationProvider(PdfScanner,
            dictionary,
            initialMatrix,
            namedDestinations,
            ParsingOptions.Logger);

        // Keep the font cache alive while this page records: Dispose() waits for in-flight
        // recordings to finish before tearing down the native resources they are drawing with.
        using (_fontCache.BeginUse())
        {
            var context = new SkiaStreamProcessor(pageNumber, ResourceStore, PdfScanner, PageContentParser,
                FilterProvider, cropBox, userSpaceUnit, rotation, initialMatrix, annotationProvider,
                _fontCache, ResourceStore.GetPageOutputIntentProfile(dictionary), ParsingOptions,
                optionalContentVisibility: null, layered: true, SkiaPageFactory.CurrentToken);

            return context.ProcessLayered(pageNumber, operations);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _fontCache.Dispose();
    }
}
