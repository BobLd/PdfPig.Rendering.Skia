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
using System.Runtime.CompilerServices;
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

        public static SKRect ToSKRect(this PdfRectangle rect)
        {
            float left = (float)rect.Left;
            float bottom = (float)rect.BottomLeft.Y;
            float right = left + (float)rect.Width;
            float top = bottom + (float)rect.Height;
            return new SKRect(left, top, right, bottom);
        }

        public static SKRectI ToSKRectI(this PdfRectangle rect)
        {
            double left = rect.Left;
            double top = rect.Top;
            double right = left + rect.Width;
            double bottom = top + rect.Height;
            return new SKRectI((int)left, (int)top, (int)right, (int)bottom); // TODO - rounding
        }

        public static SKPoint ToSKPoint(this PdfPoint pdfPoint)
        {
            return new SKPoint((float)pdfPoint.X, (float)pdfPoint.Y);
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

        public static SKPathEffect? ToSKPathEffect(this LineDashPattern lineDashPattern, float scale = 1.0f)
        {
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
                if (pdfColor is CMYKColor cmyk)
                {
                    return cmyk.ToSKColor(alpha);
                }

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

        public static SKColor ToSKColor(this CMYKColor cmyk, double alpha)
        {
            byte c, m, y, k;
            if (cmyk.C >= 0 && cmyk.C <= 1 && cmyk.M >= 0 && cmyk.M <= 1 &&
                cmyk.Y >= 0 && cmyk.Y <= 1 && cmyk.K >= 0 && cmyk.K <= 1)
            {
                // This is the expected case
                c = ConvertToByte(cmyk.C * 255);
                m = ConvertToByte(cmyk.M * 255);
                y = ConvertToByte(cmyk.Y * 255);
                k  = ConvertToByte(cmyk.K * 255);
            }
            else
            {
                // Should never happen, but happens with RGB color space in GHOSTSCRIPT-686749-1.pdf
                c = ConvertToByte(cmyk.C);
                m = ConvertToByte(cmyk.M);
                y = ConvertToByte(cmyk.Y);
                k = ConvertToByte(cmyk.K);
            }

            ApproximateCmykToRgb(c, m, y, k, out byte r, out byte g, out byte b);
            return new SKColor(r, g, b, Convert.ToByte(alpha * 255));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte ConvertToByte(double v)
        {
            return v switch
            {
                >= byte.MaxValue => byte.MaxValue,
                <= byte.MinValue => byte.MinValue,
                _ => (byte)v
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void ApproximateCmykToRgb(in byte cIn, in byte mIn, in byte yIn, in byte kIn, out byte r, out byte g, out byte b)
        {
            // From https://graphicdesign.stackexchange.com/questions/114260/alternative-formulae-for-cmyk-to-rgb-conversion-for-display-on-screen

            // inputs are c,m,y,k components on a 0-1 scale
            // work with INVERSE of CMYK values, on a 0-255 scale
            float c = 255 * (1 - cIn / 255f);
            float m = 255 * (1 - mIn / 255f);
            float y = 255 * (1 - yIn / 255f);
            float k = 255 * (1 - kIn / 255f);

            float rf = 80 + 0.5882f * c - 0.3529f * m - 0.1373f * y + 0.00185f * c * m + 0.00046f * y * c; // no YM
            float gf = 66 - 0.1961f * c + 0.2745f * m - 0.0627f * y + 0.00215f * c * m + 0.00008f * y * c + 0.00062f * y * m;
            float bf = 86 - 0.3255f * c - 0.1569f * m + 0.1647f * y + 0.00046f * c * m + 0.00123f * y * c + 0.00215f * y * m;

            r = Clamp(rf * k / 255);
            g = Clamp(gf * k / 255);
            b = Clamp(bf * k / 255);

            static byte Clamp(float value)
            {
                if (value < 0) return 0;
                if (value > 255) return 255;
                return (byte)value;
            }

            // See also https://github.com/UglyToad/PdfPig/issues/1144
        }

        public static SKMatrix ToSkMatrix(this TransformationMatrix transformationMatrix)
        {
            return new SKMatrix((float)transformationMatrix.A, (float)transformationMatrix.C,
                (float)transformationMatrix.E,
                (float)transformationMatrix.B, (float)transformationMatrix.D, (float)transformationMatrix.F,
                0, 0, 1);
        }

        /// <summary>
        /// Returns <see langword="true"/> if the paint operation should be skipped due to overprint.
        /// <para>
        /// Implements a screen-rendering approximation of PDF overprint control (§8.6.7):
        /// <list type="bullet">
        ///   <item><b>DeviceCMYK + OPM=1:</b> A zero-valued component is preserved from the background.
        ///   We skip the paint only when ALL four components are zero (white/no-ink overprint).
        ///   Partial-zero cases require per-channel compositing which is not feasible in Skia.</item>
        ///   <item><b>Separation + overprint:</b> A tint of 0 applies no colorant; skip when the
        ///   resolved alternate-space color carries no ink.</item>
        ///   <item><b>DeviceN + overprint:</b> Skip when all components resolve to no ink.</item>
        ///   <item><b>OPM=0 / DeviceGray / DeviceRGB / …:</b> No skip; current painting is already
        ///   correct for a screen renderer.</item>
        /// </list>
        /// </para>
        /// </summary>
        internal static bool ShouldSkipForOverprint(
            bool overprintActive,
            double overprintMode,
            IColor? color,
            ColorSpaceDetails? colorSpaceDetails)
        {
            if (!overprintActive || color is null)
            {
                return false;
            }

            switch (colorSpaceDetails?.Type ?? color.ColorSpace)
            {
                case ColorSpace.DeviceCMYK:
                    // OPM=0: zero components knock out that channel, which is the default raster
                    // behaviour — no change needed.
                    // OPM=1: zero components are preserved from the background.
                    //   Approximation: skip only when ALL components are zero (no-ink / white overprint).
                    if (overprintMode >= 1 && color is CMYKColor cmyk)
                    {
                        return cmyk.C == 0 && cmyk.M == 0 && cmyk.Y == 0 && cmyk.K == 0;
                    }
                    return false;

                case ColorSpace.Separation:
                    // A Separation tint of 0 means no colorant is applied; with overprint the
                    // background for that colorant is preserved → no-op on screen.
                    return IsNoInk(color);

                case ColorSpace.DeviceN:
                    // All-zero DeviceN components means no colorant; with overprint → no-op.
                    return IsNoInk(color);

                default:
                    // DeviceGray, DeviceRGB, CIE-based, ICCBased (non-CMYK), Indexed, Pattern:
                    // Overprint is not approximatable for screen rendering.
                    return false;
            }
        }

        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="color"/> carries no ink
        /// (i.e. it is the subtractive equivalent of white / no colorant applied).
        /// </summary>
        private static bool IsNoInk(IColor color)
        {
            if (color is CMYKColor cmyk)
            {
                return cmyk.C == 0 && cmyk.M == 0 && cmyk.Y == 0 && cmyk.K == 0;
            }

            if (color is GrayColor gray)
            {
                // Gray=1.0 is white; white = no ink in a subtractive model.
                return gray.Gray >= 1.0;
            }

            if (color is RGBColor rgb)
            {
                // RGB(1,1,1) is white; treat as no ink for Separation/DeviceN alternate spaces.
                return rgb.R >= 1.0 && rgb.G >= 1.0 && rgb.B >= 1.0;
            }

            return false;
        }

        public static SKBlendMode ToSKBlendMode(this BlendMode blendMode)
        {

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
