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
using SkiaSharp;
#if NET7_0_OR_GREATER
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
#endif

namespace UglyToad.PdfPig.Rendering.Skia.Helpers
{
    /// <summary>
    /// Builds and samples a 1-D parametric colour lookup table for function-based mesh shadings
    /// (Type 6 Coons / Type 7 Tensor patches with a Function present).
    /// <para>
    /// When a Function is present the per-vertex stream carries a single parametric value
    /// <c>t</c>; the final colour is <c>ColorSpace.GetSKColor(Function.Eval(t))</c>, a mapping
    /// that depends only on <c>t</c> and is therefore identical for every patch and every texel
    /// of a given shading. This type pre-evaluates that mapping once into a LUT so the per-texel
    /// patch-texture fill becomes a bilinear blend plus a table lookup instead of a Function
    /// evaluation and colour-space conversion per texel.
    /// </para>
    /// </summary>
    public static class ParametricShadingTexture
    {
        /// <summary>
        /// Packs an <see cref="SKColor"/> into a little-endian <see cref="uint"/> whose bytes are,
        /// in ascending memory order, R, G, B, A — matching an <see cref="SKColorType.Rgba8888"/>
        /// pixel so the packed value can be written straight into the bitmap backing buffer.
        /// </summary>
        public static uint PackRgba(SKColor c)
        {
            return (uint)(c.Red | (c.Green << 8) | (c.Blue << 16) | (c.Alpha << 24));
        }

        /// <summary>
        /// Maps a parametric value <paramref name="t"/> within <c>[<paramref name="lo"/>, <paramref name="hi"/>]</c>
        /// to the nearest LUT index in <c>[0, <paramref name="n"/> - 1]</c>.
        /// </summary>
        public static int IndexOf(double t, double lo, double hi, int n)
        {
            if (n <= 1 || hi <= lo)
            {
                return 0;
            }

            double f = (t - lo) / (hi - lo);
            int idx = (int)Math.Round(f * (n - 1));
            if (idx < 0)
            {
                return 0;
            }

            return idx >= n ? n - 1 : idx;
        }

        /// <summary>
        /// Fills <paramref name="lut"/> with <c><paramref name="lut"/>.Length</c> packed-RGBA colour
        /// entries sampled uniformly across <c>[<paramref name="lo"/>, <paramref name="hi"/>]</c>,
        /// evaluating <paramref name="eval"/> once per entry.
        /// </summary>
        public static void BuildLut(Func<double, SKColor> eval, double lo, double hi, Span<uint> lut)
        {
            int n = lut.Length;
            if (n == 0)
            {
                return;
            }

            if (n == 1 || hi <= lo)
            {
                lut[0] = PackRgba(eval(lo));
                for (int k = 1; k < n; k++)
                {
                    lut[k] = lut[0];
                }
                return;
            }

            double step = (hi - lo) / (n - 1);
            for (int k = 0; k < n; k++)
            {
                lut[k] = PackRgba(eval(lo + k * step));
            }
        }

        /// <summary>
        /// Fills a <paramref name="texSize"/>² <see cref="SKColorType.Rgba8888"/> pixel buffer by
        /// bilinearly interpolating the four corner parametric values across the (u, v) unit square
        /// and looking the result up in <paramref name="lut"/>. Corner index convention matches the
        /// patch tessellators: [0] = (0,0), [1] = (1,0), [2] = (1,1), [3] = (0,1).
        /// </summary>
        public static void Fill(Span<byte> pixels, int texSize,
            double cc0, double cc1, double cc2, double cc3,
            ReadOnlySpan<uint> lut, double lo, double hi)
        {
            int n = lut.Length;
            if (n == 0 || texSize <= 0)
            {
                return;
            }

            // Degenerate parametric range (or single-entry LUT / single texel): every texel maps to
            // the same LUT entry. Handling it here also keeps the (hi - lo) division out of the
            // affine index ramp the hot paths rely on.
            if (hi <= lo || n == 1 || texSize == 1)
            {
                FillSolid(pixels, texSize, lut[IndexOf(cc0, lo, hi, n)]);
                return;
            }

#if NET7_0_OR_GREATER
            if (Avx2.IsSupported)
            {
                FillAvx2(pixels, texSize, cc0, cc1, cc2, cc3, lut, lo, hi);
                return;
            }
#endif
            FillScalar(pixels, texSize, cc0, cc1, cc2, cc3, lut, lo, hi);
        }

        /// <summary>
        /// Portable scalar fill. The nearest LUT index is affine along each row
        /// (<c>idx(i) = round(A + i·B)</c>), so the per-texel division and bilinear recompute of the
        /// original form collapse to a single multiply-add plus a round and clamp.
        /// </summary>
        private static void FillScalar(Span<byte> pixels, int texSize,
            double cc0, double cc1, double cc2, double cc3,
            ReadOnlySpan<uint> lut, double lo, double hi)
        {
            int n = lut.Length;
            double invDen = 1.0 / (texSize - 1);
            double scale = (n - 1) / (hi - lo);
            int rowStride = texSize * 4;

            for (int j = 0; j < texSize; j++)
            {
                double v = j * invDen;
                double oneMinusV = 1.0 - v;
                double left = oneMinusV * cc0 + v * cc3;
                double right = oneMinusV * cc1 + v * cc2;

                double a = (left - lo) * scale;
                double b = invDen * (right - left) * scale;
                int rowOffset = j * rowStride;

                for (int i = 0; i < texSize; i++)
                {
                    int idx = ClampIndex((int)Math.Round(a + i * b), n);
                    uint packed = lut[idx];

                    int p = rowOffset + i * 4;
                    pixels[p] = (byte)packed;
                    pixels[p + 1] = (byte)(packed >> 8);
                    pixels[p + 2] = (byte)(packed >> 16);
                    pixels[p + 3] = (byte)(packed >> 24);
                }
            }
        }

#if NET7_0_OR_GREATER
        /// <summary>
        /// AVX2 fill: processes eight texels per iteration. The affine index ramp is evaluated in a
        /// <see cref="Vector256{T}"/>, rounded to nearest-even via CVTPS2DQ, clamped, and used to
        /// gather eight packed colours from the LUT which are stored straight into the row.
        /// A scalar tail handles the remaining <c>texSize % 8</c> texels per row.
        /// </summary>
        private static unsafe void FillAvx2(Span<byte> pixels, int texSize,
            double cc0, double cc1, double cc2, double cc3,
            ReadOnlySpan<uint> lut, double lo, double hi)
        {
            int n = lut.Length;
            double invDen = 1.0 / (texSize - 1);
            double scale = (n - 1) / (hi - lo);

            Span<uint> rowsU = MemoryMarshal.Cast<byte, uint>(pixels);
            Vector256<float> laneOffset = Vector256.Create(0f, 1f, 2f, 3f, 4f, 5f, 6f, 7f);
            Vector256<int> zero = Vector256<int>.Zero;
            Vector256<int> maxIdx = Vector256.Create(n - 1);
            const int width = 8;

            fixed (uint* lutPtr = lut)
            {
                for (int j = 0; j < texSize; j++)
                {
                    double v = j * invDen;
                    double oneMinusV = 1.0 - v;
                    double left = oneMinusV * cc0 + v * cc3;
                    double right = oneMinusV * cc1 + v * cc2;

                    double a = (left - lo) * scale;
                    double b = invDen * (right - left) * scale;
                    Vector256<float> aVec = Vector256.Create((float)a);
                    Vector256<float> bVec = Vector256.Create((float)b);

                    int baseU = j * texSize;
                    int i = 0;
                    for (; i + width <= texSize; i += width)
                    {
                        Vector256<float> iVec = Vector256.Create((float)i) + laneOffset;
                        Vector256<float> f = aVec + iVec * bVec;
                        Vector256<int> idx = Avx.ConvertToVector256Int32(f); // round-to-nearest-even
                        idx = Vector256.Min(Vector256.Max(idx, zero), maxIdx);

                        Vector256<uint> colors = Avx2.GatherVector256(lutPtr, idx, 4);
                        colors.StoreUnsafe(ref rowsU[baseU + i]);
                    }

                    for (; i < texSize; i++)
                    {
                        int idx = ClampIndex((int)Math.Round(a + i * b), n);
                        rowsU[baseU + i] = lutPtr[idx];
                    }
                }
            }
        }
#endif

        private static void FillSolid(Span<byte> pixels, int texSize, uint packed)
        {
            byte r = (byte)packed;
            byte g = (byte)(packed >> 8);
            byte b = (byte)(packed >> 16);
            byte a = (byte)(packed >> 24);

            int count = texSize * texSize;
            for (int k = 0; k < count; k++)
            {
                int p = k * 4;
                pixels[p] = r;
                pixels[p + 1] = g;
                pixels[p + 2] = b;
                pixels[p + 3] = a;
            }
        }

        private static int ClampIndex(int idx, int n)
        {
            if (idx < 0)
            {
                return 0;
            }

            return idx >= n ? n - 1 : idx;
        }
    }
}
