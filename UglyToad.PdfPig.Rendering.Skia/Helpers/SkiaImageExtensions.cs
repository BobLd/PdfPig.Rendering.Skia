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
using SkiaSharp;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Images;
using UglyToad.PdfPig.Logging;
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

        private static bool TryGenerate(this IPdfImage pdfImage, out SKBitmap? skBitmap, ILog? log)
        {
            skBitmap = null;

            if (!IsValidColorSpace(pdfImage) || !pdfImage.TryGetBytesAsMemory(out var imageMemory))
            {
                return false;
            }

            var imageSpan = imageMemory.Span;
            SKBitmap? mask = null;

            try
            {
                int width = pdfImage.WidthInSamples;
                int height = pdfImage.HeightInSamples;

                imageSpan = ColorSpaceDetailsByteConverter.Convert(pdfImage.ColorSpaceDetails!, imageSpan,
                    pdfImage.BitsPerComponent, width, height);

                var numberOfComponents = pdfImage.ColorSpaceDetails!.BaseNumberOfColorComponents;

                if (!IsImageArrayCorrectlySized(pdfImage, imageSpan))
                {
                    return false;
                }

                bool isRgba = numberOfComponents > 1 || pdfImage.HasAlphaChannel();
                var colorSpace = isRgba ? SKColorType.Rgba8888 : SKColorType.Gray8;

                // We apparently need SKAlphaType.Unpremul to avoid artifacts with transparency.
                // For example, the logo's background in "Motor Insurance claim form.pdf" might
                // appear black instead of transparent at certain scales.
                // See https://groups.google.com/g/skia-discuss/c/sV6e3dpf4CE for related question
                var alphaType = SKAlphaType.Unpremul;
                
                // Special case for mask
                if (pdfImage.IsImageMask && colorSpace == SKColorType.Gray8)
                {
                    colorSpace = SKColorType.Alpha8;
                    alphaType  = SKAlphaType.Premul;
                }

                var info = new SKImageInfo(width, height, colorSpace, alphaType);

                int bytesPerPixel = isRgba ? 4 : 1; // 3 (RGB) + 1 (alpha)

                Func<int, int, byte, byte, byte, byte> getAlphaChannel = (_, _, _, _, _) => byte.MaxValue;

                if (pdfImage.MaskImage is not null)
                {
                    if (pdfImage.MaskImage.TryGenerate(out mask, log))
                    {
                        mask!.SetImmutable();

                        if (!info.Rect.Equals(mask.Info.Rect))
                        {
                            // Resize
                            var maskInfo = new SKImageInfo(info.Width, info.Height, mask!.Info.ColorType, mask!.Info.AlphaType);
                            SKBitmap resized = mask.Resize(maskInfo, pdfImage.MaskImage.GetSamplingOption());
                            resized.SetImmutable();

                            mask.Dispose();
                            mask = resized;
                        }

                        if (!mask.IsEmpty)
                        {
                            // TODO - It is very unclear why we need this logic of reversing or not here for IsImageMask
          
                            if (pdfImage.MaskImage.IsImageMask)
                            {
                                // We reverse pixel color
                                getAlphaChannel = (row, col, _, _, _) => (byte)~mask.GetPixelSpan()[(row * width) + col];
                            }
                            else
                            {
                                // This is a NameToken.Smask - we do not reverse pixel color
                                getAlphaChannel = (row, col, _, _, _) => mask.GetPixelSpan()[(row * width) + col];
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
                        SkiaExtensions.ApproximateCmykToRgb(in range[0], in range[1], in range[2], in range[3], out rMin, out gMin, out bMin);
                        SkiaExtensions.ApproximateCmykToRgb(in range[4], in range[5], in range[6], in range[7], out rMax, out gMax, out bMax);
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

                skBitmap = new SKBitmap(info);
                Span<byte> rasterSpan = skBitmap.GetPixelSpan();

                if (numberOfComponents == 4)
                {
                    int i = 0;
                    for (int row = 0; row < height; ++row)
                    {
                        for (int col = 0; col < width; ++col)
                        {
                            SkiaExtensions.ApproximateCmykToRgb(in imageSpan[i++], in imageSpan[i++], in imageSpan[i++], in imageSpan[i++],
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
                            byte r = imageSpan[i++];
                            byte g = imageSpan[i++];
                            byte b = imageSpan[i++];

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
                                byte g = imageSpan[i++];

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
                        for (int i = 0; i < imageSpan.Length; ++i)
                        {
                            rasterSpan[i] = (byte)~imageSpan[i];
                        }

                        return true;
                    }

                    for (int i = 0; i < imageSpan.Length; ++i)
                    {
                        rasterSpan[i] = imageSpan[i];
                    }

                    return true;
                }

                throw new Exception($"Could not process image with ColorSpace={pdfImage.ColorSpaceDetails.BaseType}, numberOfComponents={numberOfComponents}.");
            }
            catch (Exception ex)
            {
                log?.Error($"Failed to generate bitmap from pdf image: {ex.Message}");
            }
            finally
            {
                mask?.Dispose();
            }

            skBitmap?.Dispose();
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
        /// Converts the specified <see cref="IPdfImage"/> to an <see cref="SKBitmap"/> instance.
        /// </summary>
        /// <param name="pdfImage">The PDF image to convert.</param>
        /// <param name="log"></param>
        /// <returns>
        /// An <see cref="SKBitmap"/> representation of the provided <paramref name="pdfImage"/>.
        /// If the conversion fails, a fallback mechanism is used to create the image from raw bytes.
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="pdfImage"/> is <c>null</c>.</exception>
        /// <remarks>
        /// This method attempts to generate an <see cref="SKBitmap"/> using the image's data and color space.
        /// If the generation fails, it falls back to creating the image using encoded or raw byte data.
        /// </remarks>
        public static SKBitmap? GetSKBitmap(this IPdfImage pdfImage, ILog? log = null)
        {
            if (pdfImage.TryGenerate(out var bitmap, log))
            {
                return bitmap!;
            }

            log?.Warn("Failed to generate bitmap from pdf image.");

            // Fallback to bytes
            if (pdfImage.TryGetBytesAsMemory(out var bytesL) && bytesL.Length > 0)
            {
                try
                {
                    return SKBitmap.Decode(bytesL.Span);
                }
                catch (Exception ex)
                {
                    log?.Error($"Failed to generate bitmap from decoded bytes: {ex.Message}");
                }
            }
            else
            {
                log?.Error("Failed to decode image bytes.");
            }

            // Fallback to raw bytes
            return SKBitmap.Decode(pdfImage.RawBytes);
        }
    }
}
