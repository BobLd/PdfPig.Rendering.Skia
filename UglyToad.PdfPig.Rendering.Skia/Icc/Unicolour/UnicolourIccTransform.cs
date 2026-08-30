// Copyright BobLd
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
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using UglyToad.PdfPig.Graphics.Colors.Icc;
using Wacton.Unicolour;
using Wacton.Unicolour.Icc;

namespace UglyToad.PdfPig.Rendering.Skia.Icc.Unicolour;

/// <summary>
/// The raw colour conversion for one configured <see cref="Configuration"/>: PDF components in, sRGB out,
/// with no caching.
/// <para>
/// Deliberately <b>not</b> an <see cref="IIccTransform"/>. Every transform handed to the core is a
/// <see cref="UnicolourIccTransformCached"/>, which wraps one of these and serves the interface itself, so
/// implementing it here too only invited a second, uncached copy of each entry point that nothing called
/// and that would have skipped the cache had anything started to.
/// </para>
/// </summary>
internal sealed class UnicolourIccTransform
{
    private readonly Configuration _config;
    private readonly bool _isInputLab;

    public int NumberOfComponents { get; }

    public UnicolourIccTransform(Configuration config, int numberOfComponents, bool isInputLab)
    {
        this._config = config;
        NumberOfComponents = numberOfComponents;
        this._isInputLab = isInputLab;
    }

    /// <summary>
    /// Convert ICC device channels already normalised to [0,1] to sRGB.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal (double r, double g, double b) ToRgbFromDevice(ReadOnlySpan<double> device)
    {
        return ToRgbFromDevice(device.ToArray());
    }

    /// <summary>
    /// Convert ICC device channels already normalised to [0,1] to sRGB.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal (double r, double g, double b) ToRgbFromDevice(double[] device)
    {
        var channels = new Channels(device);
        var uc = new Wacton.Unicolour.Unicolour(_config, channels);
        var rgb = uc.Rgb;
        return (Clip01(rgb.R), Clip01(rgb.G), Clip01(rgb.B));
    }

    /// <summary>
    /// Normalise PDF colour component values to ICC device channels in
    /// [0,1]. For a L*a*b* device colour space the incoming values are
    /// real Lab (L* in [0,100], a*/b* in [-128,127]) and must be encoded
    /// per the ICC.1 Lab convention (L*/100, (a*+128)/255, (b*+128)/255).
    /// All other device colour spaces already arrive in [0,1], so this is
    /// the identity copy.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EncodeDevice(ReadOnlySpan<double> values, Span<double> device)
    {
        if (_isInputLab && values.Length >= 3)
        {
            device[0] = Clip01(values[0] / 100.0);
            device[1] = Clip01((values[1] + 128.0) / 255.0);
            device[2] = Clip01((values[2] + 128.0) / 255.0);
            for (int i = 3; i < values.Length; i++)
            {
                device[i] = values[i];
            }
        }
        else
        {
            values.CopyTo(device);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Clip01(double v)
    {
        if (v <= 0.0) return 0.0;
        if (v >= 1.0) return 1.0;
        return v;
    }

}


internal sealed class UnicolourIccTransformCached : IIccTransform
{
    private readonly UnicolourIccTransform inner;
    private readonly ConcurrentDictionary<ColorCacheKey, Rgb24> cache = new();

    public int NumberOfComponents => inner.NumberOfComponents;

    public UnicolourIccTransformCached(Configuration config, int numberOfComponents, bool isInputLab)
    {
        inner = new UnicolourIccTransform(config, numberOfComponents, isInputLab);
    }

    public (double r, double g, double b) ToRgb(ReadOnlySpan<double> values)
    {
        // Normalise to ICC device channels first so the cache keys on the
        // [0,1] device values (raw Lab values would otherwise collapse to
        // the same key once clamped to a byte, colliding distinct colors).
        Span<double> device = values.Length <= 32 ? stackalloc double[values.Length] : new double[values.Length];
        inner.EncodeDevice(values, device);

        var key = ColorCacheKey.FromDoubles(device);
        if (!cache.TryGetValue(key, out var cached))
        {
            var computed = inner.ToRgbFromDevice(device);
            cached = new Rgb24(
                ConvertToByte(computed.r),
                ConvertToByte(computed.g),
                ConvertToByte(computed.b));

            cache[key] = cached;
        }

        // Return the cached 8-bit value on both paths, never the freshly computed full-precision one.
        // The cache stores bytes, so returning `computed` on a miss would make the same input yield
        // slightly different doubles depending on whether it had been seen before. Every consumer
        // quantises to a byte in the end, so nothing is lost by being consistent about it here.
        return ToDoubles(cached);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (double r, double g, double b) ToDoubles(Rgb24 rgb)
    {
        return (rgb.R / 255.0, rgb.G / 255.0, rgb.B / 255.0);
    }

    public void Transform(ReadOnlySpan<byte> src, Span<byte> dstRgb)
    {
        int n = NumberOfComponents;
        int pixels = src.Length / n;

        // Unicolour forces us to allocate (the Channels class ctor takes double[]),
        // hence using Span<double> here is counterproductive (we would need ToArray()
        // for each ToRgbFromDevice() in the loop). We prefer allocating just once, here.
        double[] buffer = new double[n];

        for (int p = 0; p < pixels; p++)
        {
            int s = p * n;
            var key = ColorCacheKey.FromBytes(src.Slice(s, n));

            if (!cache.TryGetValue(key, out var rgb))
            {
                for (int c = 0; c < n; ++c)
                {
                    buffer[c] = src[s + c] / 255.0;
                }

                // buffer holds src/255 which is already the ICC device
                // encoding (for Lab images byte/255 == L*/100 and
                // (a*/b*+128)/255), so bypass EncodeDevice here.
                var computed = inner.ToRgbFromDevice(buffer);
                rgb = new Rgb24(
                    ConvertToByte(computed.r),
                    ConvertToByte(computed.g),
                    ConvertToByte(computed.b));

                cache[key] = rgb;
            }

            int d = p * 3;
            dstRgb[d] = rgb.R;
            dstRgb[d + 1] = rgb.G;
            dstRgb[d + 2] = rgb.B;
        }
    }

    public void ClearCache() => cache.Clear();

    private readonly record struct Rgb24
    {
        public Rgb24(byte r, byte g, byte b)
        {
            R = r;
            G = g;
            B = b;
        }

        public readonly byte R;
        public readonly byte G;
        public readonly byte B;
    }

    private readonly struct ColorCacheKey : IEquatable<ColorCacheKey>
    {
        private readonly ulong a;
        private readonly ulong b;
        private readonly ulong c;
        private readonly ulong d;
        private readonly int length;

        private ColorCacheKey(ulong a, ulong b, ulong c, ulong d, int length)
        {
            this.a = a;
            this.b = b;
            this.c = c;
            this.d = d;
            this.length = length;
        }

        public static ColorCacheKey FromBytes(ReadOnlySpan<byte> values)
        {
            if ((uint)values.Length > 32)
            {
                throw new NotSupportedException("CacheKey supports up to 32 components.");
            }

            ulong a = 0, b = 0, c = 0, d = 0;

            for (int i = 0; i < values.Length; ++i)
            {
                ulong v = values[i];
                if (i < 8) a |= v << (i * 8);
                else if (i < 16) b |= v << ((i - 8) * 8);
                else if (i < 24) c |= v << ((i - 16) * 8);
                else d |= v << ((i - 24) * 8);
            }

            return new ColorCacheKey(a, b, c, d, values.Length);
        }

        public static ColorCacheKey FromDoubles(ReadOnlySpan<double> values)
        {
            if ((uint)values.Length > 32)
            {
                throw new NotSupportedException("CacheKey supports up to 32 components.");
            }

            Span<byte> tmp = stackalloc byte[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                tmp[i] = ConvertToByte(values[i]);
            }

            return FromBytes(tmp);
        }

        public bool Equals(ColorCacheKey other) =>
            a == other.a &&
            b == other.b &&
            c == other.c &&
            d == other.d &&
            length == other.length;

        public override bool Equals(object? obj) => obj is ColorCacheKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(a, b, c, d, length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ConvertToByte(double componentValue)
    {
        return (byte)Math.Round(componentValue * 255, MidpointRounding.AwayFromZero);
    }
}