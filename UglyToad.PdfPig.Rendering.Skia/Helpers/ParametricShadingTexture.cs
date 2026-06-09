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
using System.Runtime.CompilerServices;
using SkiaSharp;

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
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint PackRgba(SKColor c)
        {
            return (uint)(c.Red | (c.Green << 8) | (c.Blue << 16) | (c.Alpha << 24));
        }

        /// <summary>
        /// Maps a parametric value <paramref name="t"/> within <c>[<paramref name="lo"/>, <paramref name="hi"/>]</c>
        /// to the nearest LUT index in <c>[0, <paramref name="n"/> - 1]</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int IndexOf(double t, double lo, double hi, int n)
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
            double invDen = 1.0 / (texSize - 1);
            int rowStride = texSize * 4;

            for (int j = 0; j < texSize; j++)
            {
                double v = j * invDen;
                double oneMinusV = 1.0 - v;
                int rowOffset = j * rowStride;

                // Bilinear blend of a scalar collapses to an affine ramp along the row:
                //   t(u) = (1-u)·L + u·R  with L,R fixed per row.
                double left = oneMinusV * cc0 + v * cc3;
                double right = oneMinusV * cc1 + v * cc2;

                for (int i = 0; i < texSize; i++)
                {
                    double u = i * invDen;
                    double t = (1.0 - u) * left + u * right;

                    int idx = IndexOf(t, lo, hi, n);
                    uint packed = lut[idx];

                    int p = rowOffset + i * 4;
                    pixels[p] = (byte)packed;
                    pixels[p + 1] = (byte)(packed >> 8);
                    pixels[p + 2] = (byte)(packed >> 16);
                    pixels[p + 3] = (byte)(packed >> 24);
                }
            }
        }
    }
}
