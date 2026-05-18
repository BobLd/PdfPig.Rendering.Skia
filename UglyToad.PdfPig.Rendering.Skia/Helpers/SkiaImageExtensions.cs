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
using UglyToad.PdfPig.Graphics.Colors.Icc;
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

        private static bool IsImageArrayCorrectlySized(IPdfImage pdfImage, ReadOnlySpan<byte> bytesPure, out int requiredSize)
        {
            var actualSize = bytesPure.Length;
            requiredSize = (pdfImage.WidthInSamples * pdfImage.HeightInSamples * pdfImage.ColorSpaceDetails!.BaseNumberOfColorComponents);

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

        private static bool TryGenerate(this IPdfImage pdfImage, out SKBitmap? skBitmap, ILog? log,
            IIccTransform? outputIntentTransform)
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
                if (TryGetTruncationSize(pdfImage, imageSpan.Length, out int expectedSize))
                {
                    imageSpan = imageSpan.Slice(0, expectedSize);
                }

                int width = pdfImage.WidthInSamples;
                int height = pdfImage.HeightInSamples;

                imageSpan = ColorSpaceDetailsByteConverter.Convert(pdfImage.ColorSpaceDetails!, imageSpan,
                    pdfImage.BitsPerComponent, width, height, pdfImage.Decode, pdfImage.RenderingIntent);

                var numberOfComponents = pdfImage.ColorSpaceDetails!.BaseNumberOfColorComponents;

                if (!IsImageArrayCorrectlySized(pdfImage, imageSpan, out int requiredSize))
                {
                    if (requiredSize >= imageSpan.Length)
                    {
                        return false;
                    }

                    // We assume we can ignore the last bytes
                    imageSpan = imageSpan.Slice(0, requiredSize);
                }

                // PDF/X output intent: a DeviceCMYK/RGB/Gray raster image is characterized by the
                // document's (or page's) output intent, exactly like the equivalent device vector
                // colour. When an applicable transform is supplied (resolved by the renderer only
                // for matching device colour spaces), convert the whole device buffer to sRGB
                // up-front and continue as a 3-component RGB image, so a managed device image and a
                // managed device vector fill render identically. Skipped when a colour-key /Mask is
                // present, whose range test is defined in the original device colour space.
                if (outputIntentTransform is not null
                    && !pdfImage.ImageDictionary.TryGet(NameToken.Mask, out ArrayToken _))
                {
                    int pixels = width * height;
                    int profileComponents = outputIntentTransform.NumberOfComponents;

                    // Device buffer to feed the profile. When the device space matches the profile it is
                    // used directly; a DeviceGray buffer is expanded neutrally into the profile's colour
                    // space (grey g -> (g,g,g) for RGB, or (0,0,0,255-g) i.e. the black channel for CMYK)
                    // so grey images share the managed space (mirrors ColorSpaceContext for vectors).
                    ReadOnlySpan<byte> deviceSpan = numberOfComponents == profileComponents
                        ? imageSpan.Slice(0, pixels * numberOfComponents)
                        : ExpandGrayToProfile(imageSpan.Slice(0, pixels * numberOfComponents), numberOfComponents, profileComponents);

                    if (!deviceSpan.IsEmpty)
                    {
                        var managedRgb = new byte[pixels * 3];
                        outputIntentTransform.Transform(deviceSpan, managedRgb);
                        imageSpan = managedRgb;
                        numberOfComponents = 3;
                    }
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
                    alphaType = SKAlphaType.Premul;
                }

                var info = new SKImageInfo(width, height, colorSpace, alphaType);

                byte rMin = 0, gMin = 0, bMin = 0, rMax = 0, gMax = 0, bMax = 0;
                ImageAlphaType alphaMode = ImageAlphaType.Opaque;

                // A soft mask (/SMask) or stencil mask (/Mask) supplied as a separate image is kept at
                // its own resolution here and composited once the colour has been generated (see the
                // CompositeMaskImage call below). Compositing at the higher of the two resolutions
                // avoids destroying a high-resolution mask by collapsing it down to the base image size.
                bool compositeMaskImage = false;
                if (pdfImage.MaskImage is not null)
                {
                    // A separate /SMask or stencil /Mask image is a greyscale alpha source, not a
                    // colour to manage, so it is never routed through the output intent.
                    if (pdfImage.MaskImage.TryGenerate(out mask!, log, null) && !mask.IsEmpty)
                    {
                        alphaMode = pdfImage.MaskImage.IsImageMask ? ImageAlphaType.MaskInv : ImageAlphaType.Mask;
                        compositeMaskImage = true;
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
                        1,
                        null,
                        pdfImage.RenderingIntent);

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

                // The external mask image (if any) is applied after this colour pass, at the composite
                // resolution, so here we only resolve the inline colour-key alpha (or leave opaque).
                ImageAlphaResolver getImageAlphaChannel = ResolveAlpha(
                    alphaMode == ImageAlphaType.ColourKey ? ImageAlphaType.ColourKey : ImageAlphaType.Opaque,
                    rMin, gMin, bMin, rMax, gMax, bMax);
                ReadOnlySpan<byte> maskSpan = ReadOnlySpan<byte>.Empty;

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
                }
                else if (numberOfComponents == 3)
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
                }
                else if (numberOfComponents == 1)
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
                    }
                    else
                    {
                        // Gray8, no alpha channel: no separate mask image is possible here.
                        imageSpan.CopyTo(rasterSpan);
                        return true;
                    }
                }
                else
                {
                    throw new Exception($"Could not process image with ColorSpace={pdfImage.ColorSpaceDetails.BaseType}, numberOfComponents={numberOfComponents}.");
                }

                // Composite the separate mask image (SMask / stencil) at the higher of the colour and
                // mask resolutions so the mask's detail is preserved (we upscale the - often tiny -
                // base colour rather than downsampling the mask onto it).
                if (compositeMaskImage)
                {
                    skBitmap = CompositeMaskImage(skBitmap, mask!, alphaMode == ImageAlphaType.MaskInv,
                        pdfImage.GetSamplingOption(), pdfImage.MaskImage!.GetSamplingOption());
                }

                return true;
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

        /// <summary>
        /// Composites a separate mask image (a <c>/SMask</c> soft mask or a <c>/Mask</c> stencil) onto
        /// the generated colour bitmap. The two images may have different resolutions; per the PDF
        /// specification each is sampled independently over the same unit square. We therefore
        /// composite at the higher of the two resolutions - upscaling the colour when necessary - so a
        /// high-resolution mask is never collapsed onto a tiny base colour image (which would discard
        /// all of its detail and leave a flat block of colour).
        /// </summary>
        /// <param name="colour">The opaque colour bitmap (RGBA, <see cref="SKAlphaType.Unpremul"/>). Disposed if replaced.</param>
        /// <param name="mask">The mask bitmap (single byte per pixel: <c>Gray8</c> for an SMask, <c>Alpha8</c> for a stencil).</param>
        /// <param name="invertMask">When <see langword="true"/> the mask byte is inverted (stencil image masks).</param>
        /// <param name="colourSampling">Sampling used when upscaling the colour bitmap.</param>
        /// <param name="maskSampling">Sampling used when resizing the mask bitmap.</param>
        private static SKBitmap CompositeMaskImage(SKBitmap colour, SKBitmap mask, bool invertMask,
            SKSamplingOptions colourSampling, SKSamplingOptions maskSampling)
        {
            int outWidth = Math.Max(colour.Info.Width, mask.Info.Width);
            int outHeight = Math.Max(colour.Info.Height, mask.Info.Height);

            // Upscale the colour to the composite resolution (no-op when it already matches).
            if (colour.Info.Width != outWidth || colour.Info.Height != outHeight)
            {
                var colourInfo = new SKImageInfo(outWidth, outHeight, colour.ColorType, colour.AlphaType);
                SKBitmap upscaled = colour.Resize(colourInfo, colourSampling);
                colour.Dispose();
                colour = upscaled;
            }

            // Bring the mask to the same resolution (no-op when it already matches).
            SKBitmap maskAtOut = mask;
            bool disposeMaskAtOut = false;
            if (mask.Info.Width != outWidth || mask.Info.Height != outHeight)
            {
                var maskInfo = new SKImageInfo(outWidth, outHeight, mask.ColorType, mask.AlphaType);
                maskAtOut = mask.Resize(maskInfo, maskSampling);
                disposeMaskAtOut = true;
            }

            try
            {
                Span<byte> dst = colour.GetPixelSpan();
                ReadOnlySpan<byte> maskSpan = maskAtOut.GetPixelSpan();
                int pixelCount = outWidth * outHeight;

                for (int p = 0; p < pixelCount; p++)
                {
                    dst[(p * 4) + 3] = invertMask ? (byte)~maskSpan[p] : maskSpan[p];
                }
            }
            finally
            {
                if (disposeMaskAtOut)
                {
                    maskAtOut.Dispose();
                }
            }

            return colour;
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
        /// Expand a single-component (DeviceGray) sample buffer into the output intent profile's colour
        /// space: grey <c>g</c> maps to <c>(g, g, g)</c> for a 3-component (RGB) profile, or to
        /// <c>(0, 0, 0, 255 - g)</c> — the black channel — for a 4-component (CMYK) profile. Returns an
        /// empty span for any other combination (caller then keeps the built-in conversion).
        /// </summary>
        private static ReadOnlySpan<byte> ExpandGrayToProfile(ReadOnlySpan<byte> gray, int sourceComponents, int profileComponents)
        {
            if (sourceComponents != 1)
            {
                return ReadOnlySpan<byte>.Empty;
            }

            int pixels = gray.Length;
            byte[] expanded = new byte[pixels * profileComponents];

            switch (profileComponents)
            {
                case 3:
                    for (int p = 0; p < pixels; p++)
                    {
                        int d = p * 3;
                        expanded[d] = expanded[d + 1] = expanded[d + 2] = gray[p];
                    }
                    break;
                case 4:
                    // C = M = Y = 0 (already zero); K = 255 - grey.
                    for (int p = 0; p < pixels; p++)
                    {
                        expanded[(p * 4) + 3] = (byte)(255 - gray[p]);
                    }
                    break;
                default:
                    return ReadOnlySpan<byte>.Empty;
            }

            return expanded;
        }

        private static bool TryGetTruncationSize(IPdfImage pdfImage, int decodedLength, out int expectedSize)
        {
            expectedSize = 0;

            if (pdfImage.BitsPerComponent != 8 || pdfImage.ColorSpaceDetails is null)
            {
                return false;
            }

            int actualComponents = pdfImage.ColorSpaceDetails.NumberOfColorComponents;
            var streamDictionary = pdfImage.ImageDictionary;

            // Only activate when /DecodeParms /Colors disagrees with the colour space.
            if (!streamDictionary.TryGet(NameToken.DecodeParms, out DictionaryToken? decodeParams) || decodeParams is null)
            {
                return false;
            }

            if (!decodeParams.TryGet(NameToken.Colors, out NumericToken? colorsToken) || colorsToken is null
                || colorsToken.Int == actualComponents)
            {
                return false;
            }

            expectedSize = pdfImage.WidthInSamples * pdfImage.HeightInSamples * actualComponents;

            // Truncate only if the decoder over-produced (the buggy producer pads to a
            // multi-component "row" boundary). Never grow data we don't have.
            if (decodedLength <= expectedSize)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Converts the specified <see cref="IPdfImage"/> to an <see cref="SKBitmap"/> instance.
        /// </summary>
        /// <param name="pdfImage">The PDF image to convert.</param>
        /// <param name="log"></param>
        /// <param name="outputIntentTransform">
        /// Optional output-intent transform used to colour-manage a <c>DeviceCMYK</c>/<c>DeviceRGB</c>/
        /// <c>DeviceGray</c> image through the document's (or page's) output intent. Should only be
        /// supplied when the image's device colour space matches the transform's component count;
        /// pass <c>null</c> (the default) to use the built-in approximation.
        /// </param>
        /// <returns>
        /// An <see cref="SKBitmap"/> representation of the provided <paramref name="pdfImage"/>.
        /// If the conversion fails, a fallback mechanism is used to create the image from raw bytes.
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="pdfImage"/> is <c>null</c>.</exception>
        /// <remarks>
        /// This method attempts to generate an <see cref="SKBitmap"/> using the image's data and color space.
        /// If the generation fails, it falls back to creating the image using encoded or raw byte data.
        /// </remarks>
        public static SKBitmap? GetSKBitmap(this IPdfImage pdfImage, ILog? log = null, IIccTransform? outputIntentTransform = null)
        {
            if (pdfImage.TryGenerate(out var bitmap, log, outputIntentTransform))
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
