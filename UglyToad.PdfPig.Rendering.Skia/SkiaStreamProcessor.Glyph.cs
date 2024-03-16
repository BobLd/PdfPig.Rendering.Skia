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
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.PdfFonts;
using UglyToad.PdfPig.Rendering.Skia.Helpers;

namespace UglyToad.PdfPig.Rendering.Skia
{
    internal partial class SkiaStreamProcessor
    {
        public override void RenderGlyph(IFont font, IColor strokingColor, IColor nonStrokingColor, TextRenderingMode textRenderingMode, double fontSize, double pointSize, int code, string unicode, long currentOffset,
            TransformationMatrix renderingMatrix, TransformationMatrix textMatrix, TransformationMatrix transformationMatrix, CharacterBoundingBox characterBoundingBox)
        {
            if (textRenderingMode == TextRenderingMode.Neither)
            {
                return;
            }

            if (_fontCache.TryGetPath(font, code, out SKPath path))
            {
                ShowVectorFontGlyph(path, strokingColor, nonStrokingColor, textRenderingMode, renderingMatrix,
                    textMatrix, transformationMatrix);
            }
            else
            {
                if (!CanRender(unicode))
                {
                    return;
                }

                ShowNonVectorFontGlyph(font, strokingColor, nonStrokingColor, textRenderingMode, pointSize, unicode,
                    renderingMatrix, textMatrix, transformationMatrix, characterBoundingBox);
            }
        }

        private void ShowVectorFontGlyph(SKPath path, IColor strokingColor, IColor nonStrokingColor,
            TextRenderingMode textRenderingMode, TransformationMatrix renderingMatrix, TransformationMatrix textMatrix,
            TransformationMatrix transformationMatrix)
        {
            bool? stroke = textRenderingMode.IsStroke();
            if (!stroke.HasValue)
            {
                return;
            }

            bool? fill = textRenderingMode.IsFill();
            if (!fill.HasValue)
            {
                return;
            }

            if (!fill.Value && !stroke.Value)
            {
                // No stroke and no fill -> nothing to do
                return;
            }

            var transformMatrix = renderingMatrix.ToSkMatrix()
                .PostConcat(textMatrix.ToSkMatrix())
                .PostConcat(transformationMatrix.ToSkMatrix())
                .PostConcat(_yAxisFlipMatrix); // Inverse direction of y-axis

            var currentState = GetCurrentState();

            using (var transformedPath = new SKPath())
            {
                path.Transform(transformMatrix, transformedPath);
                if (fill.Value)
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

                if (stroke.Value)
                {
                    // Then stroke
                    var strokePaint = _paintCache.GetPaint(strokingColor, currentState.AlphaConstantStroking, true,
                        (float)currentState.LineWidth, currentState.JoinStyle, currentState.CapStyle,
                        currentState.LineDashPattern, currentState.CurrentTransformationMatrix);
                    _canvas.DrawPath(transformedPath, strokePaint);
                }
            }

            /* // No caching method
            var style = textRenderingMode.ToSKPaintStyle();
            if (!style.HasValue)
            {
                return;
            }

            var color = style == SKPaintStyle.Stroke ? strokingColor : nonStrokingColor; // TODO - very not correct

            using (var transformedPath = new SKPath())
            using (var fillBrush = new SKPaint())
            {
                //BlendMode = GetCurrentState().BlendMode.ToSKBlendMode(), // TODO - check if correct
                fillBrush.Style = style.Value;
                fillBrush.Color = color.ToSKColor(GetCurrentState().AlphaConstantNonStroking);
                fillBrush.IsAntialias = _antiAliasing;
                path.Transform(transformMatrix, transformedPath);
                _canvas!.DrawPath(transformedPath, fillBrush);
            }
            */
        }

        private void ShowNonVectorFontGlyph(IFont font, IColor strokingColor, IColor nonStrokingColor,
            TextRenderingMode textRenderingMode,
            double pointSize, string unicode, TransformationMatrix renderingMatrix, TransformationMatrix textMatrix,
            TransformationMatrix transformationMatrix, CharacterBoundingBox characterBoundingBox)
        {
            // TODO - Handle Fill

            var style = textRenderingMode.ToSKPaintStyle();
            if (!style.HasValue)
            {
                return;
            }

            var color = style == SKPaintStyle.Stroke ? strokingColor : nonStrokingColor; // TODO - very not correct

            var transformedPdfBounds = PerformantRectangleTransformer
                .Transform(renderingMatrix, textMatrix, transformationMatrix,
                    new PdfRectangle(0, 0, characterBoundingBox.Width, UserSpaceUnit.PointMultiples));
            var startBaseLine = transformedPdfBounds.BottomLeft.ToSKPoint(_height);

            if (transformedPdfBounds.Rotation != 0)
            {
                _canvas.RotateDegrees((float)-transformedPdfBounds.Rotation, startBaseLine.X, startBaseLine.Y);
            }

            float skew = ComputeSkewX(transformedPdfBounds);

            using (var drawTypeface = _fontCache.GetTypefaceOrFallback(font, unicode))
            using (var fontPaint = new SKPaint(drawTypeface.ToFont((float)pointSize, 1f, -skew)))
            {
                fontPaint.Style = style.Value;
                fontPaint.Color = color.ToSKColor(GetCurrentState().AlphaConstantNonStroking);
                fontPaint.IsAntialias = _antiAliasing;

                _canvas.DrawText(unicode, startBaseLine, fontPaint);
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
            // We always render glyphs for the moment
            return true;
        }
    }
}
