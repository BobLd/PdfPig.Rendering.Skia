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
using System.Security.Cryptography;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics.Colors.Icc;
using Wacton.Unicolour.Icc;

namespace UglyToad.PdfPig.Icc.Unicolour;

/// <summary>
/// <see cref="IIccProfileService"/> backed by Wacton.Unicolour.
/// Parsed profiles are cached process-wide, keyed by profile content
/// hash (ICC Profile ID per ICC.1:2010 §7.2.18) and component count.
/// Intent is NOT part of the key — per-intent transforms live on the
/// returned <see cref="UnicolourIccProfile"/>.
/// </summary>
public sealed class UnicolourIccProfileService : IIccProfileService
{
    /// <summary>Shared default instance.</summary>
    public static readonly UnicolourIccProfileService Default = new();

    private readonly ConcurrentDictionary<CacheKey, IIccProfile?> _cache = new();

    /// <inheritdoc/>
    public bool TryGetProfile(
        Memory<byte> profileBytes,
        int numberOfColorComponents,
        out IIccProfile? profile)
    {
        if (profileBytes.IsEmpty)
        {
            profile = null;
            return false;
        }

        byte[] id = CalculateId(profileBytes.Span);
        var key = new CacheKey(id, numberOfColorComponents);

        profile = _cache.GetOrAdd(key, _ => TryCreate(profileBytes, numberOfColorComponents));
        return profile is not null;
    }

    // Per ICC.1:2010 §7.2.18, the Profile ID is the MD5 of the profile
    // bytes with the rendering-intent (44–47), PCS illuminant (64–67),
    // creator (84–95) and Profile ID (96–99) fields set to zero. We
    // must NOT mutate the caller's buffer — those header fields are
    // critical for actual profile parsing (especially the rendering
    // intent at offset 44–47, which controls colour-managed output).
    private static readonly int[] IndexesToZeroForHash =
    [
        44, 45, 46, 47, 64, 65, 66, 67, 84, 85, 86, 87, 88, 89, 90, 91,
            92, 93, 94, 95, 96, 97, 98, 99
    ];

    private static byte[] CalculateId(ReadOnlySpan<byte> bytes)
    {
        // Hash over a temporary copy so the original profile bytes
        // stay intact for the subsequent Profile(ms) parse.
        byte[] forHash = bytes.ToArray();
        foreach (var index in IndexesToZeroForHash)
        {
            if (index < forHash.Length)
            {
                forHash[index] = 0;
            }
        }

        using var md5 = MD5.Create();
        return md5.ComputeHash(forHash);
    }

    private static IIccProfile? TryCreate(ReadOnlyMemory<byte> bytes, int components)
    {
        try
        {
            using var ms = bytes.AsReadOnlyMemoryStream();
            var profile = new Profile(ms);
            return new UnicolourIccProfile(profile, components);
        }
        catch
        {
            return null;
        }
    }

    private readonly struct CacheKey : IEquatable<CacheKey>
    {
        private readonly byte[] _id;
        private readonly int _components;

        public CacheKey(byte[] id, int components)
        {
            _id = id;
            _components = components;
        }

        public bool Equals(CacheKey other)
        {
            if (_components != other._components || _id.Length != other._id.Length)
            {
                return false;
            }

            return _id.AsSpan().SequenceEqual(other._id.AsSpan());
        }

        public override bool Equals(object? obj) => obj is CacheKey k && Equals(k);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = _components;
                for (int i = 0; i < _id.Length; i++)
                {
                    h = h * 31 + _id[i];
                }
                return h;
            }
        }
    }
}
