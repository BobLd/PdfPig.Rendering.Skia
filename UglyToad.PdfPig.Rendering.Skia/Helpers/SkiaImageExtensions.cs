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
    /// <summary>
    /// SkiaSharp image extensions.
    /// </summary>
    public static class SkiaImageExtensions
    {
        private static bool IsValidColorSpace(IPdfImage pdfImage)
        {
            return pdfImage.ColorSpaceDetails is not null &&
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

        private static bool HasAlphaChannel(this IPdfImage pdfImage)
        {
            return pdfImage.MaskImage is not null || pdfImage.ImageDictionary.ContainsKey(NameToken.Mask);
        }

        internal static int GetRasterSize(this IPdfImage pdfImage)
        {
            int width = pdfImage.WidthInSamples;
            int height = pdfImage.HeightInSamples;

            var numberOfComponents = pdfImage.ColorSpaceDetails!.BaseNumberOfColorComponents;
            bool isRgba = numberOfComponents > 1 || pdfImage.HasAlphaChannel();
            int bytesPerPixel = isRgba ? 4 : 1; // 3 (RGB) + 1 (alpha)

            return height * width * bytesPerPixel;
        }

        // https://stackoverflow.com/questions/50312937/skiasharp-tiff-support#50370515
        private static bool TryGenerate(this IPdfImage pdfImage, out SKImage? skImage)
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

                bool isRgba = numberOfComponents > 1 || pdfImage.HasAlphaChannel();
                var colorSpace = isRgba ? SKColorType.Rgba8888 : SKColorType.Gray8;
                /*
                if (pdfImage.IsImageMask && colorSpace == SKColorType.Gray8)
                {
                    colorSpace = SKColorType.Alpha8;
                }
                */

                // We apparently need SKAlphaType.Unpremul to avoid artifacts with transparency.
                // For example, the logo's background in "Motor Insurance claim form.pdf" might
                // appear black instead of transparent at certain scales.
                // See https://groups.google.com/g/skia-discuss/c/sV6e3dpf4CE for related question
                var info = new SKImageInfo(width, height, colorSpace, SKAlphaType.Unpremul);

                int bytesPerPixel = isRgba ? 4 : 1; // 3 (RGB) + 1 (alpha)

                Func<int, int, byte, byte, byte, byte> getAlphaChannel = (_, _, _, _, _) => byte.MaxValue;
                if (pdfImage.MaskImage is not null)
                {
                    if (pdfImage.MaskImage.TryGenerate(out mask))
                    {
                        if (!info.Rect.Equals(mask!.Info.Rect))
                        {
                            // Resize
                            var maskInfo = new SKImageInfo(info.Width, info.Height, mask!.Info.ColorType, mask!.Info.AlphaType);
                            var maskRasterResize = new byte[info.Width * info.Height];
                            var ptrMask = GCHandle.Alloc(maskRasterResize, GCHandleType.Pinned);
                            sMaskPixmap = new SKPixmap(maskInfo, ptrMask.AddrOfPinnedObject(), maskInfo.RowBytes);
                            if (!mask.ScalePixels(sMaskPixmap, pdfImage.MaskImage.GetSamplingOption()))
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
                            // TODO - It is very unclear why we need this logic of reversing or not here for IsImageMask
                            if (pdfImage.MaskImage.IsImageMask)
                            {
                                // We reverse pixel color
                                getAlphaChannel = (row, col, _, _, _) => (byte)~sMaskPixmap.GetPixelSpan()[(row * width) + col];
                            }
                            else
                            {
                                // This is a NameToken.Smask - we do not reverse pixel color
                                getAlphaChannel = (row, col, _, _, _) => sMaskPixmap.GetPixelSpan()[(row * width) + col];
                            }

                            // Examples of docs that are decode inverse
                            // MOZILLA-LINK-3264-0.pdf 
                            // MOZILLA-LINK-4246-2.pdf
                            // MOZILLA-LINK-4293-0.pdf
                            // MOZILLA-LINK-4314-0.pdf
                            // MOZILLA-LINK-3758-0.pdf

                            // Wrong: MOZILLA-LINK-4379-0.pdf
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

                        // TODO - Add tests
                        CmykToRgb(in range[0], in range[1], in range[2], in range[3], out rMin, out gMin, out bMin);
                        CmykToRgb(in range[4], in range[5], in range[6], in range[7], out rMax, out gMax, out bMax);
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
                    else if (numberOfComponents == 1)
                    {
                        throw new NotImplementedException("Mask with numberOfComponents == 1.");
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

                // create the buffer that will hold the pixels
                byte[] raster = new byte[height * width * bytesPerPixel];
                Span<byte> rasterSpan = raster;

                // get a pointer to the buffer, and give it to the skImage
                var ptr = GCHandle.Alloc(raster, GCHandleType.Pinned);
                using (SKPixmap pixmap = new SKPixmap(info, ptr.AddrOfPinnedObject(), info.RowBytes))
                {
                    skImage = SKImage.FromPixels(pixmap, (addr, ctx) =>
                    {
                        ptr.Free();
                        raster = null!;
                        //System.Diagnostics.Debug.WriteLine("ptr.Free()");
                    });
                }

                if (numberOfComponents == 4)
                {
                    int i = 0;
                    for (int row = 0; row < height; ++row)
                    {
                        for (int col = 0; col < width; ++col)
                        {
                            CmykToRgb(in bytesPure[i++], in bytesPure[i++], in bytesPure[i++], in bytesPure[i++],
                                out byte r, out byte g, out byte b);

                            var start = (row * (width * bytesPerPixel)) + (col * bytesPerPixel);
                            rasterSpan[start] = r;
                            rasterSpan[start + 1] = g;
                            rasterSpan[start + 2] = b;
                            rasterSpan[start + 3] = getAlphaChannel(row, col, r, g, b);
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
                            rasterSpan[start] = r;
                            rasterSpan[start + 1] = g;
                            rasterSpan[start + 2] = b;
                            rasterSpan[start + 3] = getAlphaChannel(row, col, r, g, b);
                        }
                    }

                    return true;
                }

                if (numberOfComponents == 1)
                {
                    if (isRgba)
                    {
                        // Handle gray scale image as RGBA because we have an alpha channel
                        int i = 0;
                        for (int row = 0; row < height; ++row)
                        {
                            for (int col = 0; col < width; ++col)
                            {
                                byte g = bytesPure[i++];

                                var start = (row * (width * bytesPerPixel)) + (col * bytesPerPixel);
                                rasterSpan[start] = g;
                                rasterSpan[start + 1] = g;
                                rasterSpan[start + 2] = g;
                                rasterSpan[start + 3] = getAlphaChannel(row, col, g, g, g);
                            }
                        }

                        return true;
                    }
                    
                    if (pdfImage.NeedsReverseDecode())
                    {
                        for (int i = 0; i < bytesPure.Length; ++i)
                        {
                            rasterSpan[i] = (byte)~bytesPure[i];
                        }

                        return true;
                    }
                    
                    for (int i = 0; i < bytesPure.Length; ++i)
                    {
                        rasterSpan[i] = bytesPure[i];
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

        private static void CmykToRgb(in byte c, in byte m, in byte y, in byte k, out byte r, out byte g, out byte b)
        {
            /*
             * Where CMYK in 0..1
             * R = 255 × (1-C) × (1-K)
             * G = 255 × (1-M) × (1-K)
             * B = 255 × (1-Y) × (1-K)
             */

            double cD = c / 255d;
            double mD = m / 255d;
            double yD = y / 255d;
            double kD = k / 255d;
            r = (byte)(255 * (1 - cD) * (1 - kD));
            g = (byte)(255 * (1 - mD) * (1 - kD));
            b = (byte)(255 * (1 - yD) * (1 - kD));
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

        private static SKSamplingOptions GetSamplingOption(this IPdfImage pdfImage)
        {
            if (pdfImage.Interpolate)
            {
                return new SKSamplingOptions(SKCubicResampler.Mitchell);
            }

            return new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        }

        /// <summary>
        /// Converts the specified <see cref="IPdfImage"/> to an <see cref="SKImage"/> instance.
        /// </summary>
        /// <param name="pdfImage">The PDF image to convert.</param>
        /// <returns>
        /// An <see cref="SKImage"/> representation of the provided <paramref name="pdfImage"/>.
        /// If the conversion fails, a fallback mechanism is used to create the image from raw bytes.
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="pdfImage"/> is <c>null</c>.</exception>
        /// <remarks>
        /// This method attempts to generate an <see cref="SKImage"/> using the image's data and color space.
        /// If the generation fails, it falls back to creating the image using encoded or raw byte data.
        /// </remarks>
        public static SKImage? GetSKImage(this IPdfImage pdfImage)
        {
            if (pdfImage.TryGenerate(out var bitmap))
            {
                return bitmap!;
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
