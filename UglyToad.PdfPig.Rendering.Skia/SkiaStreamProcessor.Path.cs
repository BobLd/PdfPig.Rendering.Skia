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
using UglyToad.PdfPig.Graphics.Core;
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

            var cp = _currentPath.LastPoint;
            _currentPath.CubicTo(cp.X, cp.Y, (float)x2, (float)y2, (float)x3, (float)y3);
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

            // Replicate the PDF 're' operator as an explicit subpath rather than using
            // SKPath.AddRect. AddRect normalises the rectangle and always emits it with a
            // fixed winding direction, which discards the winding implied by the signs of
            // width/height.
            var x0 = (float)x;
            var y0 = (float)y;
            var x1 = (float)(x + width);
            var y1 = (float)(y + height);

            _currentPath.MoveTo(x0, y0);
            _currentPath.LineTo(x1, y0);
            _currentPath.LineTo(x1, y1);
            _currentPath.LineTo(x0, y1);
            _currentPath.Close();
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
                        RenderTilingPattern(_currentPath, pattern as TilingPatternColor, true);
                        break;

                    case PatternType.Shading:
                        RenderShadingPattern(_currentPath, pattern as ShadingPatternColor, true);
                        break;
                }
            }
            else
            {
                if (TryGetActiveSoftMask(out var softMask))
                {
                    // Per-paint soft mask: draw with Normal blend so the gs blend mode is
                    // applied at composite-back time inside DrawWithSoftMask, not during
                    // the inner draw onto the (transparent) layer.
                    var innerPaint = _paintCache.GetPaint(currentState.CurrentStrokingColor, currentState.AlphaConstantStroking, true,
                        (float)currentState.LineWidth, currentState.JoinStyle, currentState.CapStyle,
                        currentState.LineDashPattern, BlendMode.Normal);
                    var path = _currentPath;
                    DrawWithSoftMask(softMask!, currentState.BlendMode, () => _canvas.DrawPath(path, innerPaint));
                }
                else
                {
                    var paint = _paintCache.GetPaint(currentState.CurrentStrokingColor, currentState.AlphaConstantStroking, true,
                        (float)currentState.LineWidth, currentState.JoinStyle, currentState.CapStyle,
                        currentState.LineDashPattern, currentState.BlendMode);
                    _canvas.DrawPath(_currentPath, paint);
                }
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
                    throw new ArgumentNullException($"Expecting a {nameof(PatternColor)} but got {currentState.CurrentNonStrokingColor.GetType()}");
                }

                switch (pattern.PatternType)
                {
                    case PatternType.Tiling:
                        RenderTilingPattern(_currentPath, pattern as TilingPatternColor, false);
                        break;

                    case PatternType.Shading:
                        RenderShadingPattern(_currentPath, pattern as ShadingPatternColor, false);
                        break;
                }
            }
            else
            {
                if (TryGetActiveSoftMask(out var softMask))
                {
                    var innerPaint = _paintCache.GetPaint(currentState.CurrentNonStrokingColor,
                        currentState.AlphaConstantNonStroking, false, null, null, null, null, BlendMode.Normal);
                    var path = _currentPath;
                    DrawWithSoftMask(softMask!, currentState.BlendMode, () => _canvas.DrawPath(path, innerPaint));
                }
                else
                {
                    var paint = _paintCache.GetPaint(currentState.CurrentNonStrokingColor,
                        currentState.AlphaConstantNonStroking, false, null, null, null, null, currentState.BlendMode);
                    _canvas.DrawPath(_currentPath, paint);
                }
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
