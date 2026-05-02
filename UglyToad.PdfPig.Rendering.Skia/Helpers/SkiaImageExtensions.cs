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
using System.Numerics;
using System.Runtime.InteropServices;
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
        // Alpha-source selectors used by the per-pixel loops in TryGenerate. Hoisting the
        // selection out of the loop body lets the JIT branch-predict cleanly and avoids the
        // per-pixel delegate dispatch + closure capture the previous Func-based approach paid.
        private const int AlphaOpaque = 0;
        private const int AlphaMask = 1;
        private const int AlphaMaskInv = 2;
        private const int AlphaColourKey = 3;

        private static byte ResolveAlpha(int alphaMode, int pixelIndex,
            byte r, byte g, byte b, ReadOnlySpan<byte> maskSpan,
            byte rMin, byte gMin, byte bMin, byte rMax, byte gMax, byte bMax)
        {
            switch (alphaMode)
            {
                case AlphaMask:
                    return maskSpan[pixelIndex];
                case AlphaMaskInv:
                    return (byte)~maskSpan[pixelIndex];
                case AlphaColourKey:
                    if (rMin <= r && r <= rMax && gMin <= g && g <= gMax && bMin <= b && b <= bMax)
                    {
                        return byte.MinValue;
                    }
                    return byte.MaxValue;
                default:
                    return byte.MaxValue;
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

        internal static bool TryGenerate(this IPdfImage pdfImage, out SKBitmap? skBitmap, out SKBitmap? alphaMask, ILog? log)
        {
            skBitmap = null;
            alphaMask = null;

            if (!IsValidColorSpace(pdfImage) || !pdfImage.TryGetBytesAsMemory(out var imageMemory))
            {
                return false;
            }

            // Gray + SMask fast path: keep the colour image as Gray8 and return the mask
            // separately as Alpha8 so the renderer can composite via Skia (DstIn) — saves the
            // per-pixel alpha bake and a 4× memory expansion of the colour buffer. Limited to
            // SMask masks where the gray values directly represent alpha (PDF §11.6.5.2).
            // IsImageMask stencils and colour-key /Mask arrays still go through the baked path.
            if (!pdfImage.IsImageMask
                && pdfImage.ColorSpaceDetails!.BaseNumberOfColorComponents == 1
                && pdfImage.MaskImage is not null
                && !pdfImage.MaskImage.IsImageMask
                && !pdfImage.ImageDictionary.ContainsKey(NameToken.Mask)
                && TryGenerateGrayWithSeparateMask(pdfImage, imageMemory.Span, out skBitmap, out alphaMask, log))
            {
                return true;
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

                // Resolve the alpha source ONCE, outside any per-pixel loop. The value of
                // `alphaMode` selects between four sources used by the inner loops:
                //   AlphaOpaque    — no mask, alpha is always 0xFF
                //   AlphaMask      — mask SKBitmap, alpha = maskSpan[pixelIndex] (PDF SMask)
                //   AlphaMaskInv   — mask SKBitmap with inverted bytes (mask is itself an
                //                    image-mask stencil — see MOZILLA-LINK-3264-0.pdf etc.)
                //   AlphaColourKey — /Mask array range test on the RGB triple
                ReadOnlySpan<byte> maskSpan = default;
                byte rMin = 0, gMin = 0, bMin = 0, rMax = 0, gMax = 0, bMax = 0;
                int alphaMode = AlphaOpaque;

                if (pdfImage.MaskImage is not null)
                {
                    if (pdfImage.MaskImage.TryGenerate(out mask, out SKBitmap? nestedAlphaMask, log))
                    {
                        // The mask image's own SMask (if any) isn't honoured by the bake path —
                        // discard it. Rare in practice (mask of mask) and the visual cost is
                        // small compared to the cost of recursing further.
                        nestedAlphaMask?.Dispose();
                        mask!.SetImmutable();

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
                            // Hoist the mask pixel buffer once — the previous closure refetched
                            // it on every pixel via SKBitmap.GetPixelSpan() (a P/Invoke each call).
                            maskSpan = mask.GetPixelSpan();
                            alphaMode = pdfImage.MaskImage.IsImageMask ? AlphaMaskInv : AlphaMask;

                            // Examples of docs that are decode inverse:
                            // MOZILLA-LINK-3264-0.pdf, MOZILLA-LINK-4246-2.pdf,
                            // MOZILLA-LINK-4293-0.pdf, MOZILLA-LINK-4314-0.pdf,
                            // MOZILLA-LINK-3758-0.pdf
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

                    alphaMode = AlphaColourKey;
                }

                skBitmap = new SKBitmap(info);
                Span<byte> rasterSpan = skBitmap.GetPixelSpan();
                int pixelCount = width * height;

                // The Rgba8888 paths below pack each pixel as a single uint. Skia stores
                // Rgba8888 as bytes R, G, B, A in memory order, which matches the low-to-high
                // byte layout of `(A<<24) | (B<<16) | (G<<8) | R` only on little-endian. All
                // platforms .NET supports in practice (x86/x64/ARM64 on Windows/Linux/macOS)
                // are little-endian; if you ever need big-endian, reinstate the byte writes.
                if (numberOfComponents == 4)
                {
                    Span<uint> dstU32 = MemoryMarshal.Cast<byte, uint>(rasterSpan);
                    int srcIdx = 0;
                    int p = 0;

                    // SIMD body: convert Vector<float>.Count CMYK pixels per iteration.
                    // Skipped on hardware without vector acceleration since System.Numerics
                    // would emulate it via scalar code, which is slower than the dedicated
                    // scalar path below. The CMYK→RGB polynomial dominates, so even partial
                    // vectorisation (4-wide on SSE, 8-wide on AVX) is a meaningful speedup.
                    if (Vector.IsHardwareAccelerated)
                    {
                        int vWidth = Vector<float>.Count;
                        float[] cBuf = new float[vWidth];
                        float[] mBuf = new float[vWidth];
                        float[] yBuf = new float[vWidth];
                        float[] kBuf = new float[vWidth];
                        float[] rBuf = new float[vWidth];
                        float[] gBuf = new float[vWidth];
                        float[] bBuf = new float[vWidth];

                        while (p + vWidth <= pixelCount)
                        {
                            // Deinterleave the CMYK CMYK CMYK… stream into planar buffers,
                            // applying the (255 − x) inversion ApproximateCmykToRgb does
                            // internally so the vector path receives ready-to-use values.
                            for (int i = 0; i < vWidth; i++)
                            {
                                int s = srcIdx + i * 4;
                                cBuf[i] = 255f - imageSpan[s];
                                mBuf[i] = 255f - imageSpan[s + 1];
                                yBuf[i] = 255f - imageSpan[s + 2];
                                kBuf[i] = 255f - imageSpan[s + 3];
                            }

                            SkiaExtensions.ApproximateCmykToRgbVector(
                                new Vector<float>(cBuf), new Vector<float>(mBuf),
                                new Vector<float>(yBuf), new Vector<float>(kBuf),
                                out Vector<float> rV, out Vector<float> gV, out Vector<float> bV);

                            rV.CopyTo(rBuf);
                            gV.CopyTo(gBuf);
                            bV.CopyTo(bBuf);

                            for (int i = 0; i < vWidth; i++)
                            {
                                byte r = (byte)rBuf[i];
                                byte g = (byte)gBuf[i];
                                byte b = (byte)bBuf[i];
                                byte a = ResolveAlpha(alphaMode, p + i, r, g, b, maskSpan,
                                    rMin, gMin, bMin, rMax, gMax, bMax);
                                dstU32[p + i] = ((uint)a << 24) | ((uint)b << 16) | ((uint)g << 8) | r;
                            }

                            p += vWidth;
                            srcIdx += vWidth * 4;
                        }
                    }

                    // Scalar tail (also handles the entire image when no SIMD acceleration).
                    for (; p < pixelCount; p++, srcIdx += 4)
                    {
                        SkiaExtensions.ApproximateCmykToRgb(
                            in imageSpan[srcIdx], in imageSpan[srcIdx + 1],
                            in imageSpan[srcIdx + 2], in imageSpan[srcIdx + 3],
                            out byte r, out byte g, out byte b);

                        byte a = ResolveAlpha(alphaMode, p, r, g, b, maskSpan,
                            rMin, gMin, bMin, rMax, gMax, bMax);
                        dstU32[p] = ((uint)a << 24) | ((uint)b << 16) | ((uint)g << 8) | r;
                    }

                    return true;
                }

                if (numberOfComponents == 3)
                {
                    Span<uint> dstU32 = MemoryMarshal.Cast<byte, uint>(rasterSpan);
                    int srcIdx = 0;
                    for (int p = 0; p < pixelCount; p++)
                    {
                        byte r = imageSpan[srcIdx];
                        byte g = imageSpan[srcIdx + 1];
                        byte b = imageSpan[srcIdx + 2];
                        srcIdx += 3;

                        byte a = ResolveAlpha(alphaMode, p, r, g, b, maskSpan,
                            rMin, gMin, bMin, rMax, gMax, bMax);
                        dstU32[p] = ((uint)a << 24) | ((uint)b << 16) | ((uint)g << 8) | r;
                    }

                    return true;
                }

                if (numberOfComponents == 1)
                {
                    if (isRgba)
                    {
                        // Gray + per-pixel alpha (colour-key /Mask, or IsImageMask SMask — the
                        // gray + SMask case takes the separate-mask fast path in TryGenerate).
                        // `gv * 0x010101u` broadcasts the gray byte across the R, G, B lanes
                        // of the packed uint; alpha goes in the high byte.
                        Span<uint> dstU32 = MemoryMarshal.Cast<byte, uint>(rasterSpan);
                        for (int p = 0; p < pixelCount; p++)
                        {
                            byte gv = imageSpan[p];
                            byte a = ResolveAlpha(alphaMode, p, gv, gv, gv, maskSpan,
                                rMin, gMin, bMin, rMax, gMax, bMax);
                            dstU32[p] = ((uint)a << 24) | (uint)gv * 0x010101u;
                        }

                        return true;
                    }

                    // Pure gray (no mask). Slice to pixelCount because imageSpan can carry
                    // trailing EOL bytes accepted by IsImageArrayCorrectlySized.
                    ReadOnlySpan<byte> srcGray = imageSpan.Slice(0, pixelCount);
                    Span<byte> dstGray = rasterSpan.Slice(0, pixelCount);
                    if (pdfImage.NeedsReverseDecode())
                    {
                        // Bitwise-NOT 8 bytes per iteration via ulong cast; tail loop handles
                        // the trailing 0–7 bytes when pixelCount isn't a multiple of 8.
                        ReadOnlySpan<ulong> srcU64 = MemoryMarshal.Cast<byte, ulong>(srcGray);
                        Span<ulong> dstU64 = MemoryMarshal.Cast<byte, ulong>(dstGray);
                        for (int i = 0; i < dstU64.Length; i++)
                        {
                            dstU64[i] = ~srcU64[i];
                        }
                        for (int i = dstU64.Length * 8; i < pixelCount; i++)
                        {
                            dstGray[i] = (byte)~srcGray[i];
                        }
                    }
                    else
                    {
                        srcGray.CopyTo(dstGray);
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

        private static bool TryGenerateGrayWithSeparateMask(IPdfImage pdfImage, Span<byte> rawSpan,
            out SKBitmap? grayBitmap, out SKBitmap? alphaMask, ILog? log)
        {
            grayBitmap = null;
            alphaMask = null;

            int width = pdfImage.WidthInSamples;
            int height = pdfImage.HeightInSamples;

            rawSpan = ColorSpaceDetailsByteConverter.Convert(pdfImage.ColorSpaceDetails!, rawSpan,
                pdfImage.BitsPerComponent, width, height);

            if (!IsImageArrayCorrectlySized(pdfImage, rawSpan))
            {
                return false;
            }

            // Recursively build the SMask. For a 1-component SMask without its own subsidiary
            // mask this returns Gray8; if the mask itself has nested alpha, fall back to the
            // baked path rather than handle mask-of-mask here.
            if (!pdfImage.MaskImage!.TryGenerate(out SKBitmap? maskBitmap, out SKBitmap? nestedAlpha, log)
                || maskBitmap is null
                || nestedAlpha is not null
                || maskBitmap.ColorType != SKColorType.Gray8)
            {
                maskBitmap?.Dispose();
                nestedAlpha?.Dispose();
                return false;
            }

            SKBitmap? gray = null;
            SKBitmap? alpha = null;

            try
            {
                maskBitmap.SetImmutable();

                // Resize the SMask to match the colour image dimensions if needed (keeps the
                // 1:1 mapping with rawSpan, otherwise the Alpha8 / Gray8 bytes wouldn't line up).
                if (maskBitmap.Width != width || maskBitmap.Height != height)
                {
                    var resizedInfo = new SKImageInfo(width, height, maskBitmap.Info.ColorType, maskBitmap.Info.AlphaType);
                    SKBitmap resized = maskBitmap.Resize(resizedInfo, pdfImage.MaskImage.GetSamplingOption());
                    if (resized is null)
                    {
                        return false;
                    }
                    maskBitmap.Dispose();
                    maskBitmap = resized;
                    maskBitmap.SetImmutable();
                }

                // Reinterpret the Gray8 mask as Alpha8 — same byte layout, different ColorType.
                // Skia samples Alpha8 as alpha-only so DstIn at draw time treats the gray values
                // as alpha directly, which matches PDF SMask semantics for 1-component masks.
                var alphaInfo = new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul);
                alpha = new SKBitmap(alphaInfo);
                maskBitmap.GetPixelSpan().CopyTo(alpha.GetPixelSpan());
                alpha.SetImmutable();

                // Build the Gray8 colour bitmap (mirrors the 1-component branch of TryGenerate's
                // baked path, including NeedsReverseDecode).
                var grayInfo = new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
                gray = new SKBitmap(grayInfo);
                Span<byte> graySpan = gray.GetPixelSpan();
                if (pdfImage.NeedsReverseDecode())
                {
                    for (int i = 0; i < rawSpan.Length; i++)
                    {
                        graySpan[i] = (byte)~rawSpan[i];
                    }
                }
                else
                {
                    rawSpan.CopyTo(graySpan);
                }
                gray.SetImmutable();

                grayBitmap = gray;
                alphaMask = alpha;
                gray = null;
                alpha = null;
                return true;
            }
            catch (Exception ex)
            {
                log?.Error($"Failed to generate gray bitmap with separate mask: {ex.Message}");
                return false;
            }
            finally
            {
                maskBitmap.Dispose();
                gray?.Dispose();
                alpha?.Dispose();
            }
        }

        private static SKBitmap BakeGrayAndAlphaToRgba(SKBitmap gray, SKBitmap alpha)
        {
            var info = new SKImageInfo(gray.Width, gray.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            var rgba = new SKBitmap(info);
            Span<byte> dst = rgba.GetPixelSpan();
            ReadOnlySpan<byte> grayPixels = gray.GetPixelSpan();
            ReadOnlySpan<byte> alphaPixels = alpha.GetPixelSpan();
            int n = grayPixels.Length;
            int j = 0;
            for (int i = 0; i < n; i++)
            {
                byte g = grayPixels[i];
                dst[j++] = g;
                dst[j++] = g;
                dst[j++] = g;
                dst[j++] = alphaPixels[i];
            }
            rgba.SetImmutable();
            return rgba;
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
            if (pdfImage.TryGenerate(out var bitmap, out var alphaMask, log))
            {
                if (alphaMask is null)
                {
                    return bitmap!;
                }
                // Public API contract is a single SKBitmap — bake gray + alpha into RGBA here.
                try
                {
                    return BakeGrayAndAlphaToRgba(bitmap!, alphaMask);
                }
                finally
                {
                    bitmap!.Dispose();
                    alphaMask.Dispose();
                }
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
