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
using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Graphics.Core;
using UglyToad.PdfPig.PdfFonts;

namespace UglyToad.PdfPig.Rendering.Skia.Helpers
{
    internal static class SkiaExtensions
    {
        private const float OneOver72 = (float)(1.0 / 72.0);

        private static readonly string DefaultFamilyName = SKTypeface.Default.FamilyName;

        public static bool IsDefault(this SKTypeface typeface)
        {
            return typeface.FamilyName.Equals(DefaultFamilyName);
        }

        public static SKFontStyle GetFontStyle(this FontDetails fontDetails)
        {
            if (fontDetails.IsBold && fontDetails.IsItalic)
            {
                return SKFontStyle.BoldItalic;
            }

            if (fontDetails.IsBold)
            {
                return SKFontStyle.Bold;
            }

            return fontDetails.IsItalic ? SKFontStyle.Italic : SKFontStyle.Normal;
        }

        public static string? GetCleanFontName(this IFont font)
        {
            string? fontName = font.Name?.Data;
            if (fontName is null)
            {
                return null;
            }

            if (fontName.Length <= 7 || !fontName[6].Equals('+'))
            {
                return fontName;
            }

            for (int c = 0; c < 6; ++c)
            {
                if (!char.IsUpper(fontName[c]))
                {
                    return fontName;
                }
            }

            return fontName.Substring(7);
        }

        public static SKRect ToSKRect(this PdfRectangle rect, float height)
        {
            float left = (float)rect.Left;
            float top = (float)(height - rect.Top);
            float right = left + (float)rect.Width;
            float bottom = top + (float)rect.Height;
            return new SKRect(left, top, right, bottom);
        }

        public static SKRectI ToSKRectI(this PdfRectangle rect, float height)
        {
            double left = rect.Left;
            double top = height - rect.Top;
            double right = left + rect.Width;
            double bottom = top + rect.Height;
            return new SKRectI((int)left, (int)top, (int)right, (int)bottom); // TODO - rounding
        }

        public static SKPoint ToSKPoint(this PdfPoint pdfPoint, float height)
        {
            return new SKPoint((float)pdfPoint.X, (float)(height - pdfPoint.Y));
        }

        public static SKStrokeJoin ToSKStrokeJoin(this LineJoinStyle lineJoinStyle)
        {
            return lineJoinStyle switch
            {
                LineJoinStyle.Bevel => SKStrokeJoin.Bevel,
                LineJoinStyle.Miter => SKStrokeJoin.Miter,
                LineJoinStyle.Round => SKStrokeJoin.Round,
                _ => throw new NotImplementedException($"Unknown LineJoinStyle '{lineJoinStyle}'.")
            };
        }

        public static SKStrokeCap ToSKStrokeCap(this LineCapStyle lineCapStyle)
        {
            return lineCapStyle switch
            {
                LineCapStyle.Butt => SKStrokeCap.Butt,
                LineCapStyle.ProjectingSquare => SKStrokeCap.Square,
                LineCapStyle.Round => SKStrokeCap.Round,
                _ => throw new NotImplementedException($"Unknown LineCapStyle '{lineCapStyle}'.")
            };
        }

        public static SKPathEffect? ToSKPathEffect(this LineDashPattern lineDashPattern, float scale)
        {
            //const float oneOver72 = (float)(1.0 / 72.0);

            if (lineDashPattern.Phase == 0 && !(lineDashPattern.Array?.Count > 0))
            {
                return null;
            }

            float phase = lineDashPattern.Phase / scale; // Divide

            switch (lineDashPattern.Array.Count)
            {
                case 1:
                    {
                        var v = (float)lineDashPattern.Array[0] * scale; // Multiply
                        if (v == 0)
                        {
                            v = OneOver72; // TODO - Add tests
                        }
                        return SKPathEffect.CreateDash([v, v], phase);
                    }
                case > 0:
                    {
                        float[] pattern = new float[lineDashPattern.Array.Count];
                        for (int i = 0; i < lineDashPattern.Array.Count; i++)
                        {
                            var v = (float)lineDashPattern.Array[i];
                            if (v == 0)
                            {
                                pattern[i] = OneOver72; // See APISmap1.pdf
                            }
                            else
                            {
                                pattern[i] = v * scale; // Multiply
                            }
                        }

                        return SKPathEffect.CreateDash(pattern, phase);
                    }
                default:
                    return SKPathEffect.CreateDash([0, 0], phase);
            }
        }

        public static bool IsStroke(this TextRenderingMode textRenderingMode)
        {
            switch (textRenderingMode)
            {
                case TextRenderingMode.Stroke:
                case TextRenderingMode.StrokeClip:
                case TextRenderingMode.FillThenStroke:
                case TextRenderingMode.FillThenStrokeClip:
                    return true;

                case TextRenderingMode.Fill:
                case TextRenderingMode.FillClip:
                case TextRenderingMode.NeitherClip:
                case TextRenderingMode.Neither:
                    return false;

                default:
                    return false;
            }
        }

        public static bool IsFill(this TextRenderingMode textRenderingMode)
        {
            switch (textRenderingMode)
            {
                case TextRenderingMode.Fill:
                case TextRenderingMode.FillClip:
                case TextRenderingMode.FillThenStroke:
                case TextRenderingMode.FillThenStrokeClip:
                    return true;

                case TextRenderingMode.Stroke:
                case TextRenderingMode.StrokeClip:
                case TextRenderingMode.NeitherClip:
                case TextRenderingMode.Neither:
                    return false;

                default:
                    return false;
            }
        }

        public static SKPaintStyle? ToSKPaintStyle(this TextRenderingMode textRenderingMode)
        {
            // TODO - to finish, not correct
            switch (textRenderingMode)
            {
                case TextRenderingMode.Stroke:
                case TextRenderingMode.StrokeClip:
                    return SKPaintStyle.Stroke;

                case TextRenderingMode.Fill:
                case TextRenderingMode.FillClip:
                    return SKPaintStyle.Fill;

                case TextRenderingMode.FillThenStroke:
                case TextRenderingMode.FillThenStrokeClip:
                    return SKPaintStyle.StrokeAndFill;

                case TextRenderingMode.NeitherClip:
                case TextRenderingMode.Neither:
                default:
                    return null;
            }
        }

        public static SKPathFillType ToSKPathFillType(this FillingRule fillingRule)
        {
            return fillingRule == FillingRule.NonZeroWinding ? SKPathFillType.Winding : SKPathFillType.EvenOdd;
        }

        public static SKColor ToSKColor(this IColor pdfColor, double alpha)
        {
            if (pdfColor is not null)
            {
                var (r, g, b) = pdfColor.ToRGBValues();

                if (r >= 0 && r <= 1 && g >= 0 && g <= 1 && b >= 0 && b <= 1)
                {
                    // This is the expected case
                    return new SKColor(
                        Convert.ToByte(r * 255),
                        Convert.ToByte(g * 255),
                        Convert.ToByte(b * 255),
                        Convert.ToByte(alpha * 255));
                }

                // Should never happen, but see GHOSTSCRIPT-686749-1.pdf
                return new SKColor(
                    ConvertToByte(r),
                    ConvertToByte(g),
                    ConvertToByte(b),
                    Convert.ToByte(alpha * 255));
            }

            return SKColors.Black.WithAlpha(Convert.ToByte(alpha * 255));
        }

        private static byte ConvertToByte(double v)
        {
            return v switch
            {
                >= byte.MaxValue => byte.MaxValue,
                <= byte.MinValue => byte.MinValue,
                _ => (byte)v
            };
        }
        
        public static SKMatrix ToSkMatrix(this TransformationMatrix transformationMatrix)
        {
            return new SKMatrix((float)transformationMatrix.A, (float)transformationMatrix.C,
                (float)transformationMatrix.E,
                (float)transformationMatrix.B, (float)transformationMatrix.D, (float)transformationMatrix.F,
                0, 0, 1);
        }

        private static bool doBlending = false;

        public static SKBlendMode ToSKBlendMode(this BlendMode blendMode)
        {
            if (!doBlending)
            {
                return SKBlendMode.SrcOver;
            }

            // https://pdfium.googlesource.com/pdfium/+/refs/heads/main/core/fxge/skia/fx_skia_device.cpp
            switch (blendMode)
            {
                // 11.3.5.2 Separable blend modes
                case BlendMode.Normal: // aka Compatible
                    return SKBlendMode.SrcOver;

                case BlendMode.Multiply:
                    return SKBlendMode.Multiply;

                case BlendMode.Screen:
                    return SKBlendMode.Screen;

                case BlendMode.Overlay:
                    return SKBlendMode.Overlay;

                case BlendMode.Darken:
                    return SKBlendMode.Darken;

                case BlendMode.Lighten:
                    return SKBlendMode.Lighten;

                case BlendMode.ColorDodge:
                    return SKBlendMode.ColorDodge;

                case BlendMode.ColorBurn:
                    return SKBlendMode.ColorBurn;

                case BlendMode.HardLight:
                    return SKBlendMode.HardLight;

                case BlendMode.SoftLight:
                    return SKBlendMode.SoftLight;

                case BlendMode.Difference:
                    return SKBlendMode.Difference;

                case BlendMode.Exclusion:
                    return SKBlendMode.Exclusion;

                // 11.3.5.3 Non-separable blend modes
                case BlendMode.Hue:
                    return SKBlendMode.Hue;

                case BlendMode.Saturation:
                    return SKBlendMode.Saturation;

                case BlendMode.Color:
                    return SKBlendMode.Color;

                case BlendMode.Luminosity:
                    return SKBlendMode.Luminosity;

                default:
                    throw new NotImplementedException($"Cannot convert blend mode '{blendMode}' to SKBlendMode.");
            }
        }
    }
}
