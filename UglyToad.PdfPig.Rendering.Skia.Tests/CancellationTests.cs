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
using System.IO;
using System.Text;
using System.Threading;
using SkiaSharp;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokens;
using Xunit;

namespace UglyToad.PdfPig.Rendering.Skia.Tests;

public class CancellationTests
{
    [Fact]
    public void PageTimedOut()
    {
        using (var document = PdfDocument.Open(Path.Combine(Helper.SpecificTestDocumentsFolder, "map.pdf"), SkiaRenderingParsingOptions.Instance))
        {
            document.AddSkiaPageFactory();

            // Get page but tine out
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                // Page 10 will run (virtually) forever
                Assert.Throws<OperationCanceledException>(() =>
                {
                    document.GetPageAsSKPicture(10, cts.Token);
                });
            }

            // Get page
            using (var cts = new CancellationTokenSource())
            {
                var picture = document.GetPageAsSKPicture(1, cts.Token);
                Assert.NotNull(picture);
                Assert.True(picture.ApproximateOperationCount > 0);
            }
        }
    }

    [Fact]
    public void CancelBeforeStart()
    {
        using (var document = PdfDocument.Open(Path.Combine(Helper.SpecificTestDocumentsFolder, "new.pdf"), SkiaRenderingParsingOptions.Instance))
        {
            document.AddSkiaPageFactory();
            
            // Get page but cancel
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                Assert.Throws<OperationCanceledException>(() =>
                {
                    document.GetPageAsSKPicture(1, cts.Token);
                });
            }

            // Get page
            using (var cts = new CancellationTokenSource())
            {
                var picture = document.GetPageAsSKPicture(1, cts.Token);
                Assert.NotNull(picture);
                Assert.True(picture.ApproximateOperationCount > 0);
            }
        }
    }

    [Fact]
    public void CancelWhileDrawingAnnotations()
    {
        // Annotations are drawn after the page content, each in its own try/catch. A cancellation
        // there must still fail the render: the picture would otherwise lack the annotations and
        // a caller caching it would keep the incomplete page.
        byte[] pdf = CreatePageWithAnnotation();

        // Control: without cancellation the annotation (red square) is drawn.
        using (var document = PdfDocument.Open(pdf, SkiaRenderingParsingOptions.Instance))
        {
            document.AddSkiaPageFactory();
            using (var cts = new CancellationTokenSource())
            using (var picture = document.GetPageAsSKPicture(1, cts.Token))
            using (var bitmap = new SKBitmap(200, 200))
            {
                using (var canvas = new SKCanvas(bitmap))
                {
                    canvas.Clear(SKColors.White);
                    canvas.DrawPicture(picture);
                }

                Assert.Equal(SKColors.Red, bitmap.GetPixel(145, 55));
            }
        }

        // The token is cancelled when the annotation's appearance stream, the only stream using
        // ASCIIHexDecode, is decoded: after the page content, while drawing annotations.
        using (var cts = new CancellationTokenSource())
        {
            var options = new ParsingOptions
            {
                UseLenientParsing = true,
                SkipMissingFonts = true,
                FilterProvider = new CancelOnFilterProvider(SkiaRenderingFilterProvider.Instance, NameToken.AsciiHexDecode, cts)
            };

            using (var document = PdfDocument.Open(pdf, options))
            {
                document.AddSkiaPageFactory();
                Assert.Throws<OperationCanceledException>(() =>
                {
                    document.GetPageAsSKPicture(1, cts.Token);
                });
                Assert.True(cts.IsCancellationRequested);
            }
        }
    }

    /// <summary>
    /// One 200 x 200 page: a blue square as page content, and a Square annotation whose
    /// appearance stream paints a red square in [100 100 190 190], hex-encoded.
    /// </summary>
    private static byte[] CreatePageWithAnnotation()
    {
        const string content = "0 0 1 rg 10 10 50 50 re f";
        string appearance = ToHex("1 0 0 rg 0 0 90 90 re f") + ">";

        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Annots [5 0 R] >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Annot /Subtype /Square /Rect [100 100 190 190] /AP << /N 6 0 R >> >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 90 90] /Filter /ASCIIHexDecode /Length {appearance.Length} >>\nstream\n{appearance}\nendstream"
        ];

        return BuildPdf(objects);
    }

    [Fact]
    public void CancelWhileDrawingImageUnderSoftMask()
    {
        // An image drawn under a soft mask renders the mask's group first; a cancellation there
        // must fail the render, not be logged as an image that failed to draw.
        byte[] pdf = CreatePageWithSoftMaskedImage();

        // Control: without cancellation the image (red) is drawn through the mask.
        using (var document = PdfDocument.Open(pdf, SkiaRenderingParsingOptions.Instance))
        {
            document.AddSkiaPageFactory();
            using (var cts = new CancellationTokenSource())
            using (var picture = document.GetPageAsSKPicture(1, cts.Token))
            using (var bitmap = new SKBitmap(200, 200))
            {
                using (var canvas = new SKCanvas(bitmap))
                {
                    canvas.Clear(SKColors.White);
                    canvas.DrawPicture(picture);
                }

                SKColor pixel = bitmap.GetPixel(100, 100);
                Assert.True(pixel.Red > 200 && pixel.Green < 50 && pixel.Blue < 50, $"Expected red, got {pixel}.");
            }
        }

        // The token is cancelled when the mask's group, the only stream using ASCIIHexDecode,
        // is decoded: while drawing the image.
        using (var cts = new CancellationTokenSource())
        {
            var options = new ParsingOptions
            {
                UseLenientParsing = true,
                SkipMissingFonts = true,
                FilterProvider = new CancelOnFilterProvider(SkiaRenderingFilterProvider.Instance, NameToken.AsciiHexDecode, cts)
            };

            using (var document = PdfDocument.Open(pdf, options))
            {
                document.AddSkiaPageFactory();
                Assert.Throws<OperationCanceledException>(() =>
                {
                    document.GetPageAsSKPicture(1, cts.Token);
                });
                Assert.True(cts.IsCancellationRequested);
            }
        }
    }

    /// <summary>
    /// One 200 x 200 page drawing a 1 x 1 red image over [50 50 150 150] under a luminosity soft
    /// mask whose group, hex-encoded, paints the whole page white (the image shows through).
    /// </summary>
    private static byte[] CreatePageWithSoftMaskedImage()
    {
        const string content = "q /GS1 gs 100 0 0 100 50 50 cm /Im1 Do Q";
        string group = ToHex("1 g 0 0 200 200 re f") + ">";

        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /ExtGState << /GS1 5 0 R >> /XObject << /Im1 6 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /ExtGState /SMask << /Type /Mask /S /Luminosity /G 7 0 R >> >>",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 3 >>\nstream\nÿ\u0000\u0000\nendstream",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /CS /DeviceRGB >> /Filter /ASCIIHexDecode /Length {group.Length} >>\nstream\n{group}\nendstream"
        ];

        return BuildPdf(objects);
    }

    [Fact]
    public void CancelWhileBuildingType3GlyphDoesNotBreakLaterRenders()
    {
        // Type 3 glyphs are built once and cached for the whole document. A coloured (d0) glyph is
        // built by recording its procedure into a picture, which observes the render's token. A
        // render cancelled during that build must not leave its cancellation in the cache: every
        // later render of the document would rethrow it, under a token that was never cancelled.
        byte[] pdf = CreatePageWithType3Glyph();

        // Control: without cancellation the glyph (red square) is drawn. The glyph procedure is the
        // only stream using ASCIIHexDecode; count how often a render decodes it.
        int decodes;
        using (var document = PdfDocument.Open(pdf, CancelOnFilterOptions(null, 0, out var counter)))
        {
            document.AddSkiaPageFactory();
            using (var cts = new CancellationTokenSource())
            using (var picture = document.GetPageAsSKPicture(1, cts.Token))
            {
                AssertRedSquare(picture);
            }

            decodes = counter.Count;
        }

        // The procedure is decoded more than once per render (its bounding box is read before the
        // glyph is built), so cancel at each decode in turn.
        Assert.True(decodes > 0);
        for (int i = 1; i <= decodes; i++)
        {
            using (var cts = new CancellationTokenSource())
            using (var document = PdfDocument.Open(pdf, CancelOnFilterOptions(cts, i, out _)))
            {
                document.AddSkiaPageFactory();
                Assert.Throws<OperationCanceledException>(() =>
                {
                    document.GetPageAsSKPicture(1, cts.Token);
                });
                Assert.True(cts.IsCancellationRequested);

                // Same document, a token that is never cancelled.
                using (var live = new CancellationTokenSource())
                using (var picture = document.GetPageAsSKPicture(1, live.Token))
                {
                    AssertRedSquare(picture);
                }
            }
        }
    }

    private static ParsingOptions CancelOnFilterOptions(CancellationTokenSource? cts, int occurrence, out CancelOnFilterProvider provider)
    {
        provider = new CancelOnFilterProvider(SkiaRenderingFilterProvider.Instance, NameToken.AsciiHexDecode, cts, occurrence);
        return new ParsingOptions
        {
            UseLenientParsing = true,
            SkipMissingFonts = true,
            FilterProvider = provider
        };
    }

    private static void AssertRedSquare(SKPicture picture)
    {
        using (var bitmap = new SKBitmap(200, 200))
        {
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.White);
                canvas.DrawPicture(picture);
            }

            Assert.Equal(SKColors.Red, bitmap.GetPixel(100, 100));
        }
    }

    /// <summary>
    /// One 200 x 200 page showing one glyph of a Type 3 font. The glyph is coloured (d0) and paints
    /// a red square over [50 50 150 150]; its procedure is hex-encoded.
    /// </summary>
    private static byte[] CreatePageWithType3Glyph()
    {
        const string content = "BT /F1 100 Tf 50 50 Td (a) Tj ET";
        string glyph = ToHex("100 0 d0 1 0 0 rg 0 0 100 100 re f") + ">";

        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 100 100] /FontMatrix [0.01 0 0 0.01 0 0] /CharProcs << /a 6 0 R >> /Encoding << /Type /Encoding /Differences [97 /a] >> /FirstChar 97 /LastChar 97 /Widths [100] >>",
            $"<< /Filter /ASCIIHexDecode /Length {glyph.Length} >>\nstream\n{glyph}\nendstream"
        ];

        return BuildPdf(objects);
    }

    /// <summary>
    /// Writes the objects (numbered from 1, the first being the catalog) as a PDF with a
    /// cross-reference table. Characters are written as single bytes (Latin-1).
    /// </summary>
    private static byte[] BuildPdf(string[] objects)
    {
        var builder = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = builder.Length;
            builder.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = builder.Length;
        builder.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            builder.Append($"{offset:D10} 00000 n \n");
        }

        builder.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        string text = builder.ToString();
        var bytes = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            bytes[i] = (byte)text[i];
        }

        return bytes;
    }

    private static string ToHex(string text)
    {
        var builder = new StringBuilder(text.Length * 2);
        foreach (byte b in Encoding.ASCII.GetBytes(text))
        {
            builder.Append(b.ToString("X2"));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Cancels <paramref name="cts"/> when a stream using <paramref name="filter"/> is decoded, or only
    /// at the <paramref name="occurrence"/>-th such decode when it is greater than 0.
    /// </summary>
    private sealed class CancelOnFilterProvider(IFilterProvider inner, NameToken filter, CancellationTokenSource? cts, int occurrence = 0) : IFilterProvider
    {
        /// <summary>
        /// The number of decodes of a stream using the filter so far.
        /// </summary>
        public int Count { get; private set; }

        public IReadOnlyList<IFilter> GetFilters(DictionaryToken dictionary) => inner.GetFilters(dictionary);

        public IReadOnlyList<IFilter> GetNamedFilters(IReadOnlyList<NameToken> names)
        {
            foreach (NameToken name in names)
            {
                if (name.Equals(filter))
                {
                    Count++;
                    if (occurrence <= 0 || Count == occurrence)
                    {
                        cts?.Cancel();
                    }
                }
            }

            return inner.GetNamedFilters(names);
        }

        public IReadOnlyList<IFilter> GetAllFilters() => inner.GetAllFilters();
    }
}