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
using System.IO;
using System.Linq;
using System.Threading;
using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Functions;
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Rendering.Skia.Helpers;
using UglyToad.PdfPig.Tokenization.Scanner;
using UglyToad.PdfPig.Tokens;

namespace UglyToad.PdfPig.Rendering.Skia;

/// <summary>
/// Extension methods for PdfPig.
/// </summary>
public static class PdfPigExtensions
{
    /// <summary>
    /// Add the Skia page and the page size` factories.
    /// </summary>
    public static void AddSkiaPageFactory(this PdfDocument document)
    {
        document.AddPageFactory<PdfPageSize, PageSizeFactory>();
        document.AddPageFactory<SKPicture, SkiaPageFactory>();
    }

    /// <summary>
    /// Get the page size as defined in the document.
    /// <para>Lightweight alternative to <see cref="PdfDocument.GetPage"/> to get the page size.</para>
    /// </summary>
    /// <param name="document">The pdf document.</param>
    /// <param name="pageNumber">The number of the page to return, this starts from 1.</param>
    /// <returns></returns>
    public static PdfPageSize GetPageSize(this PdfDocument document, int pageNumber)
    {
        return document.GetPage<PdfPageSize>(pageNumber);
    }

    /// <summary>
    /// Get the pdf page as a <see cref="SKPicture"/>.
    /// <para>Do not relly on <see cref="SKPicture.CullRect"/> to get the page size. Use <see cref="GetPageSize"/> instead.</para>
    /// </summary>
    /// <param name="document">The pdf document.</param>
    /// <param name="pageNumber">The number of the page to return, this starts from 1.</param>
    /// <returns>The <see cref="SKPicture"/>.</returns>
    public static SKPicture GetPageAsSKPicture(this PdfDocument document, int pageNumber)
    {
        return document.GetPage<SKPicture>(pageNumber);
    }

    /// <summary>
    /// Get the pdf page as a <see cref="SKPicture"/>, observing a <see cref="CancellationToken"/>.
    /// <para>The token is checked every 100 content-stream operator (top-level page stream,
    /// form XObjects, soft masks, tiling patterns) and will surface an
    /// <see cref="OperationCanceledException"/> from the rendering loop.</para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method is <b>synchronous</b>, despite accepting a <see cref="CancellationToken"/>.
    /// It does not return a <see cref="System.Threading.Tasks.Task"/> and it blocks the
    /// calling thread for the entire duration of the render. The token does not make the
    /// call asynchronous — it only provides a way to interrupt the rendering loop in-flight.
    /// </para>
    /// <para>
    /// This method is <b>not thread-safe</b> with respect to a single <see cref="PdfDocument"/>.
    /// PdfPig's page factories share document-scoped state (token scanner, resource store,
    /// font cache, cross-reference table), and rendering mutates per-document graphics state.
    /// You must <b>not</b> render two pages of the same document in parallel, even from
    /// different threads, even with different cancellation tokens. Doing so will produce
    /// undefined behaviour ranging from corrupted output to access violations. Serialise
    /// per-document access with an external lock (e.g. <see cref="System.Threading.SemaphoreSlim"/>)
    /// if you have multiple concurrent callers. Rendering pages from <i>different</i>
    /// <see cref="PdfDocument"/> instances in parallel is safe.
    /// </para>
    /// <para>
    /// The cancellation token is propagated to the active rendering pass via an
    /// <see cref="System.Threading.AsyncLocal{T}"/> on <see cref="SkiaPageFactory"/>; it
    /// flows correctly across <c>await</c> boundaries within a single logical call but is
    /// scoped to the in-flight <see cref="PdfDocument.GetPage{TPage}(int)"/> invocation.
    /// </para>
    /// </remarks>
    /// <param name="document">The pdf document.</param>
    /// <param name="pageNumber">The number of the page to return, this starts from 1.</param>
    /// <param name="cancellationToken">Token to cancel the rendering pass.</param>
    /// <returns>The <see cref="SKPicture"/>.</returns>
    /// <exception cref="OperationCanceledException">
    /// Thrown when <paramref name="cancellationToken"/> is signalled while rendering is in progress.
    /// </exception>
    public static SKPicture GetPageAsSKPicture(this PdfDocument document, int pageNumber, CancellationToken cancellationToken)
    {
        CancellationToken previous = SkiaPageFactory.CurrentToken;
        SkiaPageFactory.CurrentToken = cancellationToken;
        try
        {
            return document.GetPage<SKPicture>(pageNumber);
        }
        finally
        {
            SkiaPageFactory.CurrentToken = previous;
        }
    }

    /// <summary>
    /// Get the pdf page as a <see cref="SKBitmap"/>.
    /// </summary>
    /// <param name="document">The pdf document.</param>
    /// <param name="pageNumber">The number of the page to return, this starts from 1.</param>
    /// <param name="scale">The scale factor to use when rendering the page.</param>
    /// <param name="clearColor">Optional background color to clear the canvas with before rendering. If null, the canvas is not cleared.</param>
    /// <returns>The <see cref="SKBitmap"/>.</returns>
    public static SKBitmap GetPageAsSKBitmap(this PdfDocument document, int pageNumber, float scale = 1, SKColor? clearColor = null)
    {
        using (var picture = document.GetPage<SKPicture>(pageNumber))
        {
            var page = document.GetPageSize(pageNumber);
            var size = new SKSizeI((int)Math.Ceiling(page.Width * scale), (int)Math.Ceiling(page.Height * scale));
            var scaleMatrix = SKMatrix.CreateScale(scale, scale);

            var bitmap = new SKBitmap(size.Width, size.Height);
            using (var canvas = new SKCanvas(bitmap))
            {
                if (clearColor.HasValue)
                {
                    canvas.Clear(clearColor.Value);
                }
                canvas.DrawPicture(picture, in scaleMatrix);
                return bitmap;
            }
        }
    }

    /// <summary>
    /// Get the pdf page as a Png stream.
    /// </summary>
    /// <param name="document">The pdf document.</param>
    /// <param name="pageNumber">The number of the page to return, this starts from 1.</param>
    /// <param name="scale">The scale factor to use when rendering the page.</param>
    /// <param name="quality">The Png quality.</param>
    /// <returns>The Png stream.</returns>
    public static MemoryStream GetPageAsPng(this PdfDocument document, int pageNumber, float scale = 1, int quality = 100)
    {
        var ms = new MemoryStream();
        using (var bitmap = document.GetPageAsSKBitmap(pageNumber, scale, SKColors.White))
        {
            bitmap.Encode(ms, SKEncodedImageFormat.Png, quality);
            ms.Position = 0;
            return ms;
        }
    }

    internal static ArrayToken ToArrayToken(this PdfRectangle rectangle)
    {
        return new ArrayToken(new[]
        {
                new NumericToken(rectangle.Left),
                new NumericToken(rectangle.Bottom),
                new NumericToken(rectangle.Right),
                new NumericToken(rectangle.Top)
            });
    }

    internal static ArrayToken ToArrayToken(this TransformationMatrix matrix)
    {
        return new ArrayToken(new[]
        {
                new NumericToken(matrix.A), new NumericToken(matrix.B), new NumericToken(matrix[0, 2]),
                new NumericToken(matrix.C), new NumericToken(matrix.D), new NumericToken(matrix[1, 2]),
                new NumericToken(matrix.E), new NumericToken(matrix.F), new NumericToken(matrix[2, 2]),
            });
    }

    internal static TransformationMatrix GetTilingPatterInitialMatrix(this TilingPatternColor pattern)
    {
        // The sub-processor's initial CTM is a pure translation that aligns the pattern
        // cell's lower-left BBox corner with the picture origin (0, 0). pattern.Matrix is
        // intentionally NOT applied here (PdfBox does the same): the pattern stream is
        // rendered into a tile in pattern-local space, and pattern.Matrix — which encodes
        // pattern-to-user-space and may scale the cell so a single tile covers the whole
        // page (e.g. full-size-image.pdf: BBox 942x626 with /Matrix [0.63 0 0 1.34 0 0]
        // tiles to 595x842) or include rotations/sign flips (e.g. /Matrix [-1 0 0 -1 0 0])
        // — would otherwise push the content outside the BBox-sized recording rect and
        // cause the shader's XStep/YStep tile area to slice the content into bands.
        // pattern.Matrix is reapplied in GetTilingPatterAdjMatrix as part of the shader's
        // local matrix.

        return TransformationMatrix.GetTranslationMatrix(-pattern.BBox.BottomLeft.X, -pattern.BBox.BottomLeft.Y);
    }

    internal static SKMatrix GetTilingPatterAdjMatrix(this TilingPatternColor pattern)
    {
        // The shader's local matrix maps PICTURE coords → user-space PDF coords (Skia
        // inverts it internally when sampling). The picture was drawn with
        // initialMatrix = Translate(-BBox.LowerLeft) plus the canvas' Y-flip (around
        // BBox.Height/2), so for picture (X, Y):
        //     pattern_local.x = X + LL.X
        //     pattern_local.y = UR.Y - Y                      (undo Y-flip + LL shift)
        //     user_space     = pattern.Matrix * pattern_local  (apply 8.7.2 alteration)
        // In matrix form: localMatrix = pattern.Matrix * Translate(LL.X, UR.Y) * Scale(1,-1).
        // Wrap that with inv(CTM) * orig so cm modifications and the parent stream's
        // initial transform stay consistent with the existing rendering pipeline.
        return pattern.Matrix.ToSkMatrix()
            .PreConcat(SKMatrix.CreateTranslation(
                (float)pattern.BBox.BottomLeft.X,
                (float)pattern.BBox.TopRight.Y))
            .PreConcat(SKMatrix.CreateScale(1, -1));
    }

    /// <summary>
    /// Evaluates a shading's colour function(s) and remaps each output linearly from its
    /// declared Range to [0,1]. Functions without a Range entry fall back to plain [0,1]
    /// clamping, matching <see cref="Shading.Eval"/>'s behaviour. Handles both the
    /// "single n-out function" and "n separate 1-out functions" forms allowed by
    /// PDF 1.7 §8.7.4.5 for Type 1 / Type 4 / Type 6 / Type 7 shadings.
    /// Allocation-free for the common single-function and multi-function fan-out forms.
    /// </summary>
    internal static int EvalWithRangeRemap(this Shading shading, ReadOnlySpan<double> inputs, Span<double> output)
    {
        // Bypass shading.Eval (which clamps to [0,1] without using Range) so we
        // can linearly remap each component from its declared Range to [0,1].
        // For compliant PDFs (Range=[0,1]) the remap is identity; for malformed
        // ones (e.g. Range=[-1,1] paired with DeviceRGB) the remap recovers the
        // intended gradient instead of clamping every negative output to black.

        PdfFunction[]? funcs = shading.Functions;
        if (funcs is null || funcs.Length == 0)
        {
            inputs.CopyTo(output);
            return inputs.Length;
        }

        if (funcs.Length == 1)
        {
            int written = funcs[0].Eval(inputs, output);
            for (int k = 0; k < written; k++)
            {
                output[k] = RemapWithRange(output[k], funcs[0], k);
            }

            return written;
        }

        // Multi-function fan-out: each function is 1-out, we keep only the first value of each.
        Span<double> buffer = stackalloc double[32];
        for (int k = 0; k < funcs.Length; k++)
        {
            int outLen = funcs[k].NumberOfOutputParameters;
            if (outLen <= 0)
            {
                outLen = 1;
            }

            if (outLen > buffer.Length)
            {
                double[] big = new double[outLen];
                funcs[k].Eval(inputs, big);
                output[k] = RemapWithRange(big[0], funcs[k], 0);
            }
            else
            {
                funcs[k].Eval(inputs, buffer);
                output[k] = RemapWithRange(buffer[0], funcs[k], 0);
            }
        }

        return funcs.Length;
    }

    private static double RemapWithRange(double value, PdfFunction func, int outputIndex)
    {
        if (func.NumberOfOutputParameters > outputIndex)
        {
            PdfRange range = func.GetRangeForOutput(outputIndex);
            double extent = range.Max - range.Min;
            if (extent > 0)
            {
                value = (value - range.Min) / extent;
            }
        }
        return value < 0 ? 0 : (value > 1 ? 1 : value);
    }

    internal static TransformationMatrix ReadFormMatrix(StreamToken formStream, IPdfTokenScanner scanner)
    {
        if (formStream.StreamDictionary.TryGet<ArrayToken>(NameToken.Matrix, scanner, out var token))
        {
            return TransformationMatrix.FromArray(
                token.Data.OfType<NumericToken>().Select(x => x.Double).ToArray());
        }
        return TransformationMatrix.Identity;
    }

    /// <summary>
    /// Resolves the back-drop colour used to seed the soft mask offscreen surface for
    /// /Luminosity masks. The PDF spec specifies the BC array in the group's colour space;
    /// we approximate by component count: 1 = DeviceGray, 3 = DeviceRGB, 4 = DeviceCMYK
    /// (converted to RGB so the subsequent <see cref="SKColorFilter.CreateLumaColor"/>
    /// derives the correct luminance). /Alpha masks ignore colour so we keep transparent
    /// (alpha 0) as the seed.
    /// </summary>
    internal static SKColor GetSoftMaskBackdrop(this SoftMask softMask)
    {
        if (softMask.Subtype != SoftMaskType.Luminosity)
        {
            return SKColors.Transparent;
        }

        double[]? bc = softMask.BC;
        if (bc is null || bc.Length == 0)
        {
            // Spec default: the colour space's initial value, representing black.
            return SKColors.Black;
        }

        if (bc.Length == 1)
        {
            return new GrayColor(bc[0]).ToSKColor();
        }

        if (bc.Length >= 4)
        {
            double c = bc[0], m = bc[1], y = bc[2], k = bc[3];
            return new CMYKColor(c, m, y, k).ToSKColor();
        }

        return new RGBColor(bc[0], bc[1], bc[2]).ToSKColor();
    }
}
