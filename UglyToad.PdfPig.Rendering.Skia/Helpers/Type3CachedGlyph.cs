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
using SkiaSharp;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Graphics.Operations;
using UglyToad.PdfPig.Logging;
using UglyToad.PdfPig.Parser;
using UglyToad.PdfPig.PdfFonts;
using UglyToad.PdfPig.Tokenization.Scanner;

namespace UglyToad.PdfPig.Rendering.Skia.Helpers
{
    /// <summary>
    /// Cached representation of a Type 3 glyph. One of <see cref="Type3VectorGlyph"/>,
    /// <see cref="Type3PictureGlyph"/>, or <see cref="Type3MissingGlyph"/>.
    /// </summary>
    internal abstract class Type3CachedGlyph : IDisposable
    {
        public abstract void Dispose();
    }

    /// <summary>
    /// A d1 (uncoloured / stencil) Type 3 glyph whose CharProc contains only path-construction
    /// operators. The geometry is cached; current text colour is applied at draw time by routing
    /// through <c>ShowVectorFontGlyph</c>.
    /// </summary>
    internal sealed class Type3VectorGlyph : Type3CachedGlyph
    {
        public SKPath Path { get; }

        public Type3VectorGlyph(SKPath path)
        {
            Path = path;
        }

        public override void Dispose() => Path.Dispose();
    }

    /// <summary>
    /// A d0 (coloured) or bitmap-bearing Type 3 glyph. The CharProc was recorded into an
    /// <see cref="SKPicture"/> with all paints baked in; current text colour is irrelevant.
    /// </summary>
    internal sealed class Type3PictureGlyph : Type3CachedGlyph
    {
        public SKPicture Picture { get; }

        public Type3PictureGlyph(SKPicture picture)
        {
            Picture = picture;
        }

        public override void Dispose() => Picture.Dispose();
    }

    /// <summary>
    /// Sentinel for character codes whose CharProc could not be resolved on the font.
    /// </summary>
    internal sealed class Type3MissingGlyph : Type3CachedGlyph
    {
        public static Type3MissingGlyph Instance { get; } = new Type3MissingGlyph();

        private Type3MissingGlyph() { }

        public override void Dispose() { }
    }

    /// <summary>
    /// Inputs the Type 3 cache build path needs from the calling <c>SkiaStreamProcessor</c>.
    /// The <see cref="RecordPicture"/> callback exists because recording must drive the live
    /// stream processor (it understands every PDF operator) against a swapped canvas.
    /// </summary>
    internal readonly struct Type3BuildContext
    {
        public ILookupFilterProvider FilterProvider { get; }
        public IPdfTokenScanner PdfScanner { get; }
        public IPageContentParser PageContentParser { get; }
        public int PageNumber { get; }
        public ILog? Logger { get; }
        public Func<IReadOnlyList<IGraphicsStateOperation>, IType3Font, int, SKPicture> RecordPicture { get; }

        public Type3BuildContext(
            ILookupFilterProvider filterProvider,
            IPdfTokenScanner pdfScanner,
            IPageContentParser pageContentParser,
            int pageNumber,
            ILog? logger,
            Func<IReadOnlyList<IGraphicsStateOperation>, IType3Font, int, SKPicture> recordPicture)
        {
            FilterProvider = filterProvider;
            PdfScanner = pdfScanner;
            PageContentParser = pageContentParser;
            PageNumber = pageNumber;
            Logger = logger;
            RecordPicture = recordPicture;
        }
    }
}
