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
using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Functions;
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
        // For uncoloured patterns, the sub-processor's initial CTM is a pure translation
        // that aligns the pattern cell's lower-left BBox corner with picture origin (0, 0).
        // pattern.Matrix is intentionally NOT applied here (PdfBox does the same): the
        // pattern stream is rendered into a tile in pattern-local space, and pattern.Matrix
        // — which encodes pattern-to-user-space and may include rotations or sign flips
        // (e.g. /Matrix [-1 0 0 -1 0 0]) — would otherwise push the content outside the
        // recording rect.
        // Coloured patterns stay on the legacy path that passes pattern.Matrix, to preserve
        // existing visual baselines.

        return pattern.PaintType == PatternPaintType.Uncoloured
            ? TransformationMatrix.GetTranslationMatrix(-pattern.BBox.BottomLeft.X, -pattern.BBox.BottomLeft.Y)
            : pattern.Matrix;
    }

    internal static SKMatrix GetTilingPatterAdjMatrix(this TilingPatternColor pattern)
    {
        if (pattern.PaintType == PatternPaintType.Uncoloured)
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

        // We cancel CTM, but not canvas' Y flip, as we still need it.
        // We are drawing a SKPicture, we need to flip the Y axis of this picture.
        return SKMatrix.CreateScale(1, -1, 0, (float)pattern.BBox.Height / 2f);
    }

    /// <summary>
    /// Evaluates a shading's colour function(s) and remaps each output linearly from its
    /// declared Range to [0,1]. Functions without a Range entry fall back to plain [0,1]
    /// clamping, matching <see cref="Shading.Eval"/>'s behaviour. Handles both the
    /// "single n-out function" and "n separate 1-out functions" forms allowed by
    /// PDF 1.7 §8.7.4.5 for Type 1 / Type 4 / Type 6 / Type 7 shadings.
    /// </summary>
    internal static double[] EvalWithRangeRemap(this Shading shading, params double[] inputs)
    {
        // Bypass shading.Eval (which clamps to [0,1] without using Range) so we
        // can linearly remap each component from its declared Range to [0,1].
        // For compliant PDFs (Range=[0,1]) the remap is identity; for malformed
        // ones (e.g. Range=[-1,1] paired with DeviceRGB) the remap recovers the
        // intended gradient instead of clamping every negative output to black.

        PdfFunction[]? funcs = shading.Functions;
        if (funcs is null || funcs.Length == 0)
        {
            return inputs;
        }

        if (funcs.Length == 1)
        {
            double[] raw = funcs[0].Eval(inputs);
            for (int k = 0; k < raw.Length; k++)
            {
                raw[k] = RemapWithRange(raw[k], funcs[0], k);
            }
            return raw;
        }

        double[] result = new double[funcs.Length];
        for (int k = 0; k < funcs.Length; k++)
        {
            double v = funcs[k].Eval(inputs)[0];
            result[k] = RemapWithRange(v, funcs[k], 0);
        }
        return result;
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
}
