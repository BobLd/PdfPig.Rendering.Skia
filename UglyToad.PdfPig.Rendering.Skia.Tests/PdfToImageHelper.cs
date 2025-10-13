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
using SkiaSharp;
using UglyToad.PdfPig.Graphics.Colors;

namespace UglyToad.PdfPig.Rendering.Skia.Tests
{
    // https://github.com/apache/pdfbox/blob/trunk/pdfbox/src/test/java/org/apache/pdfbox/rendering/TestPDFToImage.java
    public static class PdfToImageHelper
    {
        private static SKBitmap createEmptyDiffImage(int minWidth, int minHeight, int maxWidth,
            int maxHeight)
        {
            SKBitmap bim3 = new SKBitmap(new SKImageInfo(maxWidth, maxHeight, SKColorType.Rgb888x));
            using (SKCanvas canvas = new SKCanvas(bim3))
            {
                if (minWidth != maxWidth || minHeight != maxHeight)
                {
                    canvas.Clear(SKColors.Black);
                    using (var paint = new SKPaint() { Color = SKColors.White })
                    {
                        canvas.DrawRect(0, 0, minWidth, minHeight, paint);
                    }

                    return bim3;
                }

                canvas.Clear(SKColors.White);
                return bim3;
            }
        }

        private const byte _threshold = 2;

        private static SKBitmap diffImages(SKBitmap bim1, SKBitmap bim2)
        {
            int minWidth = Math.Min(bim1.Width, bim2.Width);
            int minHeight = Math.Min(bim1.Height, bim2.Height);
            int maxWidth = Math.Max(bim1.Width, bim2.Width);
            int maxHeight = Math.Max(bim1.Height, bim2.Height);

            SKBitmap bim3 = null;
            if (minWidth != maxWidth || minHeight != maxHeight)
            {
                bim3 = createEmptyDiffImage(minWidth, minHeight, maxWidth, maxHeight);
            }

            for (int x = 0; x < minWidth; ++x)
            {
                for (int y = 0; y < minHeight; ++y)
                {
                    SKColor rgb1 = bim1.GetPixel(x, y);
                    SKColor rgb2 = bim2.GetPixel(x, y);
                    if (rgb1 != rgb2)
                    {
                        byte rDiff = (byte)Math.Abs(rgb1.Red - rgb2.Red);
                        byte gDiff = (byte)Math.Abs(rgb1.Green - rgb2.Green);
                        byte bDiff = (byte)Math.Abs(rgb1.Blue - rgb2.Blue);

                        if (rDiff <= _threshold && gDiff <= _threshold && bDiff <= _threshold)
                        {
                            // don't bother about small differences
                            continue;
                        }

                        if (bim3 is null)
                        {
                            bim3 = createEmptyDiffImage(minWidth, minHeight, maxWidth, maxHeight);
                        }

                        bim3.SetPixel(x, y, new SKColor(Math.Min((byte)125, rDiff), Math.Min((byte)125, gDiff), Math.Min((byte)125, bDiff)));
                    }
                    else
                    {
                        bim3?.SetPixel(x, y, SKColors.White);
                    }
                }
            }

            return bim3;
        }

        private static bool filesAreIdentical(SKBitmap left, SKBitmap right)
        {
            if (left is null || right is null)
            {
                return false;
            }

            ReadOnlySpan<byte> leftSpan = left.GetPixelSpan();
            ReadOnlySpan<byte> rightSpan = right.GetPixelSpan();
            return leftSpan.Length == rightSpan.Length && leftSpan.SequenceEqual(rightSpan);
        }

        private static string _errorFolder = "ErrorImages";

        public static bool TestResizeSinglePage(string pdfFile, int pageNumber, string expectedFile, int scale = 1)
        {
            string docPath = Path.Combine("Documents", pdfFile);
            string expectedImage = Path.Combine("ExpectedImages", expectedFile);

            using (SKBitmap expected = SKBitmap.Decode(expectedImage))
            {
                if (expected is null)
                {
                    throw new NullReferenceException("Could not load expected image.");
                }

                using (var document = PdfDocument.Open(docPath, SkiaRenderingParsingOptions.Instance))
                {
                    document.AddSkiaPageFactory();
                    using (var actual = document.GetPageAsSKBitmap(pageNumber, scale, RGBColor.White))
                    {
                        var skInfo = new SKImageInfo()
                        {
                            Height = (int)Math.Ceiling(actual.Height / (double)scale),
                            Width = (int)Math.Ceiling(actual.Width / (double)scale),
                            ColorType = actual.ColorType,
                            AlphaType = SKAlphaType.Premul
                        };

                        using (SKBitmap actualResize = new SKBitmap(skInfo))
                        {
                            if (!actual.ScalePixels(actualResize, SKFilterQuality.High))
                            {
                                throw new Exception("Unable to resize image.");
                            }

                            using (var expectedResize = new SKBitmap(skInfo))
                            {
                                if (!expected.ScalePixels(expectedResize, SKFilterQuality.High))
                                {
                                    throw new Exception("Unable to resize image.");
                                }

                                if (filesAreIdentical(expectedResize, actualResize))
                                {
                                    return true;
                                }

                                SKBitmap bim3 = diffImages(expectedResize, actualResize);
                                if (bim3 is null)
                                {
                                    return true;
                                }

                                // Save error
                                string rootName = expectedFile.Substring(0, expectedFile.Length - 4);
                                Directory.CreateDirectory(_errorFolder);
                                using (var fs = new FileStream(
                                           Path.Combine(_errorFolder, $"{rootName}_{pageNumber}_diff.png"),
                                           FileMode.Create))
                                {
                                    bim3.Encode(fs, SKEncodedImageFormat.Png, 100);
                                }
                            }
                        }

                        return false;
                    }
                }
            }
        }

        public static bool TestSinglePage(string pdfFile, int pageNumber, string expectedFile, int scale = 1)
        {
            string docPath = Path.Combine("Documents", pdfFile);
            string expectedImage = Path.Combine("ExpectedImages", expectedFile);

            using (SKBitmap expected = SKBitmap.Decode(expectedImage))
            {
                if (expected is null)
                {
                    throw new NullReferenceException("Could not load expected image.");
                }

                using (var document = PdfDocument.Open(docPath, SkiaRenderingParsingOptions.Instance))
                {
                    document.AddSkiaPageFactory();
                    using (var actual = document.GetPageAsSKBitmap(pageNumber, scale, RGBColor.White))
                    {
                        if (filesAreIdentical(expected, actual))
                        {
                            return true;
                        }

                        SKBitmap bim3 = diffImages(expected, actual);
                        if (bim3 is null)
                        {
                            return true;
                        }

                        // Save error
                        string rootName = expectedFile.Substring(0, expectedFile.Length - 4);

                        string errorToSaveFile = Path.Combine(_errorFolder, $"{rootName}_{pageNumber}_diff.png");

                        Directory.CreateDirectory(Path.GetDirectoryName(errorToSaveFile));
                        using (var fs = new FileStream(errorToSaveFile, FileMode.Create))
                        {
                            bim3.Encode(fs, SKEncodedImageFormat.Png, 100);
                        }

                        string renderToSaveFile = Path.Combine(_errorFolder, $"{rootName}_{pageNumber}_rendered.png");

                        Directory.CreateDirectory(Path.GetDirectoryName(renderToSaveFile));
                        using (var fs = new FileStream(renderToSaveFile, FileMode.Create))
                        {
                            actual.Encode(fs, SKEncodedImageFormat.Png, 100);
                        }

                        return false;
                    }
                }
            }
        }
    }
}
