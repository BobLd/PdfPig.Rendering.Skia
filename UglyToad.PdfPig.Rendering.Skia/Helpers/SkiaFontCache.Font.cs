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
        private readonly Lazy<SkiaFontCacheItem> DefaultSkiaFontCacheItem = new(() => new SkiaFontCacheItem(SKTypeface.Default)); // Do not make static

        private readonly ConcurrentDictionary<IFont, ConcurrentDictionary<int, Lazy<SKPath?>>> _cache = new();

        private readonly ConcurrentDictionary<string, List<SkiaFontCacheItem>> _typefaces = new();

        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();

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

                SKTypeface currentTypeface;
                using (var style = font.Details.GetFontStyle())
                {
                    // Try get font by name
                    string cleanFontName = font.GetCleanFontName();
                    currentTypeface = SKTypeface.FromFamilyName(cleanFontName, style);

                    if (currentTypeface.IsDefault())
                    {
                        // We found the default font
                        // Try get font by substitute name
                        string? fontFamilyName = GetTrueTypeFontFontName(cleanFontName);
                        if (!string.IsNullOrEmpty(fontFamilyName))
                        {
                            currentTypeface.Dispose();
                            currentTypeface = SKTypeface.FromFamilyName(fontFamilyName, style);
                        }
                    }

                    // Fallback font
                    // https://github.com/mono/SkiaSharp/issues/232
                    if (!string.IsNullOrWhiteSpace(unicode) && !currentTypeface.ContainsGlyph(codepoint))
                    {
                        // If font cannot render the char
                        var fallback = _skFontManager.MatchCharacter(codepoint); // Access violation here
                        if (fallback is not null)
                        {
                            currentTypeface.Dispose();
                            currentTypeface = _skFontManager.MatchFamily(fallback.FamilyName, style);
                            fallback.Dispose();
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

        private bool TryGetFontCacheItem(string fontKey,string unicode, int codepoint, out SkiaFontCacheItem? item)
        {
            item = null;

            if (!_typefaces.TryGetValue(fontKey, out List<SkiaFontCacheItem>? skiaFontCacheItems))
            {
                return false;
            }

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

            return false;
        }

        private SkiaFontCacheItem SetFontCacheItem(string fontKey, SKTypeface typeface)
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
            var item = typeface.IsDefault()
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

        private static string? GetTrueTypeFontFontName(string fontName)
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

            return $"{font.Name.Data}|{(font.Details.IsBold ? (byte)1 : (byte)0)}|{(font.Details.IsItalic ? (byte)1 : (byte)0)}";
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

                foreach (var key in _cache.Keys)
                {
                    if (_cache.TryRemove(key, out var fontCache))
                    {
                        foreach (var value in fontCache.Values)
                        {
                            value?.Value?.Dispose();
                        }

                        fontCache.Clear();
                    }
                }

                _cache.Clear();

                foreach (string key in _typefaces.Keys)
                {
                    if (_typefaces.TryRemove(key, out var typeface))
                    {
                        foreach (SkiaFontCacheItem item in typeface)
                        {
                            item.Dispose();
                        }
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
