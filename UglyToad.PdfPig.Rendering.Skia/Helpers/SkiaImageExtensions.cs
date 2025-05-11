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
using System.Linq;
using System.Runtime.InteropServices;
using SkiaSharp;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Images;
using UglyToad.PdfPig.Tokens;

namespace UglyToad.PdfPig.Rendering.Skia.Helpers
{
    internal static class SkiaImageExtensions
    {
        private static bool IsValidColorSpace(IPdfImage pdfImage)
        {
            return pdfImage.ColorSpaceDetails != null &&
                                  !(pdfImage.ColorSpaceDetails is UnsupportedColorSpaceDetails)
                                  && pdfImage.ColorSpaceDetails!.BaseType != ColorSpace.Pattern;
        }


        private static bool IsImageArrayCorrectlySized(IPdfImage pdfImage, ReadOnlySpan<byte> bytesPure)
        {
            var actualSize = bytesPure.Length;
            var requiredSize = (pdfImage.WidthInSamples * pdfImage.HeightInSamples * pdfImage.ColorSpaceDetails!.BaseNumberOfColorComponents);

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
        private static bool TryGenerate(this IPdfImage pdfImage, out SKImage skImage)
        {
            skImage = null;

            if (!IsValidColorSpace(pdfImage) || !pdfImage.TryGetBytesAsMemory(out var imageMemory))
            {
                return false;
            }

            var bytesPure = imageMemory.Span;
            SKImage? mask = null;
            SKPixmap? sMaskPixmap = null;

            try
            {
                int width = pdfImage.WidthInSamples;
                int height = pdfImage.HeightInSamples;

                bytesPure = ColorSpaceDetailsByteConverter.Convert(pdfImage.ColorSpaceDetails!, bytesPure,
                    pdfImage.BitsPerComponent, width, height);

                var numberOfComponents = pdfImage.ColorSpaceDetails!.BaseNumberOfColorComponents;

                if (!IsImageArrayCorrectlySized(pdfImage, bytesPure))
                {
                    return false;
                }

                if (numberOfComponents == 1)
                {
                    if (pdfImage.MaskImage is not null)
                    {
                        // TODO
                    }

                    return TryGetGray8Bitmap(width, height, bytesPure, out skImage);
                }

                // We apparently need SKAlphaType.Unpremul to avoid artifacts with transparency.
                // For example, the logo's background in "Motor Insurance claim form.pdf" might
                // appear black instead of transparent at certain scales.
                // See https://groups.google.com/g/skia-discuss/c/sV6e3dpf4CE for related question
                var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);

                // create the buffer that will hold the pixels
                const int bytesPerPixel = 4; // 3 (RGB) + 1 (alpha)

                var length = (height * width * bytesPerPixel) + height;

                var raster = new byte[length];

                // get a pointer to the buffer, and give it to the skImage
                var ptr = GCHandle.Alloc(raster, GCHandleType.Pinned);
                using (SKPixmap pixmap = new SKPixmap(info, ptr.AddrOfPinnedObject(), info.RowBytes))
                {
                    skImage = SKImage.FromPixels(pixmap, (addr, ctx) => ptr.Free());
                }

                Func<int, int, byte, byte, byte, byte> getAlphaChannel = (_, _, _, _, _) => byte.MaxValue;
                if (pdfImage.MaskImage?.TryGenerate(out mask) == true)
                {
                    if (!skImage.Info.Rect.Equals(mask!.Info.Rect))
                    {
                        // Resize
                        var maskInfo = new SKImageInfo(skImage.Info.Width, skImage.Info.Height, SKColorType.Gray8, SKAlphaType.Unpremul);
                        var maskRaster = new byte[skImage.Info.Width * skImage.Info.Height];
                        var ptrMask = GCHandle.Alloc(maskRaster, GCHandleType.Pinned);
                        sMaskPixmap = new SKPixmap(maskInfo, ptrMask.AddrOfPinnedObject(), maskInfo.RowBytes);
                        if (!mask.ScalePixels(sMaskPixmap, SKFilterQuality.High))
                        {
                            // TODO - Error
                        }

                        mask.Dispose();
                        mask = SKImage.FromPixels(sMaskPixmap, (addr, ctx) => ptrMask.Free());
                    }
                    else
                    {
                        sMaskPixmap = mask.PeekPixels();
                    }

                    if (!sMaskPixmap.GetPixelSpan().IsEmpty)
                    {
                        //getAlphaChannel = (row, col, _, _, _) => sMaskPixmap.GetPixelSpan()[(row * width) + col];

                        if (pdfImage.MaskImage.NeedsReverseDecode())
                        {
                            // MOZILLA-LINK-3264-0.pdf 
                            // MOZILLA-LINK-4246-2.pdf
                            // MOZILLA-LINK-4293-0.pdf
                            // MOZILLA-LINK-4314-0.pdf
                            // MOZILLA-LINK-3758-0.pdf

                            // Wrong: MOZILLA-LINK-4379-0.pd

                            getAlphaChannel = (row, col, _, _, _) =>
                                Convert.ToByte(255 - sMaskPixmap.GetPixelSpan()[(row * width) + col]);
                        }
                        else
                        {
                            getAlphaChannel = (row, col, _, _, _) => sMaskPixmap.GetPixelSpan()[(row * width) + col];
                        }
                    }
                }
                else if (pdfImage.ImageDictionary.TryGet(NameToken.Mask, out ArrayToken maskArr))
                {
                    var bytes = maskArr.Data.OfType<NumericToken>().Select(x => Convert.ToByte(x.Int)).ToArray();

                    var range = ColorSpaceDetailsByteConverter.Convert(
                        pdfImage.ColorSpaceDetails!,
                        bytes,
                        pdfImage.BitsPerComponent,
                        bytes.Length / pdfImage.ColorSpaceDetails!.NumberOfColorComponents,
                        1);

                    byte rMin = 0;
                    byte gMin = 0;
                    byte bMin = 0;
                    byte rMax = 0;
                    byte gMax = 0;
                    byte bMax = 0;

                    if (numberOfComponents == 4)
                    {
                        if (range.Length != 8)
                        {
                            throw new ArgumentException($"The size of the transformed mask array does not match number of components of 4: got {range.Length} but expected 8.");
                        }

                        throw new NotImplementedException("Mask CMYK");
                    }
                    else if (numberOfComponents == 3)
                    {
                        if (range.Length != 6)
                        {
                            throw new ArgumentException($"The size of the transformed mask array does not match number of components of 3: got {range.Length} but expected 6.");
                        }

                        rMin = range[0];
                        gMin = range[1];
                        bMin = range[2];
                        rMax = range[3];
                        gMax = range[4];
                        bMax = range[5];
                    }

                    getAlphaChannel = (_, _, r, g, b) =>
                    {
                        if (rMin <= r && r <= rMax &&
                            gMin <= g && g <= gMax &&
                            bMin <= b && b <= bMax)
                        {
                            return byte.MinValue;
                        }

                        return byte.MaxValue;
                    };
                }

                if (pdfImage.ColorSpaceDetails.BaseType == ColorSpace.DeviceCMYK || numberOfComponents == 4)
                {
                    int i = 0;
                    for (int row = 0; row < height; ++row)
                    {
                        for (int col = 0; col < width; ++col)
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

                            var start = (row * (width * bytesPerPixel)) + (col * bytesPerPixel);
                            raster[start++] = r;
                            raster[start++] = g;
                            raster[start++] = b;
                            raster[start] = getAlphaChannel(row, col, r, g, b);
                        }
                    }

                    return true;
                }

                if (numberOfComponents == 3)
                {
                    int i = 0;
                    for (int row = 0; row < height; ++row)
                    {
                        for (int col = 0; col < width; ++col)
                        {
                            byte r = bytesPure[i++];
                            byte g = bytesPure[i++];
                            byte b = bytesPure[i++];

                            var start = (row * (width * bytesPerPixel)) + (col * bytesPerPixel);
                            raster[start++] = r;
                            raster[start++] = g;
                            raster[start++] = b;
                            raster[start] = getAlphaChannel(row, col, r, g, b);
                        }
                    }

                    return true;
                }

                throw new Exception($"Could not process image with ColorSpace={pdfImage.ColorSpaceDetails.BaseType}, numberOfComponents={numberOfComponents}.");
            }
            catch
            {
                // ignored.
            }
            finally
            {
                mask?.Dispose();
                sMaskPixmap?.Dispose();
            }

            skImage?.Dispose();
            return false;
        }

        private static bool TryGetGray8Bitmap(int width, int height, ReadOnlySpan<byte> bytesPure, out SKImage? bitmap)
        {
            bitmap = null;

            try
            {
                // Alpha8 for mask?
                bitmap = SKImage.FromPixelCopy(new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Unpremul), bytesPure); // Alpha8 Or Gray8
                return true;
            }
            catch (Exception)
            {
                // ignored.
            }

            bitmap?.Dispose();
            return false;
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
    }
}
