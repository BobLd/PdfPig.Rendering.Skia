/*
 * Copyright 2024 BobLd
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *    http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Concurrent;
using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.SystemFonts;
using UglyToad.PdfPig.PdfFonts;

namespace UglyToad.PdfPig.Rendering.Skia.Helpers
{
    internal sealed class FontCache : IDisposable
    {
        private readonly ConcurrentDictionary<int, ConcurrentDictionary<int, Lazy<SKPath>>> _cache =
            new ConcurrentDictionary<int, ConcurrentDictionary<int, Lazy<SKPath>>>();

        private readonly ConcurrentDictionary<string, SKTypeface> _typefaces =
            new ConcurrentDictionary<string, SKTypeface>();

        private readonly SKFontManager skFontManager;

        public FontCache()
        {
            skFontManager = SKFontManager.CreateDefault();
        }

        public void Dispose()
        {
            skFontManager.Dispose();

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

        public SKTypeface GetTypefaceOrFallback(IFont font, string unicode)
        {
            using (var style = font.Details.GetFontStyle())
            {
                if (_typefaces.TryGetValue(font.Name, out SKTypeface drawTypeface) && drawTypeface != null &&
                    (string.IsNullOrWhiteSpace(unicode) ||
                     drawTypeface.ContainsGlyph(unicode[0]))) // Check if can render
                {
                    if (FontStyleEquals(drawTypeface.FontStyle, style))
                    {
                        return drawTypeface;
                    }

                    drawTypeface = skFontManager.MatchTypeface(drawTypeface, style);

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
                if (!string.IsNullOrWhiteSpace(unicode) && !drawTypeface.ContainsGlyph(unicode[0]))
                {
                    var fallback = skFontManager.MatchCharacter(unicode[0]);
                    if (fallback != null)
                    {
                        drawTypeface.Dispose();
                        drawTypeface = skFontManager.MatchTypeface(fallback, style);
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

        public bool TryGetPath(IFont font, int code, out SKPath path)
        {
            // TODO - check if font can even have path info

            var fontCache = _cache.GetOrAdd(GetFontKey(font), new ConcurrentDictionary<int, Lazy<SKPath>>());

            Lazy<SKPath> glyph = fontCache.GetOrAdd(code, c =>
            {
                return new Lazy<SKPath>(() =>
                {
                    if (font.TryGetNormalisedPath(c, out var nPath))
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
                                else if (command is PdfSubpath.BezierCurve curve)
                                {
                                    // TODO - PdfPig needs to be fixed to properly handle Quad / Cubic curves
                                    // See https://github.com/UglyToad/PdfPig/issues/625
                                    if (curve.StartPoint.Equals(curve.FirstControlPoint))
                                    {
                                        // Quad curve
                                        gp.QuadTo((float)curve.SecondControlPoint.X, (float)curve.SecondControlPoint.Y,
                                            (float)curve.EndPoint.X, (float)curve.EndPoint.Y);
                                    }
                                    else
                                    {
                                        // Cubic curve
                                        gp.CubicTo((float)curve.FirstControlPoint.X, (float)curve.FirstControlPoint.Y,
                                            (float)curve.SecondControlPoint.X, (float)curve.SecondControlPoint.Y,
                                            (float)curve.EndPoint.X, (float)curve.EndPoint.Y);
                                    }
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
                });
            });

            if (glyph.Value != null)
            {
                path = glyph.Value;
                return true;
            }

            path = null;
            return false;
        }

        private static int GetFontKey(IFont font)
        {
            // https://stackoverflow.com/questions/263400/what-is-the-best-algorithm-for-overriding-gethashcode?lq=1
            // https://thomaslevesque.com/2020/05/15/things-every-csharp-developer-should-know-1-hash-codes/
            unchecked
            {
                // 1430287 amd 7302013 are prime numbers
                int hashcode = 1430287;
                hashcode = hashcode * 7302013 ^ font.Name.GetHashCode();
                hashcode = hashcode * 7302013 ^ font.GetType().Name.GetHashCode();
                hashcode = hashcode * 7302013 ^ font.IsVertical.GetHashCode();
                hashcode = hashcode * 7302013 ^ font.Details.IsItalic.GetHashCode();
                hashcode = hashcode * 7302013 ^ font.Details.IsBold.GetHashCode();
                hashcode = hashcode * 7302013 ^ font.Details.Weight.GetHashCode();
                return hashcode;
            }
            //return HashCode.Combine(...)
        }
    }
}
