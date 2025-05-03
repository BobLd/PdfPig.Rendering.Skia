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
using System.IO;
using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Rendering.Skia.Helpers;
using UglyToad.PdfPig.Tokens;

namespace UglyToad.PdfPig.Rendering.Skia
{
    /// <summary>
    /// Extension methods for PdfPig.
    /// </summary>
    public static class PdfPigExtensions
    {
        /// <summary>
        /// Add the Skia page factory.
        /// </summary>
        public static void AddSkiaPageFactory(this PdfDocument document)
        {
            document.AddPageFactory<IAsyncEnumerable<SKPicture>, SkiaPageFactory>();
        }

        /// <summary>
        /// Get the pdf page as a <see cref="SKBitmap"/>.
        /// </summary>
        /// <param name="document">The pdf document.</param>
        /// <param name="pageNumber">The number of the page to return, this starts from 1.</param>
        /// <param name="scale">The scale factor to use when rendering the page.</param>
        /// <param name="background">The page background color to use when rendering the page. Pdf page have no default color so the background is transparent.</param>
        /// <returns>The <see cref="SKBitmap"/>.</returns>
        public static SKBitmap GetPageAsSKBitmap(this PdfDocument document, int pageNumber, float scale = 1, IColor background = null)
        {
            using (var picture = document.GetPage<SKPicture>(pageNumber))
            {
                var size = new SKSizeI((int)Math.Ceiling(picture.CullRect.Width * scale), (int)Math.Ceiling(picture.CullRect.Height * scale));
                var scaleMatrix = SKMatrix.CreateScale(scale, scale);

                var bitmap = new SKBitmap(size.Width, size.Height);
                using (var canvas = new SKCanvas(bitmap))
                {
                    if (background != null)
                    {
                        canvas.Clear(background.ToSKColor(1.0));
                    }

                    canvas.DrawPicture(picture, ref scaleMatrix);
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
        /// <param name="background">The page background color to use when rendering the page. Pdf page have no default color so the background is transparent.</param>
        /// <param name="quality">The Png quality.</param>
        /// <returns>The Png stream.</returns>
        public static MemoryStream GetPageAsPng(this PdfDocument document, int pageNumber, float scale = 1, IColor background = null, int quality = 100)
        {
            var ms = new MemoryStream();
            using (var bitmap = document.GetPageAsSKBitmap(pageNumber, scale, background))
            {
                bitmap.Encode(ms, SKEncodedImageFormat.Png, quality);
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
    }
}
