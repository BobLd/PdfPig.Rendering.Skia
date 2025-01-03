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
using System.Runtime.InteropServices;
using SkiaSharp;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Images;

namespace UglyToad.PdfPig.Rendering.Skia.Helpers
{
    internal static class SkiaImageExtensions
    {
        // https://stackoverflow.com/questions/50312937/skiasharp-tiff-support#50370515
        private static bool TryGenerate(this IPdfImage image, out SKImage bitmap)
        {
            bitmap = null;

            var hasValidDetails = image.ColorSpaceDetails != null && !(image.ColorSpaceDetails is UnsupportedColorSpaceDetails);

            var isColorSpaceSupported = hasValidDetails && image.ColorSpaceDetails!.BaseType != ColorSpace.Pattern;

            if (!isColorSpaceSupported || !image.TryGetBytesAsMemory(out var imageMemory))
            {
                return false;
            }

            var bytesPure = imageMemory.Span;

            try
            {
                bytesPure = ColorSpaceDetailsByteConverter.Convert(image.ColorSpaceDetails!, bytesPure,
                    image.BitsPerComponent, image.WidthInSamples, image.HeightInSamples);

                var numberOfComponents = image.ColorSpaceDetails!.BaseNumberOfColorComponents;

                var is3Byte = numberOfComponents == 3;

                var requiredSize = (image.WidthInSamples * image.HeightInSamples * numberOfComponents);

                var actualSize = bytesPure.Length;
                var isCorrectlySized = bytesPure.Length == requiredSize ||
                    // Spec, p. 37: "...error if the stream contains too much data, with the exception that
                    // there may be an extra end-of-line marker..."
                    (actualSize == requiredSize + 1 && bytesPure[actualSize - 1] == ReadHelper.AsciiLineFeed) ||
                    (actualSize == requiredSize + 1 && bytesPure[actualSize - 1] == ReadHelper.AsciiCarriageReturn) ||
                    // The combination of a CARRIAGE RETURN followed immediately by a LINE FEED is treated as one EOL marker.
                    (actualSize == requiredSize + 2 &&
                        bytesPure[actualSize - 2] == ReadHelper.AsciiCarriageReturn &&
                        bytesPure[actualSize - 1] == ReadHelper.AsciiLineFeed);

                if (!isCorrectlySized)
                {
                    return false;
                }

                if (numberOfComponents == 1)
                {
                    return TryGetGray8Bitmap(image.WidthInSamples, image.HeightInSamples, bytesPure, out bitmap);
                }
                
                var info = new SKImageInfo(image.WidthInSamples, image.HeightInSamples, SKColorType.Rgba8888);

                // create the buffer that will hold the pixels
                bool hasAlphaChannel = true;
                var bpp = hasAlphaChannel ? 4 : 3;

                var length = (image.HeightInSamples * image.WidthInSamples * bpp) + image.HeightInSamples;

                var raster = new byte[length];

                var builder = ImageBuilder.Create(raster, image.WidthInSamples, image.HeightInSamples, hasAlphaChannel);

                // get a pointer to the buffer, and give it to the bitmap
                var ptr = GCHandle.Alloc(raster, GCHandleType.Pinned);

                using (SKPixmap pixmap = new SKPixmap(info, ptr.AddrOfPinnedObject(), info.RowBytes))
                {
                    bitmap = SKImage.FromPixels(pixmap, (addr, ctx) => ptr.Free());
                }
                
                byte alpha = byte.MaxValue;
                if (image.ColorSpaceDetails.BaseType == ColorSpace.DeviceCMYK || numberOfComponents == 4)
                {
                    int i = 0;
                    for (int col = 0; col < image.HeightInSamples; col++)
                    {
                        for (int row = 0; row < image.WidthInSamples; row++)
                        {
                            /*
                             * Where CMYK in 0..1
                             * R = 255 × (1-C) × (1-K)
                             * G = 255 × (1-M) × (1-K)
                             * B = 255 × (1-Y) × (1-K)
                             */

                            double c = (bytesPure[i++] / 255d);
                            double m = (bytesPure[i++] / 255d);
                            double y = (bytesPure[i++] / 255d);
                            double k = (bytesPure[i++] / 255d);
                            var r = (byte)(255 * (1 - c) * (1 - k));
                            var g = (byte)(255 * (1 - m) * (1 - k));
                            var b = (byte)(255 * (1 - y) * (1 - k));

                            builder.SetPixel(r, g, b, alpha, row, col);
                        }
                    }
                    return true;
                }

                if (is3Byte)
                {
                    int i = 0;
                    for (int col = 0; col < image.HeightInSamples; col++)
                    {
                        for (int row = 0; row < image.WidthInSamples; row++)
                        {
                            builder.SetPixel(bytesPure[i++], bytesPure[i++], bytesPure[i++], alpha, row, col);
                        }
                    }
                    return true;
                }

                throw new Exception($"Could not process image with ColorSpace={image.ColorSpaceDetails.BaseType}, numberOfComponents={numberOfComponents}.");
            }
            catch
            {
                // ignored.
            }

            bitmap?.Dispose();
            return false;
        }

        private static bool TryGetGray8Bitmap(int width, int height, ReadOnlySpan<byte> bytesPure, out SKImage? bitmap)
        {
            bitmap = null;

            try
            {
                bitmap = SKImage.FromPixelCopy(new SKImageInfo(width, height, SKColorType.Gray8), bytesPure);
                return true;
            }
            catch (Exception)
            {
                // ignored.
            }

            bitmap?.Dispose();
            return false;
        }

        private sealed class ImageBuilder
        {
            private readonly byte[] rawData;
            private readonly bool hasAlphaChannel;
            private readonly int width;
            private readonly int height;
            private readonly int bytesPerPixel;

            /// <summary>
            /// Create a builder for a PNG with the given width and size.
            /// </summary>
            public static ImageBuilder Create(byte[] rawData, int width, int height, bool hasAlphaChannel)
            {
                var bpp = hasAlphaChannel ? 4 : 3;

                var length = (height * width * bpp) + height;

                if (rawData.Length != length)
                {
                    throw new ArgumentOutOfRangeException(nameof(rawData.Length), "TestBuilder.Create");
                }

                return new ImageBuilder(rawData, hasAlphaChannel, width, height, bpp);
            }

            private ImageBuilder(byte[] rawData, bool hasAlphaChannel, int width, int height, int bytesPerPixel)
            {
                this.rawData = rawData;
                this.hasAlphaChannel = hasAlphaChannel;
                this.width = width;
                this.height = height;
                this.bytesPerPixel = bytesPerPixel;
            }

            /// <summary>
            /// Set the pixel value for the given column (x) and row (y).
            /// </summary>
            public void SetPixel(byte r, byte g, byte b, byte a, int x, int y)
            {
                var start = (y * (width * bytesPerPixel)) + (x * bytesPerPixel);

                rawData[start++] = r;
                rawData[start++] = g;
                rawData[start++] = b;

                if (hasAlphaChannel)
                {
                    rawData[start] = a;
                }
            }
        }

        public static SKImage GetSKImage(this IPdfImage pdfImage)
        {
            // Try get png bytes
            if (pdfImage.TryGenerate(out var bitmap))
            {
                return bitmap;
            }

            // Fallback to bytes
            if (pdfImage.TryGetBytesAsMemory(out var bytesL) && bytesL.Length > 0)
            {
                try
                {
                    return SKImage.FromEncodedData(bytesL.Span);
                }
                catch (Exception)
                {
                    // ignore
                }
            }

            // Fallback to raw bytes
            return SKImage.FromEncodedData(pdfImage.RawBytes);
        }

        public static ReadOnlySpan<byte> GetImageBytes(this IPdfImage pdfImage)
        {
            // Try get png bytes
            if (pdfImage.TryGetPng(out byte[]? bytes) && bytes?.Length > 0)
            {
                return bytes;
            }

            // Fallback to bytes
            if (pdfImage.TryGetBytesAsMemory(out var bytesL) && bytesL.Length > 0)
            {
                return bytesL.Span;
            }

            // Fallback to raw bytes
            return pdfImage.RawBytes;
        }
    }
}
