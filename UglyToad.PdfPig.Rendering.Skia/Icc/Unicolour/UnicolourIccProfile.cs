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
using UglyToad.PdfPig.Graphics.Colors.Icc;
using UglyToad.PdfPig.Graphics.Core;
using Wacton.Unicolour;
using Wacton.Unicolour.Icc;

namespace UglyToad.PdfPig.Icc.Unicolour;

/// <summary>
/// A parsed Unicolour ICC profile. Owns a per-intent <see cref="IIccTransform"/> cache
/// so each intent is configured at most once. Safe for concurrent reads.
/// </summary>
internal sealed class UnicolourIccProfile : IIccProfile
{
    private readonly Profile _profile;
    private readonly ConcurrentDictionary<RenderingIntent, IIccTransform> _transforms = new();
    private readonly bool _isInputLab;

    public int NumberOfComponents { get; }

    public UnicolourIccProfile(Profile profile, int numberOfComponents)
    {
        NumberOfComponents = numberOfComponents;
        _profile = profile;
        _isInputLab = string.Equals(profile.Header.DataColourSpace, "Lab ", StringComparison.Ordinal);
    }

    public bool TryGetTransform(RenderingIntent intent, out IIccTransform transform)
    {
        transform = _transforms.GetOrAdd(intent, BuildFor);
        return transform is not null;
    }

    private IIccTransform BuildFor(RenderingIntent intent)
    {
        var iccConfig = new IccConfiguration(_profile, MapIntent(intent));
        var config = new Configuration(iccConfig: iccConfig);
        return new UnicolourIccTransformCached(config, NumberOfComponents, _isInputLab);
    }

    private static Intent MapIntent(RenderingIntent intent) => intent switch
    {
        RenderingIntent.Perceptual => Intent.Perceptual,
        RenderingIntent.RelativeColorimetric => Intent.RelativeColorimetric,
        RenderingIntent.Saturation => Intent.Saturation,
        RenderingIntent.AbsoluteColorimetric => Intent.AbsoluteColorimetric,
        _ => Intent.RelativeColorimetric
    };
}
