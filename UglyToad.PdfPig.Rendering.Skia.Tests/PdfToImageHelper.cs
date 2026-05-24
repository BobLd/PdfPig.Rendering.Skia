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
using System.Runtime.InteropServices;
using SkiaSharp;

namespace UglyToad.PdfPig.Rendering.Skia.Tests
{
    // https://github.com/apache/pdfbox/blob/trunk/pdfbox/src/test/java/org/apache/pdfbox/rendering/TestPDFToImage.java
    public static class PdfToImageHelper
    {
        private const string _expectedImagesFolder = "ExpectedImages";

        /// <summary>
        /// Resolves the expected image to compare against. Rendering differs slightly across
        /// operating systems (font hinting, available fonts), so a per-OS override may exist under
        /// <c>ExpectedImages/&lt;os&gt;/</c>. If found it is used; otherwise the cross-platform
        /// default under <c>ExpectedImages/</c> is used.
        /// </summary>
        private static string resolveExpectedImagePath(string expectedFile)
        {
            string? os = currentOsFolder();
            if (os is not null)
            {
                string platformSpecific = Path.Combine(_expectedImagesFolder, os, expectedFile);
                if (File.Exists(platformSpecific))
                {
                    return platformSpecific;
                }
            }

            return Path.Combine(_expectedImagesFolder, expectedFile);
        }

        // arm64 and x64 renders are treated as identical, so the folder is keyed by OS only.
        private static string? currentOsFolder()
        {
#if NET
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return "linux";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return "macos";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return "windows";
            }

            return null;
#else
            return "windows";
#endif
        }
        private static SKBitmap createEmptyDiffImage(int minWidth, int minHeight, int maxWidth,
            int maxHeight)
        {
            var bim3 = new SKBitmap(new SKImageInfo(maxWidth, maxHeight, SKColorType.Rgb888x));
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

        // Maximum fraction of the overlapping pixels allowed to differ beyond the per-channel
        // threshold before a comparison fails. Absorbs anti-aliasing / sub-pixel rasterization
        // deltas (which vary slightly across platforms and font versions) without masking real
        // rendering regressions. Tunable per call via the optional parameter on the Test* methods.
        private const double _maxDifferingPixelRatio = 0.001;

        private readonly struct DiffResult
        {
            public DiffResult(SKBitmap? diffImage, long differingPixels, long comparedPixels, bool dimensionsMismatch)
            {
                DiffImage = diffImage;
                DifferingPixels = differingPixels;
                ComparedPixels = comparedPixels;
                DimensionsMismatch = dimensionsMismatch;
            }

            public SKBitmap? DiffImage { get; }

            public long DifferingPixels { get; }

            public long ComparedPixels { get; }

            public bool DimensionsMismatch { get; }

            public double DifferingRatio => ComparedPixels == 0L ? 0d : (double)DifferingPixels / ComparedPixels;
        }

        private static bool isWithinTolerance(in DiffResult diff, double maxDifferingPixelRatio)
        {
            // A different output size is a structural difference, not an anti-aliasing artifact.
            if (diff.DimensionsMismatch)
            {
                return false;
            }

            return diff.DifferingRatio <= maxDifferingPixelRatio;
        }

        /// <summary>
        /// Returns the byte offsets of the R, G, B channels within a pixel for the given color type.
        /// Byte order follows the naming convention: e.g. Bgra8888 → B=0, G=1, R=2, A=3.
        /// </summary>
        private static (int r, int g, int b) GetRgbChannelOffsets(SKColorType colorType) => colorType switch
        {
            SKColorType.Bgra8888 => (2, 1, 0),
            SKColorType.Rgba8888 => (0, 1, 2),
            SKColorType.Rgb888x  => (0, 1, 2),
            _ => throw new NotSupportedException($"Color type {colorType} is not supported for pixel diffing.")
        };

        private static DiffResult diffImages(SKBitmap bim1, SKBitmap bim2)
        {
            int minWidth = Math.Min(bim1.Width, bim2.Width);
            int minHeight = Math.Min(bim1.Height, bim2.Height);
            int maxWidth = Math.Max(bim1.Width, bim2.Width);
            int maxHeight = Math.Max(bim1.Height, bim2.Height);

            bool dimensionsMismatch = minWidth != maxWidth || minHeight != maxHeight;

            // bim3 is always Rgb888x: memory layout is [R=0, G=1, B=2, X=3].
            // createEmptyDiffImage pre-fills the overlap area with white, so equal
            // pixels don't need to be written explicitly.
            SKBitmap? bim3 = null;
            Span<byte> span3 = default;
            int rowBytes3 = 0;
            int bpp3 = 0;

            if (dimensionsMismatch)
            {
                bim3 = createEmptyDiffImage(minWidth, minHeight, maxWidth, maxHeight);
                span3 = bim3.GetPixelSpan();
                rowBytes3 = bim3.RowBytes;
                bpp3 = bim3.BytesPerPixel;
            }

            Span<byte> span1 = bim1.GetPixelSpan();
            Span<byte> span2 = bim2.GetPixelSpan();

            int bpp1 = bim1.BytesPerPixel;
            int bpp2 = bim2.BytesPerPixel;
            int rowBytes1 = bim1.RowBytes;
            int rowBytes2 = bim2.RowBytes;

            (int rOff1, int gOff1, int bOff1) = GetRgbChannelOffsets(bim1.ColorType);
            (int rOff2, int gOff2, int bOff2) = GetRgbChannelOffsets(bim2.ColorType);

            var sameColorTypes = bim1.ColorType == bim2.ColorType;

            long differingPixels = 0L;
            long comparedPixels = (long)minWidth * minHeight;

            for (int y = 0; y < minHeight; ++y)
            {
                for (int x = 0; x < minWidth; ++x)
                {
                    int idx1 = y * rowBytes1 + x * bpp1;
                    int idx2 = y * rowBytes2 + x * bpp2;

                    // Quick equality short-circuit when both bitmaps share the same color type
                    if (sameColorTypes && span1.Slice(idx1, bpp1).SequenceEqual(span2.Slice(idx2, bpp1)))
                    {
                        continue;
                    }

                    byte rDiff = (byte)Math.Abs(span1[idx1 + rOff1] - span2[idx2 + rOff2]);
                    byte gDiff = (byte)Math.Abs(span1[idx1 + gOff1] - span2[idx2 + gOff2]);
                    byte bDiff = (byte)Math.Abs(span1[idx1 + bOff1] - span2[idx2 + bOff2]);

                    if (rDiff <= _threshold && gDiff <= _threshold && bDiff <= _threshold)
                    {
                        // don't bother about small differences
                        continue;
                    }

                    differingPixels++;

                    if (bim3 is null)
                    {
                        bim3 = createEmptyDiffImage(minWidth, minHeight, maxWidth, maxHeight);
                        span3 = bim3.GetPixelSpan();
                        rowBytes3 = bim3.RowBytes;
                        bpp3 = bim3.BytesPerPixel;
                    }

                    int idx3 = y * rowBytes3 + x * bpp3;
                    span3[idx3]     = Math.Min((byte)125, rDiff);
                    span3[idx3 + 1] = Math.Min((byte)125, gDiff);
                    span3[idx3 + 2] = Math.Min((byte)125, bDiff);
                }
            }

            return new DiffResult(bim3, differingPixels, comparedPixels, dimensionsMismatch);
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

        private static readonly string _errorFolder = "ErrorImages";

        public static bool TestResizeSinglePage(string pdfFile, int pageNumber, string expectedFile, int scale = 1,
            double maxDifferingPixelRatio = _maxDifferingPixelRatio)
        {
            string docPath = Path.Combine("Documents", pdfFile);
            string expectedImage = resolveExpectedImagePath(expectedFile);

            using (SKBitmap expected = SKBitmap.Decode(expectedImage))
            {
                if (expected is null)
                {
                    throw new NullReferenceException("Could not load expected image.");
                }
                
                var samplingOptions = new SKSamplingOptions(SKCubicResampler.Mitchell);
                
                using (var document = PdfDocument.Open(docPath, SkiaRenderingParsingOptions.Instance))
                {
                    document.AddSkiaPageFactory();
                    using (var actual = document.GetPageAsSKBitmap(pageNumber, scale, SKColors.White))
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
                            if (!actual.ScalePixels(actualResize, samplingOptions))
                            {
                                throw new Exception("Unable to resize image.");
                            }

                            using (var expectedResize = new SKBitmap(skInfo))
                            {
                                if (!expected.ScalePixels(expectedResize, samplingOptions))
                                {
                                    throw new Exception("Unable to resize image.");
                                }

                                if (filesAreIdentical(expectedResize, actualResize))
                                {
                                    return true;
                                }

                                DiffResult diff = diffImages(expectedResize, actualResize);
                                using SKBitmap? bim3 = diff.DiffImage;

                                if (isWithinTolerance(diff, maxDifferingPixelRatio))
                                {
                                    return true;
                                }

                                Console.WriteLine(
                                    $"{expectedFile} (page {pageNumber}): {diff.DifferingPixels} of {diff.ComparedPixels} pixels differ ({diff.DifferingRatio:P3}), tolerance {maxDifferingPixelRatio:P3}.");

                                // Save error
                                string rootName = expectedFile.Substring(0, expectedFile.Length - 4);
                                Directory.CreateDirectory(_errorFolder);
                                using (var fs = new FileStream(
                                           Path.Combine(_errorFolder, $"{rootName}_{pageNumber}_diff.png"),
                                           FileMode.Create))
                                {
                                    bim3?.Encode(fs, SKEncodedImageFormat.Png, 100);
                                }
                            }
                        }

                        return false;
                    }
                }
            }
        }

        public static bool TestSinglePage(string pdfFile, int pageNumber, string expectedFile, int scale = 1,
            double maxDifferingPixelRatio = _maxDifferingPixelRatio)
        {
            string docPath = Path.Combine("Documents", pdfFile);
            string expectedImage = resolveExpectedImagePath(expectedFile);

            using (SKBitmap expected = SKBitmap.Decode(expectedImage))
            {
                if (expected is null)
                {
                    throw new NullReferenceException("Could not load expected image.");
                }

                using (var document = PdfDocument.Open(docPath, SkiaRenderingParsingOptions.Instance))
                {
                    document.AddSkiaPageFactory();
                    using (var actual = document.GetPageAsSKBitmap(pageNumber, scale, SKColors.White))
                    {
                        if (filesAreIdentical(expected, actual))
                        {
                            return true;
                        }

                        DiffResult diff = diffImages(expected, actual);
                        SKBitmap? bim3 = diff.DiffImage;

                        if (isWithinTolerance(diff, maxDifferingPixelRatio))
                        {
                            bim3?.Dispose();
                            return true;
                        }

                        Console.WriteLine(
                            $"{expectedFile} (page {pageNumber}): {diff.DifferingPixels} of {diff.ComparedPixels} pixels differ ({diff.DifferingRatio:P3}), tolerance {maxDifferingPixelRatio:P3}.");

                        // Save error
                        string rootName = expectedFile.Substring(0, expectedFile.Length - 4);

                        string errorToSaveFile = Path.Combine(_errorFolder, $"{rootName}_diff.png");

                        Directory.CreateDirectory(Path.GetDirectoryName(errorToSaveFile));
                        using (var fs = new FileStream(errorToSaveFile, FileMode.Create))
                        {
                            bim3?.Encode(fs, SKEncodedImageFormat.Png, 100);
                        }

                        bim3?.Dispose();

                        string renderToSaveFile = Path.Combine(_errorFolder, $"{rootName}_rendered.png");

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
