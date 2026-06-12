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
using System.Linq;
using System.Text;
using System.Threading;
using SkiaSharp;
using UglyToad.PdfPig.Fonts.SystemFonts;
using UglyToad.PdfPig.Fonts.TrueType;
using UglyToad.PdfPig.PdfFonts;

namespace UglyToad.PdfPig.Rendering.Skia.Helpers
{
    internal sealed partial class SkiaFontCache : IDisposable
    {
        /// <summary>
        /// The "Noto" families are tried first. They're purpose-built as the universal "no tofu" fallback
        /// and are present across the Linux/macOS runners, so the choice is both good quality and consistent.
        /// </summary>
        private const string NotoFont = "Noto";

        private readonly Lazy<SkiaFontCacheItem> DefaultSkiaFontCacheItem = new(() => new SkiaFontCacheItem(SKTypeface.Default)); // Do not make static

        private readonly ConcurrentDictionary<IFont, ConcurrentDictionary<int, Lazy<SKPath?>>> _cache = new();

        private readonly ConcurrentDictionary<string, List<SkiaFontCacheItem>> _typefaces = new();

        private readonly ReaderWriterLockSlim _lock = new();

        private readonly SKFontManager _skFontManager = SKFontManager.CreateDefault();
        
        public SkiaFontCacheItem GetTypefaceOrFallback(IFont font, string unicode)
        {
            if (IsDisposed())
            {
                throw new ObjectDisposedException(nameof(SkiaFontCache));
            }

            _lock.EnterReadLock();
            try
            {
                if (IsDisposed())
                {
                    throw new ObjectDisposedException(nameof(SkiaFontCache));
                }

                string fontKey = GetFontKey(font);
                int codepoint = GetCodepoint(unicode);

                if (TryGetFontCacheItem(fontKey, unicode, codepoint, out var item))
                {
                    return item!;
                }

                // Cannot find font ZapfDingbats MOZILLA-LINK-5251-1

                SKTypeface? currentTypeface;
                using (var style = font.Details.GetFontStyle())
                {
                    // Try get font by name
                    string? cleanFontName = font.GetCleanFontName();
                    currentTypeface = SKTypeface.FromFamilyName(cleanFontName, style);

                    if (currentTypeface is null || currentTypeface.IsDefault())
                    {
                        // We found the default font
                        // Try get font by substitute name
                        string? fontFamilyName = GetTrueTypeFontFontName(cleanFontName);
                        if (!string.IsNullOrEmpty(fontFamilyName))
                        {
                            currentTypeface?.Dispose();
                            currentTypeface = SKTypeface.FromFamilyName(fontFamilyName, style);
                        }
                    }

                    // Fallback font
                    // https://github.com/mono/SkiaSharp/issues/232
                    if (currentTypeface is null || (!string.IsNullOrWhiteSpace(unicode) && !currentTypeface.ContainsGlyph(codepoint)))
                    {
                        // If font cannot render the char
                        var fallback = _skFontManager.MatchCharacter(codepoint); // Access violation here
                        if (fallback is not null)
                        {
                            currentTypeface?.Dispose();
                            currentTypeface = _skFontManager.MatchFamily(fallback.FamilyName, style);
                            fallback.Dispose();
                        }
                    }

                    // Some native SkiaSharp builds — notably SkiaSharp.NativeAssets.Linux.NoDependencies,
                    // which the test/CI runners use to avoid the libfontconfig/libfreetype/libuuid native
                    // dependencies of the full build — don't implement codepoint-based MatchCharacter and
                    // return null. Without this, glyphs from non-embedded fonts (e.g. CJK text referencing
                    // "SimSun" by name) silently vanish on Linux while rendering fine on Windows/macOS.
                    // The font families ARE enumerable, so scan them for one that can render the character.
                    // Ordinal-first selection keeps the choice deterministic across runs and architectures.
                    if (!string.IsNullOrWhiteSpace(unicode) &&
                        (currentTypeface is null || !currentTypeface.ContainsGlyph(codepoint)))
                    {
                        SKTypeface? enumerated = MatchCharacterByEnumeration(codepoint, style);
                        if (enumerated is not null)
                        {
                            currentTypeface?.Dispose();
                            currentTypeface = enumerated;
                        }
                    }
                }
                
                // MOZILLA-LINK-625-0 ("BVNSKD+wasy10|0|0") ;
                // test-2_so_74165171.pdf ("NHVBQA+NotoSansHK-Thin|0|0");
                // cmap-parsing-exception;
                // ssm2163
                // GHOSTSCRIPT-698363-0
                // Type0_CJK_Font.pdf

                return SetFontCacheItem(fontKey, currentTypeface);
            }
            finally
            {
                if (!IsDisposed())
                {
                    _lock.ExitReadLock();
                }
            }
        }

        // Font family names known to the font manager, sorted ordinally so character-coverage
        // scanning is deterministic. Built lazily because the installed font set is stable for
        // the lifetime of the (document-scoped) cache.
        private string[]? _sortedFontFamilies;

        /// <summary>
        /// Last-resort fallback used when <see cref="SKFontManager.MatchCharacter(int)"/> cannot supply a
        /// typeface for <paramref name="codepoint"/> (e.g. the NoDependencies native build). Scans the
        /// installed font families and returns the first whose typeface contains the glyph, or <c>null</c>
        /// if none can render it. The Noto fonts are prioritised. Within each pass families are visited in
        /// stable ordinal order so the result is deterministic across runs and architectures.
        /// </summary>
        private SKTypeface? MatchCharacterByEnumeration(int codepoint, SKFontStyle style)
        {
            string[] families = _sortedFontFamilies ??= BuildSortedFontFamilies();

            return FindCoveringTypeface(families, codepoint, style, notoOnly: true)
                   ?? FindCoveringTypeface(families, codepoint, style, notoOnly: false);
        }

        private SKTypeface? FindCoveringTypeface(string[] families, int codepoint, SKFontStyle style, bool notoOnly)
        {
            foreach (string family in families)
            {
                if (notoOnly && !family.StartsWith(NotoFont, StringComparison.Ordinal))
                {
                    continue;
                }

                SKTypeface? candidate = _skFontManager.MatchFamily(family, style);
                if (candidate is null)
                {
                    continue;
                }

                if (candidate.ContainsGlyph(codepoint))
                {
                    return candidate;
                }

                candidate.Dispose();
            }

            return null;
        }

        private string[] BuildSortedFontFamilies()
        {
            string[] families = _skFontManager.GetFontFamilies();
            Array.Sort(families, StringComparer.Ordinal);
            return families;
        }

        private bool TryGetFontCacheItem(string fontKey,string unicode, int codepoint, out SkiaFontCacheItem? item)
        {
            item = null;

            if (_typefaces.TryGetValue(fontKey, out List<SkiaFontCacheItem>? skiaFontCacheItems))
            {
                if (string.IsNullOrWhiteSpace(unicode))
                {
                    item = skiaFontCacheItems[0];
                    return true;
                }

                foreach (var cacheItem in skiaFontCacheItems)
                {
                    // Find first font that can render char
                    if (cacheItem.Typeface.ContainsGlyph(codepoint))
                    {
                         item = cacheItem;
                         return true;
                    }
                }
            }

            return false;
        }

        private SkiaFontCacheItem SetFontCacheItem(string fontKey, SKTypeface? typeface)
        {
            if (_typefaces.TryGetValue(fontKey, out List<SkiaFontCacheItem>? skiaFontCacheItems))
            {
                // Make sure the font is not already cached
                SkiaFontCacheItem? skiaFontCacheItem = skiaFontCacheItems.FirstOrDefault(x => x.Typeface.Equals(typeface));
                if (skiaFontCacheItem is not null)
                {
                    // TODO - We might want to improve the equality check here
                    // This is the best we could find, might not render
                    // properly though (see MOZILLA-3136-0.pdf)
                    return skiaFontCacheItem;
                }
            }

            // Check if we need to create cache item
            var item = typeface is null || typeface.IsDefault()
                ? DefaultSkiaFontCacheItem.Value
                : new SkiaFontCacheItem(typeface);

            if (skiaFontCacheItems is null)
            {
                skiaFontCacheItems = new List<SkiaFontCacheItem>();
                _typefaces[fontKey] = skiaFontCacheItems;
            }

            skiaFontCacheItems.Add(item);
            return item;
        }

        private static string? GetTrueTypeFontFontName(string? fontName)
        {
            TrueTypeFont trueTypeFont = SystemFontFinder.Instance.GetTrueTypeFont(fontName);
            return trueTypeFont?.TableRegister?.NameTable?.FontFamilyName;
        }

        private static string GetFontKey(IFont font)
        {
            if (string.IsNullOrEmpty(font.Name?.Data))
            {
                throw new NullReferenceException("The font's name is null.");
            }

            return $"{font.Name!.Data}|{(font.Details.IsBold ? (byte)1 : (byte)0)}|{(font.Details.IsItalic ? (byte)1 : (byte)0)}";
        }

        private static int GetCodepoint(string unicode)
        {
            return BitConverter.ToInt32(Encoding.UTF32.GetBytes(unicode), 0);
        }

        private bool IsDisposed()
        {
            return Interlocked.Read(ref _isDisposed) != 0;
        }

        private long _isDisposed;

        public void Dispose()
        {
            _lock.EnterWriteLock();

            try
            {
                if (IsDisposed())
                {
                    return;
                }

                Interlocked.Increment(ref _isDisposed);

                _skFontManager.Dispose();

                foreach (IFont? key in _cache.Keys)
                {
                    if (!_cache.TryRemove(key, out var fontCache))
                    {
                        continue;
                    }

                    foreach (var value in fontCache.Values)
                    {
                        value?.Value?.Dispose();
                    }

                    fontCache.Clear();
                }

                _cache.Clear();

                foreach (IType3Font key in _type3Cache.Keys)
                {
                    if (!_type3Cache.TryRemove(key, out var perFont))
                    {
                        continue;
                    }

                    foreach (var value in perFont.Values)
                    {
                        value?.Value?.Dispose();
                    }

                    perFont.Clear();
                }

                _type3Cache.Clear();

                foreach (string key in _typefaces.Keys)
                {
                    if (!_typefaces.TryRemove(key, out var typeface))
                    {
                        continue;
                    }

                    foreach (SkiaFontCacheItem item in typeface)
                    {
                        item.Dispose();
                    }
                }

                _typefaces.Clear();
            }
            finally
            {
                _lock.ExitWriteLock();
                _lock.Dispose();
            }
        }
    }
}
