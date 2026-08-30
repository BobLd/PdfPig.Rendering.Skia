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
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics.Colors.Icc;
using Wacton.Unicolour;
using Wacton.Unicolour.Icc;

namespace UglyToad.PdfPig.Rendering.Skia.Icc.Unicolour;

/// <summary>
/// <see cref="IIccProfileService"/> backed by Wacton.Unicolour.
/// </summary>
public sealed class UnicolourIccProfileService : IIccProfileService
{
    /// <summary>
    /// Shared default instance. Converts <c>/ICCBased</c> colour spaces through their profile, and leaves
    /// device colours to their built-in conversion.
    /// </summary>
    public static readonly UnicolourIccProfileService Instance = new();

    /// <summary>
    /// Shared instance that additionally colour-manages device colours through the document's output
    /// intent, which is what a previewing or proofing consumer wants. See
    /// <see cref="IIccProfileService.UseOutputIntent"/> for why that is opt-in.
    /// </summary>
    public static readonly UnicolourIccProfileService InstanceWithIntent =
        new(useOutputIntent: true);

    /// <summary>
    /// Create a service. The parsing behaviour is the same either way - only what is done with a
    /// document's output intent differs - and parsed profiles are cached process-wide, so holding several
    /// instances costs nothing.
    /// </summary>
    /// <param name="useOutputIntent"><inheritdoc cref="IIccProfileService.UseOutputIntent" path="/summary/node()[1]"/></param>
    /// <param name="preferredOutputIntentSubtype"><inheritdoc cref="IIccProfileService.PreferredOutputIntentSubtype" path="/summary/node()[1]"/></param>
    public UnicolourIccProfileService(bool useOutputIntent = false,
        string? preferredOutputIntentSubtype = OutputIntent.PdfXSubtype)
    {
        UseOutputIntent = useOutputIntent;
        PreferredOutputIntentSubtype = preferredOutputIntentSubtype;
    }

    /// <inheritdoc/>
    public bool UseOutputIntent { get; }

    /// <inheritdoc/>
    public string? PreferredOutputIntentSubtype { get; }

    /// <summary>
    /// Parsed profiles, keyed on the byte array backing the profile bytes.
    /// <para>
    /// Reuse matters for much more than the parse itself, which is only a fraction of a millisecond: a new
    /// <see cref="UnicolourIccProfile"/> also means a new per-intent transform dictionary, hence a new
    /// <see cref="IccConfiguration"/> and — the expensive part — a brand new <b>empty</b> colour conversion
    /// cache, so every colour on the page has to be converted through the profile again. An
    /// <c>/ICCBased</c> colour space is re-created on every resource dictionary load, so without this the
    /// conversion cache never survives a single page render.
    /// </para>
    /// <para>
    /// A <see cref="ConditionalWeakTable{TKey,TValue}"/> gives exactly the right lifetime: the entry is
    /// reachable for as long as the profile bytes are (they are held by PdfPig's document-scoped ICC byte
    /// cache) and is collected with them, so nothing is retained after the document is closed. This is also
    /// why the key is the array identity rather than a content hash — hashing megabytes on every colour
    /// space parse would cost more than it saves, and the byte cache upstream already guarantees the same
    /// array instance comes back for the same profile.
    /// </para>
    /// </summary>
    private static readonly ConditionalWeakTable<byte[], ParsedProfiles> Cache = new();

    /// <inheritdoc/>
    public bool TryGetProfile(ReadOnlyMemory<byte> profileBytes, [NotNullWhen(true)] out IIccProfile? profile)
    {
        profile = null;

        if (profileBytes.IsEmpty)
        {
            return false;
        }

        // Without an array to key on there is no stable identity, so parse without caching.
        if (!MemoryMarshal.TryGetArray(profileBytes, out ArraySegment<byte> segment) || segment.Array is null)
        {
            profile = Parse(profileBytes);
            return profile is not null;
        }

        var parsed = Cache.GetValue(segment.Array, static _ => new ParsedProfiles());

        // Offset and count are part of the key so two profiles sharing one backing array cannot collide.
        var key = (segment.Offset, segment.Count);

        if (parsed.TryGet(key, out profile))
        {
            return profile is not null;
        }

        profile = Parse(profileBytes);

        // A failed parse is cached as null on purpose, so a malformed profile is not retried per page.
        parsed.Set(key, profile);

        return profile is not null;
    }

    private static IIccProfile? Parse(ReadOnlyMemory<byte> profileBytes)
    {
        try
        {
            using var ms = profileBytes.AsReadOnlyMemoryStream();
            var parsed = new Profile(ms);

            // The channel count comes from the profile's own data colour space, never from the PDF. A
            // signature we cannot count channels for is one we cannot drive a conversion with either, so
            // the caller falls back to the alternate colour space rather than being handed a profile whose
            // component count is a guess.
            if (!UnicolourIccProfile.TryGetComponentCount(parsed.Header.DataColourSpace, out int components))
            {
                return null;
            }

            return new UnicolourIccProfile(parsed, components);
        }
        catch
        {
            return null;
        }
    }

    private sealed class ParsedProfiles
    {
        private readonly Dictionary<(int Offset, int Count), IIccProfile?> profiles = new();

        public bool TryGet((int Offset, int Count) key, out IIccProfile? profile)
        {
            lock (profiles)
            {
                return profiles.TryGetValue(key, out profile);
            }
        }

        public void Set((int Offset, int Count) key, IIccProfile? profile)
        {
            lock (profiles)
            {
                profiles[key] = profile;
            }
        }
    }
}
