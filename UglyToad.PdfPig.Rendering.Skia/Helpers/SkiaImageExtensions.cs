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
        private static bool IsValidColorSpace(IPdfImage image)
        {
            return image.ColorSpaceDetails != null &&
                                  !(image.ColorSpaceDetails is UnsupportedColorSpaceDetails)
                                  && image.ColorSpaceDetails!.BaseType != ColorSpace.Pattern;
        }


        private static bool IsImageArrayCorrectlySized(IPdfImage image, ReadOnlySpan<byte> bytesPure)
        {
            var actualSize = bytesPure.Length;
            var requiredSize = (image.WidthInSamples * image.HeightInSamples * image.ColorSpaceDetails!.BaseNumberOfColorComponents);
            
            return bytesPure.Length == requiredSize ||
                                   // Spec, p. 37: "...error if the stream contains too much data, with the exception that
                                   // there may be an extra end-of-line marker..."
                                   (actualSize == requiredSize + 1 &&
                                    bytesPure[actualSize - 1] == ReadHelper.AsciiLineFeed) ||
                                   (actualSize == requiredSize + 1 &&
                                    bytesPure[actualSize - 1] == ReadHelper.AsciiCarriageReturn) ||
                                   // The combination of a CARRIAGE RETURN followed immediately by a LINE FEED is treated as one EOL marker.
                                   (actualSize == requiredSize + 2 &&
                                    bytesPure[actualSize - 2] == ReadHelper.AsciiCarriageReturn &&
                                    bytesPure[actualSize - 1] == ReadHelper.AsciiLineFeed);
        }

        // https://stackoverflow.com/questions/50312937/skiasharp-tiff-support#50370515
        private static bool TryGenerate(this IPdfImage image, out SKImage bitmap)
        {
            bitmap = null;

            if (!IsValidColorSpace(image) || !image.TryGetBytesAsMemory(out var imageMemory))
            {
                return false;
            }

            var bytesPure = imageMemory.Span;

            try
            {
                int width = image.WidthInSamples;
                int height = image.HeightInSamples;

                bytesPure = ColorSpaceDetailsByteConverter.Convert(image.ColorSpaceDetails!, bytesPure,
                    image.BitsPerComponent, width, height);

                var numberOfComponents = image.ColorSpaceDetails!.BaseNumberOfColorComponents;

                if (!IsImageArrayCorrectlySized(image, bytesPure))
                {
                    return false;
                }

                if (numberOfComponents == 1)
                {
                    if (image.SoftMaskImage is not null)
                    {
                        // TODO
                    }
                    
                    return TryGetGray8Bitmap(width, height, bytesPure, out bitmap);
                }

                var info = new SKImageInfo(width, height, SKColorType.Rgba8888);

                // create the buffer that will hold the pixels
                const int bytesPerPixel = 4; // 3 (RGB) + 1 (alpha)

                var length = (height * width * bytesPerPixel) + height;

                var raster = new byte[length];

                // get a pointer to the buffer, and give it to the bitmap
                var ptr = GCHandle.Alloc(raster, GCHandleType.Pinned);
                using (SKPixmap pixmap = new SKPixmap(info, ptr.AddrOfPinnedObject(), info.RowBytes))
                {
                    bitmap = SKImage.FromPixels(pixmap, (addr, ctx) => ptr.Free());
                }

                SKMask? sMask = null;
                if (image.SoftMaskImage?.TryGenerate(out var mask) == true)
                {
                    if (!bitmap.Info.Rect.Equals(mask.Info.Rect))
                    {
                        // TODO - Resize
                    }

                    sMask = info.GetSKMask(mask);
                }

                using (var alphaProvider = new AlphaChannelProvider(sMask))
                {
                    if (image.ColorSpaceDetails.BaseType == ColorSpace.DeviceCMYK || numberOfComponents == 4)
                    {
                        int i = 0;
                        for (int col = 0; col < height; ++col)
                        {
                            for (int row = 0; row < width; ++row)
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

                                var start = (col * (width * bytesPerPixel)) + (row * bytesPerPixel);
                                raster[start++] = r;
                                raster[start++] = g;
                                raster[start++] = b;
                                raster[start] = alphaProvider.GetAlphaChannel(row, col);
                            }
                        }

                        return true;
                    }

                    if (numberOfComponents == 3)
                    {
                        int i = 0;
                        for (int col = 0; col < height; ++col)
                        {
                            for (int row = 0; row < width; ++row)
                            {
                                var start = (col * (width * bytesPerPixel)) + (row * bytesPerPixel);
                                raster[start++] = bytesPure[i++];
                                raster[start++] = bytesPure[i++];
                                raster[start++] = bytesPure[i++];
                                raster[start] = alphaProvider.GetAlphaChannel(row, col);
                            }
                        }

                        return true;
                    }
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

        private sealed class AlphaChannelProvider : IDisposable
        {
            private readonly SKMask? _skMask;
            private readonly SKAutoMaskFreeImage? _disposable;

            public readonly Func<int, int, byte> GetAlphaChannel;
            
            public AlphaChannelProvider(SKMask? mask)
            {
                _skMask = mask;
                if (_skMask.HasValue)
                {
                    if (_skMask.Value.Image == IntPtr.Zero)
                    {
                        throw new ArgumentNullException(nameof(mask), "The SKMask has a pointer of zero.");
                    }
                    
                    GetAlphaChannel = _skMask.Value.GetAddr8;
                    _disposable = new SKAutoMaskFreeImage(_skMask.Value.Image);
                }
                else
                {
                    GetAlphaChannel = (x, y) => byte.MaxValue;
                }
            }

            public void Dispose()
            {
                _disposable?.Dispose();
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


        public static SKMask GetSKMask(this SKImageInfo skImageInfo, SKImage softMaskBitmap)
        {
            return SKMask.Create(softMaskBitmap.PeekPixels().GetPixelSpan(),
                new SKRectI(0, 0, skImageInfo.Width, skImageInfo.Height),
                (uint)softMaskBitmap.Info.RowBytes,
                SKMaskFormat.A8);
        }
    }
}
