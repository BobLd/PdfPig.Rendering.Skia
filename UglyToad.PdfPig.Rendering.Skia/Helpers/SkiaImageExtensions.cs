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
        private enum ImageAlphaType : byte
        {
            /// <summary>
            /// no mask, alpha is always 0xFF.
            /// </summary>
            Opaque = 0,

            /// <summary>
            /// mask SKBitmap, alpha = maskSpan[pixelIndex] (PDF SMask).
            /// </summary>
            Mask = 1,

            /// <summary>
            /// mask SKBitmap with inverted bytes (mask is itself an
            /// image-mask stencil — see MOZILLA-LINK-3264-0.pdf etc.).
            /// </summary>
            MaskInv = 2,

            /// <summary>
            /// /Mask array range test on the RGB triple.
            /// </summary>
            ColourKey = 3
        }

        private delegate byte ImageAlphaResolver(int pixelIndex, ReadOnlySpan<byte> maskSpan,
            byte r, byte g, byte b);

        private static ImageAlphaResolver ResolveAlpha(ImageAlphaType alphaMode,
            byte rMin, byte gMin, byte bMin,
            byte rMax, byte gMax, byte bMax)
        {
            switch (alphaMode)
            {
                case ImageAlphaType.Mask:
                    return static (p, mask, _, _, _) => mask[p];
                case ImageAlphaType.MaskInv:
                    return static (p, mask, _, _, _) => (byte)~mask[p];
                case ImageAlphaType.ColourKey:
                    return (_, _, r, g, b) =>
                    {
                        if (rMin <= r && r <= rMax &&
                            gMin <= g && g <= gMax &&
                            bMin <= b && b <= bMax)
                        {
                            return byte.MinValue;
                        }

                        return byte.MaxValue;
                    };
                default:
                    return static (_, _, _, _, _) => byte.MaxValue;
            }
        }

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

                ReadOnlySpan<byte> maskSpan = ReadOnlySpan<byte>.Empty;
                byte rMin = 0, gMin = 0, bMin = 0, rMax = 0, gMax = 0, bMax = 0;
                ImageAlphaType alphaMode = ImageAlphaType.Opaque;

                if (pdfImage.MaskImage is not null)
                {
                    if (pdfImage.MaskImage.TryGenerate(out mask!, log))
                    {
                        if (!info.Rect.Equals(mask.Info.Rect))
                        {
                            var maskInfo = new SKImageInfo(info.Width, info.Height, mask.Info.ColorType, mask.Info.AlphaType);
                            SKBitmap resized = mask.Resize(maskInfo, pdfImage.MaskImage.GetSamplingOption());
                            resized.SetImmutable();
                            mask.Dispose();
                            mask = resized;
                        }

                        if (!mask.IsEmpty)
                        {
                            maskSpan = mask.GetPixelSpan();
                            alphaMode = pdfImage.MaskImage.IsImageMask ? ImageAlphaType.MaskInv : ImageAlphaType.Mask;
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

                    alphaMode = ImageAlphaType.ColourKey;
                }

                ImageAlphaResolver getImageAlphaChannel = ResolveAlpha(alphaMode, rMin, gMin, bMin, rMax, gMax, bMax);
                skBitmap = new SKBitmap(info);
                Span<byte> rasterSpan = skBitmap.GetPixelSpan();
                int pixelCount = width * height;

                if (numberOfComponents == 4)
                {
                    int srcIdx = 0;
                    int dstIdx = 0;
                    for (int p = 0; p < pixelCount; p++)
                    {
                        SkiaExtensions.ApproximateCmykToRgb(
                            in imageSpan[srcIdx], in imageSpan[srcIdx + 1],
                            in imageSpan[srcIdx + 2], in imageSpan[srcIdx + 3],
                            out byte r, out byte g, out byte b);
                        srcIdx += 4;

                        rasterSpan[dstIdx] = r;
                        rasterSpan[dstIdx + 1] = g;
                        rasterSpan[dstIdx + 2] = b;
                        rasterSpan[dstIdx + 3] = getImageAlphaChannel(p, maskSpan, r, g, b);
                        dstIdx += 4;
                    }

                    return true;
                }

                if (numberOfComponents == 3)
                {
                    int srcIdx = 0;
                    int dstIdx = 0;
                    for (int p = 0; p < pixelCount; p++)
                    {
                        ref byte r = ref imageSpan[srcIdx];
                        ref byte g = ref imageSpan[srcIdx + 1];
                        ref byte b = ref imageSpan[srcIdx + 2];
                        srcIdx += 3;

                        rasterSpan[dstIdx] = r;
                        rasterSpan[dstIdx + 1] = g;
                        rasterSpan[dstIdx + 2] = b;
                        rasterSpan[dstIdx + 3] = getImageAlphaChannel(p, maskSpan, r, g, b);
                        dstIdx += 4;
                    }

                    return true;
                }

                if (numberOfComponents == 1)
                {
                    if (isRgba)
                    {
                        // Handle gray scale image as RGBA because we have an alpha channel
                        int dstIdx = 0;
                        for (int p = 0; p < pixelCount; p++)
                        {
                            ref byte gv = ref imageSpan[p];
                            rasterSpan[dstIdx] = gv;
                            rasterSpan[dstIdx + 1] = gv;
                            rasterSpan[dstIdx + 2] = gv;
                            rasterSpan[dstIdx + 3] = getImageAlphaChannel(p, maskSpan, gv, gv, gv);
                            dstIdx += 4;
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

                    imageSpan.CopyTo(rasterSpan);

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
