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
using UglyToad.PdfPig.Rendering.Skia.Helpers;
using Xunit;

namespace UglyToad.PdfPig.Rendering.Skia.Tests
{
    public class ParametricShadingTextureTests
    {
        private static byte ToByteLevel(double t)
        {
            double v = t < 0 ? 0 : (t > 1 ? 1 : t);
            return (byte)Math.Round(v * 255.0);
        }

        // Smooth evaluator: grey ramp directly proportional to t.
        private static SKColor Grey(double t)
        {
            byte g = ToByteLevel(t);
            return new SKColor(g, g, g, 255);
        }

        // Hard step evaluator: black below 0.5, white at/above 0.5.
        private static SKColor Step(double t)
        {
            return t < 0.5 ? new SKColor(0, 0, 0, 255) : new SKColor(255, 255, 255, 255);
        }

        [Fact]
        public void PackRgba_PlacesChannelsInRgbaLittleEndianByteOrder()
        {
            uint packed = ParametricShadingTexture.PackRgba(new SKColor(10, 20, 30, 40));

            Assert.Equal(10u, packed & 0xFF);
            Assert.Equal(20u, (packed >> 8) & 0xFF);
            Assert.Equal(30u, (packed >> 16) & 0xFF);
            Assert.Equal(40u, (packed >> 24) & 0xFF);
        }

        [Fact]
        public void Fill_LinearEvaluator_MatchesDirectPerTexelEvalWithinOneLevel()
        {
            const int texSize = 16;
            const int n = 1024;
            const double cc0 = 0.0, cc1 = 1.0, cc2 = 0.5, cc3 = 0.25;
            const double lo = 0.0, hi = 1.0;

            uint[] lut = new uint[n];
            ParametricShadingTexture.BuildLut(Grey, lo, hi, lut);

            byte[] pixels = new byte[texSize * texSize * 4];
            ParametricShadingTexture.Fill(pixels, texSize, cc0, cc1, cc2, cc3, lut, lo, hi);

            double invDen = 1.0 / (texSize - 1);
            for (int j = 0; j < texSize; j++)
            {
                double v = j * invDen;
                for (int i = 0; i < texSize; i++)
                {
                    double u = i * invDen;
                    double t = (1 - u) * (1 - v) * cc0 + u * (1 - v) * cc1 + u * v * cc2 + (1 - u) * v * cc3;
                    SKColor expected = Grey(t);

                    int idx = (j * texSize + i) * 4;
                    Assert.True(Math.Abs(pixels[idx] - expected.Red) <= 1, $"R mismatch at ({i},{j})");
                    Assert.True(Math.Abs(pixels[idx + 1] - expected.Green) <= 1, $"G mismatch at ({i},{j})");
                    Assert.True(Math.Abs(pixels[idx + 2] - expected.Blue) <= 1, $"B mismatch at ({i},{j})");
                    Assert.Equal(expected.Alpha, pixels[idx + 3]);
                }
            }
        }

        [Fact]
        public void Fill_StepEvaluator_KeepsBoundaryWithinOneTexel()
        {
            const int texSize = 64;
            const int n = 1024;
            // Corners chosen so t == u along every row (v drops out): t boundary at u = 0.5.
            const double cc0 = 0.0, cc1 = 1.0, cc2 = 1.0, cc3 = 0.0;
            const double lo = 0.0, hi = 1.0;

            uint[] lut = new uint[n];
            ParametricShadingTexture.BuildLut(Step, lo, hi, lut);

            byte[] pixels = new byte[texSize * texSize * 4];
            ParametricShadingTexture.Fill(pixels, texSize, cc0, cc1, cc2, cc3, lut, lo, hi);

            // Bottom row (j = 0): first texel that came out white.
            int firstWhiteActual = -1;
            for (int i = 0; i < texSize; i++)
            {
                if (pixels[i * 4] == 255)
                {
                    firstWhiteActual = i;
                    break;
                }
            }

            double invDen = 1.0 / (texSize - 1);
            int firstWhiteExpected = -1;
            for (int i = 0; i < texSize; i++)
            {
                double u = i * invDen;
                if (!(u < 0.5))
                {
                    firstWhiteExpected = i;
                    break;
                }
            }

            Assert.InRange(firstWhiteActual, firstWhiteExpected - 1, firstWhiteExpected + 1);
        }
    }
}
