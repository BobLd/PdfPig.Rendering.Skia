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
using Wacton.Unicolour;
using Wacton.Unicolour.Icc;
using System.Security.Cryptography;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Graphics.Core;

namespace UglyToad.PdfPig.Colors.Icc.Unicolour
{
    /// <summary>
    /// <see cref="IIccProfileService"/> backed by Wacton.Unicolour.
    /// Parsed profiles are cached process-wide, keyed by profile content
    /// hash (<see cref="Profile.CalculateId"/>), intent, and component count.
    /// </summary>
    public sealed class UnicolourIccProfileService : IIccProfileService
    {
        /// <summary>Shared default instance.</summary>
        public static readonly UnicolourIccProfileService Default = new();

        private readonly ConcurrentDictionary<CacheKey, IIccTransform?> cache = new();

        /// <inheritdoc/>
        public bool TryGetTransform(
            Memory<byte> profileBytes,
            int numberOfColorComponents,
            RenderingIntent intent,
            out IIccTransform? transform)
        {
            if (profileBytes.IsEmpty)
            {
                transform = null;
                return false;
            }
            
            byte[] id = CalculateId(profileBytes);

            var key = new CacheKey(id, intent, numberOfColorComponents);

            transform = cache.GetOrAdd(key, _ => TryCreate(profileBytes, numberOfColorComponents, intent));
            return transform is not null;
        }

        private static readonly int[] IndexesToZeroForHash = [44, 45, 46, 47, 64, 65, 66, 67, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 97, 98, 99];

        private static byte[] CalculateId(Memory<byte> bytes)
        {
            foreach (var index in IndexesToZeroForHash)
            {
                bytes.Span[index] = 0;
            }

            using var ms = ((ReadOnlyMemory<byte>)bytes).AsReadOnlyMemoryStream();
            using var md5 = MD5.Create();
            return md5.ComputeHash(ms);
        }

        private static IIccTransform? TryCreate(ReadOnlyMemory<byte> bytes, int components, RenderingIntent intent)
        {
            try
            {
                using var ms = bytes.AsReadOnlyMemoryStream();
                var profile = new Profile(ms);
                var iccConfig = new IccConfiguration(profile, MapIntent(intent));
                var config = new Configuration(iccConfig: iccConfig);
                //return new UnicolourIccTransform(config, components);
                return new UnicolourIccTransformCached(config, components);
            }
            catch
            {
                return null;
            }
        }

        private static Intent MapIntent(RenderingIntent intent) => intent switch
        {
            RenderingIntent.Perceptual            => Intent.Perceptual,
            RenderingIntent.RelativeColorimetric  => Intent.RelativeColorimetric,
            RenderingIntent.Saturation            => Intent.Saturation,
            RenderingIntent.AbsoluteColorimetric  => Intent.AbsoluteColorimetric,
            _ => Intent.RelativeColorimetric
        };

        private readonly struct CacheKey : IEquatable<CacheKey>
        {
            private readonly byte[] _id;
            private readonly RenderingIntent _intent;
            private readonly int _components;

            public CacheKey(byte[] id, RenderingIntent intent, int components)
            {
                _id = id;
                _intent = intent;
                _components = components;
            }

            public bool Equals(CacheKey other)
            {
                if (_intent != other._intent ||
                    _components != other._components ||
                    _id.Length != other._id.Length)
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
                    int h = (int)_intent * 397 ^ _components;
                    for (int i = 0; i < _id.Length; i++)
                    {
                        h = h * 31 + _id[i];
                    }
                    return h;
                }
            }
        }
    }
}
