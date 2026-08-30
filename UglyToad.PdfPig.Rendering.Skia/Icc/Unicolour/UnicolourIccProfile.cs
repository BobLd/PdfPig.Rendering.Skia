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
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using UglyToad.PdfPig.Graphics.Colors.Icc;
using UglyToad.PdfPig.Graphics.Core;
using Wacton.Unicolour;
using Wacton.Unicolour.Icc;

namespace UglyToad.PdfPig.Rendering.Skia.Icc.Unicolour;

/// <summary>
/// A parsed Unicolour ICC profile. Owns a per-intent <see cref="IIccTransform"/> cache
/// so each intent is configured at most once. Safe for concurrent reads.
/// </summary>
internal sealed class UnicolourIccProfile : IIccProfile
{
    private readonly Profile _profile;

    /// <summary>
    /// One entry per intent, holding <see langword="null"/> for an intent this profile cannot be driven
    /// with so a declined intent is not re-attempted on every colour.
    /// </summary>
    private readonly ConcurrentDictionary<RenderingIntent, IIccTransform?> _transforms = new();

    public int NumberOfComponents { get; }

    /// <inheritdoc/>
    public IReadOnlyList<double> ComponentRanges { get; }

    /// <summary>
    /// Whether the profile's data colour space is L*a*b*, in which case the components it consumes are real
    /// L*a*b* values rather than the usual <c>[0, 1]</c> encoding. Both <see cref="ComponentRanges"/> - which
    /// tells the core how to clip - and <see cref="UnicolourIccTransformCached"/> - which applies the ICC.1
    /// encoding itself - are driven from this one reading of the header, so they cannot disagree.
    /// </summary>
    public bool IsLabInput { get; }

    public UnicolourIccProfile(Profile profile, int numberOfComponents)
    {
        _profile = profile;
        NumberOfComponents = numberOfComponents;
        IsLabInput = string.Equals(profile.Header.DataColourSpace, LabSignature, StringComparison.Ordinal);
        ComponentRanges = GetComponentRanges(IsLabInput, numberOfComponents);
    }

    private const string LabSignature = "Lab ";

    /// <summary>
    /// The ICC.1 L*a*b* encoding range (7.2.6): L* in [0, 100], a* and b* in [-128, 127].
    /// </summary>
    private static readonly double[] LabComponentRanges = [0.0, 100.0, -128.0, 127.0, -128.0, 127.0];

    private static readonly double[] UnitRanges1 = [0.0, 1.0];
    private static readonly double[] UnitRanges3 = [0.0, 1.0, 0.0, 1.0, 0.0, 1.0];
    private static readonly double[] UnitRanges4 = [0.0, 1.0, 0.0, 1.0, 0.0, 1.0, 0.0, 1.0];

    /// <summary>
    /// The valid range of each input component, as 2 x <paramref name="numberOfComponents"/> values. Every
    /// data colour space this class accepts encodes its components in <c>[0, 1]</c> except L*a*b*, and
    /// reporting <c>[0, 1]</c> for the latter would have the core clip every colour to near-black.
    /// </summary>
    private static IReadOnlyList<double> GetComponentRanges(bool isLabInput, int numberOfComponents)
    {
        if (isLabInput && numberOfComponents == 3)
        {
            return LabComponentRanges;
        }

        switch (numberOfComponents)
        {
            case 1:
                return UnitRanges1;
            case 3:
                return UnitRanges3;
            case 4:
                return UnitRanges4;
            default:
                double[] ranges = new double[2 * numberOfComponents];
                for (int i = 1; i < ranges.Length; i += 2)
                {
                    ranges[i] = 1.0;
                }

                return ranges;
        }
    }

    /// <summary>
    /// The number of device channels implied by an ICC data colour space signature (ICC.1 Table 19, header
    /// bytes 16-19). This is what makes the profile — rather than the PDF's <c>/N</c> — the authority on how
    /// many components it consumes, so that a disagreement between the two can actually be detected.
    /// <para>
    /// Deliberately limited to the 1-, 3- and 4-channel spaces the rest of the pipeline can actually
    /// consume: <c>/N</c> on an <c>/ICCBased</c> space must be 1, 3 or 4, and
    /// <c>OutputIntentColorManagement.TryMapDeviceToProfileComponents</c> can only map a device colour onto
    /// a profile of those sizes. Counting the channels of an <c>nCLR</c> profile would make it look usable
    /// and let output-intent selection settle on an intent nothing can convert through, so those are
    /// rejected here and the caller keeps looking or falls back.
    /// </para>
    /// </summary>
    /// <returns><c>false</c> for a signature naming no device space we can drive a conversion with.</returns>
    public static bool TryGetComponentCount(string dataColourSpace, out int count)
    {
        switch (dataColourSpace)
        {
            case "GRAY":
                count = 1;
                return true;
            case "RGB ":
            case "CMY ":
            case "XYZ ":
            case LabSignature:
            case "Luv ":
            case "YCbr":
            case "Yxy ":
            case "HSV ":
            case "HLS ":
                count = 3;
                return true;
            case "CMYK":
                count = 4;
                return true;
            default:
                count = 0;
                return false;
        }
    }

    public bool TryGetTransform(RenderingIntent intent, [NotNullWhen(true)] out IIccTransform? transform)
    {
        transform = _transforms.GetOrAdd(intent, BuildFor);
        return transform is not null;
    }

    /// <summary>
    /// The transform for one intent, or <see langword="null"/> when Unicolour cannot drive this profile
    /// with it.
    /// <para>
    /// <see cref="IccConfiguration"/> does not throw when it cannot use a profile: it sets
    /// <see cref="IccConfiguration.Error"/> and the <see cref="Configuration"/> quietly converts through
    /// Unicolour's ordinary RGB pipeline instead, which is a different answer from the profile's. That is
    /// exactly the outcome <see cref="IIccProfileService"/> forbids — reporting success and then producing
    /// wrong colours — and neither the caller nor
    /// <c>ICCBasedColorSpaceDetails.IsUsable</c> could detect it, since nothing throws and a plausible
    /// colour comes back. So the error is read here and the intent is declined instead, leaving the caller
    /// to retry with another intent or fall back to the alternate colour space.
    /// </para>
    /// </summary>
    private IIccTransform? BuildFor(RenderingIntent intent)
    {
        try
        {
            var iccConfig = new IccConfiguration(_profile, MapIntent(intent));

            if (!string.IsNullOrEmpty(iccConfig.Error))
            {
                return null;
            }

            var config = new Configuration(iccConfig: iccConfig);
            return new UnicolourIccTransformCached(config, NumberOfComponents, IsLabInput);
        }
        catch
        {
            return null;
        }
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
