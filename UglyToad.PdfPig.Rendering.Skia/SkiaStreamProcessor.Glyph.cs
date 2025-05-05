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
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.PdfFonts;
using UglyToad.PdfPig.Rendering.Skia.Helpers;

namespace UglyToad.PdfPig.Rendering.Skia
{
    internal partial class SkiaStreamProcessor
    {
        public override void RenderGlyph(IFont font,
            CurrentGraphicsState currentState,
            double fontSize,
            double pointSize,
            int code,
            string unicode,
            long currentOffset,
            in TransformationMatrix renderingMatrix,
            in TransformationMatrix textMatrix,
            in TransformationMatrix transformationMatrix,
            CharacterBoundingBox characterBoundingBox)
        {
            var textRenderingMode = currentState.FontState.TextRenderingMode;

            if (!textRenderingMode.IsFill() && !textRenderingMode.IsStroke())
            {
                // No stroke and no fill -> nothing to do
                return;
            }

            var strokingColor = currentState.CurrentStrokingColor!;
            var nonStrokingColor = currentState.CurrentNonStrokingColor!;

            if (_fontCache.TryGetPath(font, code, out SKPath path))
            {
                if (path.IsEmpty)
                {
                    // If null or whitespace, ignore
                    if (string.IsNullOrWhiteSpace(unicode))
                    {
                        return;
                    }

                    ParsingOptions.Logger.Debug($"RenderGlyph: VectorFontGlyph path is empty for '{unicode}' ({font.Details}), falling back to NonVectorFontGlyph.");
                    ShowNonVectorFontGlyph(font, strokingColor, nonStrokingColor, textRenderingMode, pointSize, unicode,
                        in renderingMatrix, in textMatrix, in transformationMatrix, characterBoundingBox);
                }
                else
                {
                    ShowVectorFontGlyph(path, strokingColor, nonStrokingColor, textRenderingMode, in renderingMatrix,
                        in textMatrix, in transformationMatrix);
                }
            }
            else
            {
                ShowNonVectorFontGlyph(font, strokingColor, nonStrokingColor, textRenderingMode, pointSize, unicode,
                    in renderingMatrix, in textMatrix, in transformationMatrix, characterBoundingBox);
            }
        }

        private void ShowVectorFontGlyph(SKPath path, IColor strokingColor, IColor nonStrokingColor,
            TextRenderingMode textRenderingMode, in TransformationMatrix renderingMatrix,
            in TransformationMatrix textMatrix, in TransformationMatrix transformationMatrix)
        {
            bool stroke = textRenderingMode.IsStroke();
            bool fill = textRenderingMode.IsFill();

            var transformMatrix = renderingMatrix.ToSkMatrix()
                .PostConcat(textMatrix.ToSkMatrix())
                .PostConcat(transformationMatrix.ToSkMatrix())
                .PostConcat(_yAxisFlipMatrix); // Inverse direction of y-axis

            var currentState = GetCurrentState();

            using (var transformedPath = new SKPath())
            {
                path.Transform(transformMatrix, transformedPath);

                if (_canvas.QuickReject(transformedPath))
                {
                    return;
                }

                if (fill)
                {
                    // Do fill first
                    if (nonStrokingColor != null && nonStrokingColor.ColorSpace == ColorSpace.Pattern)
                    {
                        // TODO - Clean shading patterns painting
                        // See documents:
                        // - GHOSTSCRIPT-693120-0
                        // - GHOSTSCRIPT-698721-0.zip-6
                        // - GHOSTSCRIPT-698721-1_1

                        if (!(nonStrokingColor is PatternColor pattern))
                        {
                            throw new ArgumentNullException($"Expecting a {nameof(PatternColor)} but got {nonStrokingColor.GetType()}");
                        }

                        switch (pattern.PatternType)
                        {
                            case PatternType.Tiling:
                                RenderTilingPattern(transformedPath, pattern as TilingPatternColor, false);
                                break;

                            case PatternType.Shading:
                                RenderShadingPattern(transformedPath, pattern as ShadingPatternColor, false);
                                break;
                        }
                    }
                    else
                    {
                        var fillBrush = _paintCache.GetPaint(nonStrokingColor, currentState.AlphaConstantNonStroking, false,
                            null, null, null, null, null);
                        _canvas.DrawPath(transformedPath, fillBrush);
                    }
                }

                if (stroke)
                {
                    // Then stroke
                    var strokePaint = _paintCache.GetPaint(strokingColor, currentState.AlphaConstantStroking, true,
                        (float)currentState.LineWidth, currentState.JoinStyle, currentState.CapStyle,
                        currentState.LineDashPattern, currentState.CurrentTransformationMatrix);
                    _canvas.DrawPath(transformedPath, strokePaint);
                }
            }
        }

        private void ShowNonVectorFontGlyph(IFont font, IColor strokingColor, IColor nonStrokingColor,
            TextRenderingMode textRenderingMode,
            double pointSize, string unicode, in TransformationMatrix renderingMatrix,
            in TransformationMatrix textMatrix, in TransformationMatrix transformationMatrix,
            CharacterBoundingBox characterBoundingBox)
        {
            if (!CanRender(unicode))
            {
                return;
            }

            // TODO - Handle Fill

            var style = textRenderingMode.ToSKPaintStyle();
            if (!style.HasValue)
            {
                return;
            }

            var transformedPdfBounds = PerformantRectangleTransformer
                .Transform(renderingMatrix, textMatrix, transformationMatrix,
                    new PdfRectangle(0, 0, characterBoundingBox.Width, UserSpaceUnit.PointMultiples));

            var startBaseLine = transformedPdfBounds.BottomLeft.ToSKPoint(_height);

            if (transformedPdfBounds.Rotation != 0)
            {
                _canvas.RotateDegrees((float)-transformedPdfBounds.Rotation, startBaseLine.X, startBaseLine.Y);
            }

            if (_canvas.QuickReject(transformedPdfBounds.ToSKRect(_height)))
            {
                return;
            }

            var color = style == SKPaintStyle.Stroke ? strokingColor : nonStrokingColor; // TODO - very not correct

            float skew = ComputeSkewX(transformedPdfBounds);

            var drawTypeface = _fontCache.GetTypefaceOrFallback(font, unicode);

            using (var skFont = drawTypeface.Typeface.ToFont((float)pointSize, 1f, -skew))
            using (var fontPaint = new SKPaint(skFont))
            {
                fontPaint.Style = style.Value;
                fontPaint.Color = color.ToSKColor(GetCurrentState().AlphaConstantNonStroking);
                fontPaint.IsAntialias = _antiAliasing;

                // TODO - Benchmark with SPARC - v9 Architecture Manual.pdf
                // as _canvas.DrawShapedText(unicode, startBaseLine, fontPaint); as very slow without 'Shaper' caching
                _canvas.DrawShapedText(drawTypeface.Shaper, unicode, startBaseLine, fontPaint);
                _canvas.ResetMatrix();
            }
        }

        private static float ComputeSkewX(PdfRectangle rectangle)
        {
            var rotationRadians = rectangle.Rotation * Math.PI / 180.0;
            var diffY = rectangle.TopLeft.Y - rectangle.BottomLeft.Y;
            var diffX = rectangle.TopLeft.X - rectangle.BottomLeft.X;

            var rotationBottomTopRadians = (float)Math.Atan2(diffY, diffX);

            // Test documents:
            // - SPARC - v9 Architecture Manual.pdf, page 1
            // - 68-1990-01_A.pdf, page 15

            return (float)Math.Round(Math.PI / 2.0 + rotationRadians - rotationBottomTopRadians, 5);
        }

        private static bool CanRender(string unicode)
        {
            ReadOnlySpan<char> chars = unicode.AsSpan();
            for (int i = 0; i < chars.Length; ++i)
            {
                if (char.IsControl(chars[i]))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
