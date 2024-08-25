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
using System.Text;
using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.SystemFonts;
using UglyToad.PdfPig.PdfFonts;

namespace UglyToad.PdfPig.Rendering.Skia.Helpers
{
    internal sealed class FontCache : IDisposable
    {
        private readonly ConcurrentDictionary<IFont, ConcurrentDictionary<int, Lazy<SKPath>>> _cache =
            new ConcurrentDictionary<IFont, ConcurrentDictionary<int, Lazy<SKPath>>>();

        private readonly ConcurrentDictionary<string, SKTypeface> _typefaces =
            new ConcurrentDictionary<string, SKTypeface>();

        private readonly SKFontManager _skFontManager = SKFontManager.CreateDefault();

        public SKTypeface GetTypefaceOrFallback(IFont font, string unicode)
        {
            using (var style = font.Details.GetFontStyle())
            {
                var codepoint = BitConverter.ToInt32(Encoding.UTF32.GetBytes(unicode), 0);

                if (_typefaces.TryGetValue(font.Name, out SKTypeface drawTypeface) && drawTypeface != null &&
                    (string.IsNullOrWhiteSpace(unicode) ||
                     drawTypeface.ContainsGlyph(codepoint))) // Check if can render
                {
                    if (FontStyleEquals(drawTypeface.FontStyle, style))
                    {
                        return drawTypeface;
                    }

                    drawTypeface = _skFontManager.MatchFamily(drawTypeface.FamilyName, style);

                    if (drawTypeface != null)
                    {
                        return drawTypeface;
                    }
                }

                string cleanFontName = font.GetCleanFontName();

                drawTypeface = SKTypeface.FromFamilyName(cleanFontName, style);

                if (drawTypeface.FamilyName.Equals(SKTypeface.Default.FamilyName))
                {
                    var trueTypeFont = SystemFontFinder.Instance.GetTrueTypeFont(cleanFontName);

                    if (trueTypeFont != null &&
                        !string.IsNullOrEmpty(trueTypeFont.TableRegister.NameTable.FontFamilyName))
                    {
                        drawTypeface.Dispose();
                        drawTypeface =
                            SKTypeface.FromFamilyName(trueTypeFont.TableRegister.NameTable.FontFamilyName, style);
                    }
                }

                // Fallback font
                // https://github.com/mono/SkiaSharp/issues/232
                if (!string.IsNullOrWhiteSpace(unicode) && !drawTypeface.ContainsGlyph(codepoint))
                {
                    var fallback = _skFontManager.MatchCharacter(codepoint);
                    if (fallback != null)
                    {
                        drawTypeface.Dispose();
                        drawTypeface = _skFontManager.MatchFamily(fallback.FamilyName, style);
                    }
                }

                _typefaces[font.Name] = drawTypeface;

                return drawTypeface;
            }
        }

        private static bool FontStyleEquals(SKFontStyle fontStyle1, SKFontStyle fontStyle2)
        {
            return fontStyle1.Width == fontStyle2.Width &&
                   fontStyle1.Weight == fontStyle2.Weight &&
                   fontStyle1.Slant == fontStyle2.Slant;
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

        public void Dispose()
        {
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
                    typeface?.Dispose();
                }
            }

            _typefaces.Clear();
        }
    }
}
