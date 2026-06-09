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

// Guarded to the Local project reference: the published NuGet baseline ('Latest' job) predates the
// public ParametricShadingTexture API, so this class must not be compiled against it.
#if PDFPIGSKIA_LOCAL
using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using UglyToad.PdfPig.Rendering.Skia.Helpers;

namespace UglyToad.PdfPig.Rendering.Skia.Benchmarks;

/// <summary>
/// Isolates the per-texel patch-texture fill loop so the AVX2 path can be compared against an
/// equivalent scalar implementation of the same affine index-ramp algorithm.
/// </summary>
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 7)]
[MemoryDiagnoser(displayGenColumns: false)]
public class ParametricFillBenchmarks
{
    private const int Lo = 0;
    private const int Hi = 1;

    [Params(512)]
    public int TexSize { get; set; }

    [Params(2 * 512)]
    public int LutSize { get; set; }

    private uint[] _lut = Array.Empty<uint>();
    private byte[] _pixels = Array.Empty<byte>();

    // Representative non-degenerate, non-axis-aligned corner parametric values.
    private const double Cc0 = 0.10, Cc1 = 0.90, Cc2 = 0.70, Cc3 = 0.30;

    [GlobalSetup]
    public void Setup()
    {
        _lut = new uint[LutSize];
        for (int k = 0; k < LutSize; k++)
        {
            // Arbitrary deterministic colours; the fill cost is independent of the LUT contents.
            byte g = (byte)(k * 255 / (LutSize - 1));
            _lut[k] = (uint)(g | (g << 8) | (g << 16) | (255 << 24));
        }

        _pixels = new byte[TexSize * TexSize * 4];
    }

    [Benchmark(Baseline = true)]
    public void Scalar()
    {
        FillScalarBaseline(_pixels, TexSize, Cc0, Cc1, Cc2, Cc3, _lut, Lo, Hi);
    }

    [Benchmark]
    public void Avx2OrBest()
    {
        // ParametricShadingTexture.Fill auto-selects the AVX2 path when supported.
        ParametricShadingTexture.Fill(_pixels, TexSize, Cc0, Cc1, Cc2, Cc3, _lut, Lo, Hi);
    }

    // Mirror of the production scalar fallback (affine index ramp), kept here so the AVX2 path has a
    // like-for-like scalar comparand independent of which path Fill() chooses at runtime.
    private static void FillScalarBaseline(Span<byte> pixels, int texSize,
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
                int idx = (int)Math.Round(a + i * b);
                if (idx < 0) idx = 0; else if (idx >= n) idx = n - 1;
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
#endif
