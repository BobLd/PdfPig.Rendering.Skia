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

namespace UglyToad.PdfPig.Icc.Unicolour;

/// <summary>
/// <see cref="IIccTransform"/> backed by a configured
/// <see cref="Configuration"/>.
/// </summary>
internal sealed class UnicolourIccTransform : IIccTransform
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (double r, double g, double b) ToRgb(ReadOnlySpan<double> values)
    {
        Span<double> device = values.Length <= 16 ? stackalloc double[values.Length] : new double[values.Length];
        EncodeDevice(values, device);
        return ToRgbFromDevice(device);
    }

    /// <summary>
    /// Convert ICC device channels already normalised to [0,1] to sRGB.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal (double r, double g, double b) ToRgbFromDevice(ReadOnlySpan<double> device)
    {
        var channels = new Channels(device.ToArray());
        var uc = new Wacton.Unicolour.Unicolour(_config, channels);
        var rgb = uc.Rgb;

        // ICC conversion from CMYK/wide-gamut sources can produce sRGB
        // components outside [0,1] for out-of-gamut colors. Clip to [0,1]
        // so downstream consumers (SKColor conversion, image bytes) see
        // a valid sRGB triple. The IMAGE path's caller already clamps via
        // ClampToByte, but the paint path's caller (ICCBasedColorSpaceDetails
        // → RGBColor → ToSKColor) treats doubles outside [0,1] as raw bytes,
        // which silently destroys the color (e.g. 0.62 -> byte 1).
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

    public void Transform(ReadOnlySpan<byte> src, Span<byte> dstRgb)
    {
        int n = NumberOfComponents;
        int pixels = src.Length / n;
        double[] buffer = new double[n];

        for (int p = 0; p < pixels; p++)
        {
            int s = p * n;
            for (int c = 0; c < n; c++)
            {
                buffer[c] = src[s + c] / 255.0;
            }

            var channels = new Channels(buffer);
            var uc = new Wacton.Unicolour.Unicolour(_config, channels);
            var rgb = uc.Rgb;

            int d = p * 3;
            dstRgb[d] = ClampToByte(rgb.R);
            dstRgb[d + 1] = ClampToByte(rgb.G);
            dstRgb[d + 2] = ClampToByte(rgb.B);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ClampToByte(double v)
    {
        double scaled = v * 255.0;
        if (scaled <= 0) return 0;
        if (scaled >= 255) return 255;
        //return (byte)Math.Round(scaled, MidpointRounding.AwayFromZero);
        return Convert.ToByte(scaled);
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
        Span<double> device = values.Length <= 16 ? stackalloc double[values.Length] : new double[values.Length];
        inner.EncodeDevice(values, device);

        var key = ColorCacheKey.FromDoubles(device);
        if (cache.TryGetValue(key, out var cached))
        {
            return (
                cached.R / 255.0,
                cached.G / 255.0,
                cached.B / 255.0
            );
        }

        var rgb = inner.ToRgbFromDevice(device);
        cache[key] = new Rgb24(
            ClampToByte(rgb.r),
            ClampToByte(rgb.g),
            ClampToByte(rgb.b));

        return rgb;
    }

    public void Transform(ReadOnlySpan<byte> src, Span<byte> dstRgb)
    {
        int n = NumberOfComponents;
        int pixels = src.Length / n;
        Span<double> buffer = NumberOfComponents <= 128 ? stackalloc double[n] : new double[n];

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
                    ClampToByte(computed.r),
                    ClampToByte(computed.g),
                    ClampToByte(computed.b));

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

            for (int i = 0; i < values.Length; i++)
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
                tmp[i] = ClampToByte(values[i]);
            }

            return FromBytes(tmp);
        }

        public bool Equals(ColorCacheKey other) =>
            a == other.a &&
            b == other.b &&
            c == other.c &&
            d == other.d &&
            length == other.length;

        public override bool Equals(object? obj) =>
            obj is ColorCacheKey other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(a, b, c, d, length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ClampToByte(double v)
    {
        double scaled = v * 255.0;
        if (scaled <= 0) return 0;
        if (scaled >= 255) return 255;
        return (byte)Math.Round(scaled, MidpointRounding.AwayFromZero);
    }
}
