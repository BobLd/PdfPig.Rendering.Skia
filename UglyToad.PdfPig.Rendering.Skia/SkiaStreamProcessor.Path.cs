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
using UglyToad.PdfPig.Rendering.Skia.Helpers;

namespace UglyToad.PdfPig.Rendering.Skia
{
    internal partial class SkiaStreamProcessor
    {
        private SKPath? _currentPath;

        public override void BeginSubpath()
        {
            _currentPath ??= new SKPath();
        }

        public override PdfPoint? CloseSubpath()
        {
            if (_currentPath is null)
            {
                return null;
            }

            _currentPath.Close();
            return null;
        }

        public override void MoveTo(double x, double y)
        {
            BeginSubpath();

            if (_currentPath is null)
            {
                return;
            }

            _currentPath.MoveTo((float)x, (float)y);
        }

        public override void LineTo(double x, double y)
        {
            if (_currentPath is null)
            {
                return;
            }

            _currentPath.LineTo((float)x, (float)y);
        }

        public override void BezierCurveTo(double x2, double y2, double x3, double y3)
        {
            if (_currentPath is null)
            {
                return;
            }

            _currentPath.QuadTo((float)x2, (float)y2, (float)x3, (float)y3);
        }

        public override void BezierCurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
        {
            if (_currentPath is null)
            {
                return;
            }

            _currentPath.CubicTo((float)x1, (float)y1, (float)x2, (float)y2, (float)x3, (float)y3);
        }

        public override void ClosePath()
        {
            // No op
        }

        public override void EndPath()
        {
            if (_currentPath is null)
            {
                return;
            }

            _currentPath.Dispose();
            _currentPath = null;
        }

        public override void Rectangle(double x, double y, double width, double height)
        {
            BeginSubpath();

            if (_currentPath == null)
            {
                return;
            }

            var left = (float)x;
            var top = (float)(y + height);
            var right = (float)(x + width);
            var bottom = (float)(y);

            _currentPath.AddRect(new SKRect(left, top, right, bottom));
        }

        public override void StrokePath(bool close)
        {
            if (_currentPath is null)
            {
                return;
            }

            if (close)
            {
                _currentPath.Close();
            }

            var currentState = GetCurrentState();

            PaintStrokePath(currentState);

            _currentPath.Dispose();
            _currentPath = null;
        }

        private void PaintStrokePath(CurrentGraphicsState currentState)
        {
            if (currentState.CurrentStrokingColor?.ColorSpace == ColorSpace.Pattern)
            {
                if (currentState.CurrentStrokingColor is not PatternColor pattern)
                {
                    throw new ArgumentNullException($"Expecting a {nameof(PatternColor)} but got {currentState.CurrentStrokingColor.GetType()}");
                }

                switch (pattern.PatternType)
                {
                    case PatternType.Tiling:
                        RenderTilingPatternCurrentPath(pattern as TilingPatternColor, true);
                        break;

                    case PatternType.Shading:
                        RenderShadingPatternCurrentPath(pattern as ShadingPatternColor, true);
                        break;
                }
            }
            else
            {
                var paint = _paintCache.GetPaint(currentState.CurrentStrokingColor, currentState.AlphaConstantStroking, true,
                    (float)currentState.LineWidth, currentState.JoinStyle, currentState.CapStyle,
                    currentState.LineDashPattern);
                _canvas.DrawPath(_currentPath, paint);
            }
        }

        public override void FillPath(FillingRule fillingRule, bool close)
        {
            if (_currentPath is null)
            {
                return;
            }

            if (close)
            {
                _currentPath.Close();
            }

            var currentState = GetCurrentState();

            PaintFillPath(currentState, fillingRule);

            _currentPath.Dispose();
            _currentPath = null;
        }

        private void PaintFillPath(CurrentGraphicsState currentState, FillingRule fillingRule)
        {
            if (_currentPath is null)
            {
                return;
            }

            _currentPath.FillType = fillingRule.ToSKPathFillType();

            if (currentState.CurrentNonStrokingColor?.ColorSpace == ColorSpace.Pattern)
            {
                if (currentState.CurrentNonStrokingColor is not PatternColor pattern)
                {
                    throw new ArgumentNullException($"Expecting a {nameof(PatternColor)} but got {currentState.CurrentStrokingColor.GetType()}");
                }

                switch (pattern.PatternType)
                {
                    case PatternType.Tiling:
                        RenderTilingPatternCurrentPath(pattern as TilingPatternColor, false);
                        break;

                    case PatternType.Shading:
                        RenderShadingPatternCurrentPath(pattern as ShadingPatternColor, false);
                        break;
                }
            }
            else
            {
                var paint = _paintCache.GetPaint(currentState.CurrentNonStrokingColor,
                    currentState.AlphaConstantNonStroking, false, null, null, null, null);
                _canvas.DrawPath(_currentPath, paint);

                /* No cache method
                using (SKPaint paint = new SKPaint()
                {
                    IsAntialias = _antiAliasing,
                    Color = currentState.GetCurrentNonStrokingColorSKColor(),
                    Style = SKPaintStyle.Fill
                })
                {
                    //paint.BlendMode = currentGraphicsState.BlendMode.ToSKBlendMode();
                    _canvas!.DrawPath(CurrentPath, paint);
                }
                */
            }
        }

        public override void FillStrokePath(FillingRule fillingRule, bool close)
        {
            if (_currentPath is null)
            {
                return;
            }

            if (close)
            {
                _currentPath.Close();
            }

            var currentState = GetCurrentState();

            PaintFillPath(currentState, fillingRule);
            PaintStrokePath(currentState);

            _currentPath.Dispose();
            _currentPath = null;
        }
    }
}
