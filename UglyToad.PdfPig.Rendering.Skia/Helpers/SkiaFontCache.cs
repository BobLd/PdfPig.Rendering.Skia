﻿// Copyright 2024 BobLd
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
using SkiaSharp.HarfBuzz;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.SystemFonts;
using UglyToad.PdfPig.PdfFonts;

namespace UglyToad.PdfPig.Rendering.Skia.Helpers
{
    internal sealed class SkiaFontCache : IDisposable
    {
        private static readonly Lazy<SkiaFontCacheItem> DefaultSkiaFontCacheItem = new Lazy<SkiaFontCacheItem>(() => new SkiaFontCacheItem(SKTypeface.Default));

        private readonly ConcurrentDictionary<IFont, ConcurrentDictionary<int, Lazy<SKPath>>> _cache =
            new ConcurrentDictionary<IFont, ConcurrentDictionary<int, Lazy<SKPath>>>();

        private readonly ConcurrentDictionary<string, List<SkiaFontCacheItem>> _typefaces =
            new ConcurrentDictionary<string, List<SkiaFontCacheItem>>();

        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();

        private readonly SKFontManager _skFontManager = SKFontManager.CreateDefault();
        
        internal sealed class SkiaFontCacheItem : IDisposable
        {
            public SkiaFontCacheItem(SKTypeface typeface)
            {
                if (typeface is null)
                {
                    throw new ArgumentNullException(nameof(typeface));
                }

                Typeface = typeface;
                Shaper = new SKShaper(Typeface);
            }

            public SKTypeface Typeface { get; }

            public SKShaper Shaper { get; }

            public void Dispose()
            {
                Typeface.Dispose();
                Shaper.Dispose();
            }
        }

        private static string GetFontKey(IFont font)
        {
            if (string.IsNullOrEmpty(font.Name?.Data))
            {
                throw new NullReferenceException("The font's name is null.");
            }

            return $"{font.Name.Data}|{(font.Details.IsBold ? (byte)1 : (byte)0)}|{(font.Details.IsItalic ? (byte)1 : (byte)0)}";
        }

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

                var codepoint = BitConverter.ToInt32(Encoding.UTF32.GetBytes(unicode), 0);
                
                if (_typefaces.TryGetValue(fontKey, out List<SkiaFontCacheItem> skiaFontCacheItems))
                {
                    // Check if can render
                    foreach (var skiaFontCacheItem in skiaFontCacheItems)
                    {
                        if (string.IsNullOrWhiteSpace(unicode) || skiaFontCacheItem.Typeface.ContainsGlyph(codepoint))
                        {
                            return skiaFontCacheItem;
                        }
                    }
                }
                
                // Cannot find font ZapfDingbats MOZILLA-LINK-5251-1

                using (var style = font.Details.GetFontStyle())
                {
                    string cleanFontName = font.GetCleanFontName();

                    var typeface = SKTypeface.FromFamilyName(cleanFontName, style);
                    if (typeface.FamilyName.Equals(SKTypeface.Default.FamilyName))
                    {
                        var trueTypeFont = SystemFontFinder.Instance.GetTrueTypeFont(cleanFontName);

                        string? fontFamilyName = trueTypeFont?.TableRegister?.NameTable?.FontFamilyName;

                        if (!string.IsNullOrEmpty(fontFamilyName))
                        {
                            typeface.Dispose();
                            typeface = SKTypeface.FromFamilyName(fontFamilyName, style);
                        }
                    }

                    // Fallback font
                    // https://github.com/mono/SkiaSharp/issues/232
                    if (!string.IsNullOrWhiteSpace(unicode) && !typeface.ContainsGlyph(codepoint))
                    {
                        var fallback = _skFontManager.MatchCharacter(codepoint); // Access violation here
                        if (fallback != null)
                        {
                            typeface.Dispose();
                            typeface = _skFontManager.MatchFamily(fallback.FamilyName, style);
                            fallback.Dispose();
                        }
                    }

                    SkiaFontCacheItem? skiaFontCacheItem;
                    if (skiaFontCacheItems is not null)
                    {
                        skiaFontCacheItem = skiaFontCacheItems.FirstOrDefault(x => x.Typeface.Equals(typeface));
                        if (skiaFontCacheItem != null)
                        {
                            // TODO - We might want to improve the equality check here
                            // This is the best we could find, might not render properly though (see MOZILLA-3136-0.pdf)
                            return skiaFontCacheItem;
                        }
                    }

                    if (typeface.FamilyName.Equals(SKTypeface.Default.FamilyName))
                    {
                        skiaFontCacheItem = DefaultSkiaFontCacheItem.Value;
                    }
                    else
                    {
                        skiaFontCacheItem = new SkiaFontCacheItem(typeface);
                    }

                    // MOZILLA-LINK-625-0 ("BVNSKD+wasy10|0|0") ;
                    // test-2_so_74165171.pdf ("NHVBQA+NotoSansHK-Thin|0|0");
                    // cmap-parsing-exception;
                    // ssm2163
                    // GHOSTSCRIPT-698363-0
                    // Type0_CJK_Font.pdf

                    if (skiaFontCacheItems is null)
                    {
                        skiaFontCacheItems = new List<SkiaFontCacheItem>();
                        _typefaces[fontKey] = skiaFontCacheItems;
                    }

                    skiaFontCacheItems.Add(skiaFontCacheItem);

                    return skiaFontCacheItem;
                }
            }
            finally
            {
                if (!IsDisposed())
                {
                    _lock.ExitReadLock();
                }
            }
        }

        private static SKPath GetPathInternal(IFont font, int code)
        {
            // TODO - check if font can even have path info

            if (font.TryGetNormalisedPath(code, out var nPath))
            {
                var gp = new SKPath() { FillType = SKPathFillType.EvenOdd };

                foreach (var subpath in nPath)
                {
                    foreach (var command in subpath.Commands)
                    {
                        if (command is PdfSubpath.Move move)
                        {
                            gp.MoveTo((float)move.Location.X, (float)move.Location.Y);
                        }
                        else if (command is PdfSubpath.Line line)
                        {
                            gp.LineTo((float)line.To.X, (float)line.To.Y);
                        }
                        else if (command is PdfSubpath.CubicBezierCurve cubic)
                        {
                            gp.CubicTo((float)cubic.FirstControlPoint.X, (float)cubic.FirstControlPoint.Y,
                                (float)cubic.SecondControlPoint.X, (float)cubic.SecondControlPoint.Y,
                                (float)cubic.EndPoint.X, (float)cubic.EndPoint.Y);
                        }
                        else if (command is PdfSubpath.QuadraticBezierCurve quadratic)
                        {
                            gp.QuadTo((float)quadratic.ControlPoint.X, (float)quadratic.ControlPoint.Y,
                                (float)quadratic.EndPoint.X, (float)quadratic.EndPoint.Y);
                        }
                        else if (command is PdfSubpath.Close)
                        {
                            gp.Close();
                        }
                    }
                }

                // TODO - check/benchmark if useful to Simplify()
                var simplified = new SKPath();
                if (gp.Simplify(simplified))
                {
                    gp.Dispose();
                    return simplified;
                }

                simplified.Dispose();
                return gp;
            }

            return null;
        }

        public bool TryGetPath(IFont font, int code, out SKPath path)
        {
            ConcurrentDictionary<int, Lazy<SKPath>> fontCache = _cache.GetOrAdd(font, new ConcurrentDictionary<int, Lazy<SKPath>>());

            Lazy<SKPath> glyph = fontCache.GetOrAdd(code, c => new Lazy<SKPath>(() => GetPathInternal(font, c)));

            if (glyph.Value is null)
            {
                path = null;
                return false;
            }

            path = glyph.Value;
            return true;
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
