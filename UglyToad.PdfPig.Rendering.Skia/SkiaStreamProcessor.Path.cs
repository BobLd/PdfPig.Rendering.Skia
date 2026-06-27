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

            if (!TryPaintAtomicFillStroke(currentState, fillingRule))
            {
                PaintFillPath(currentState, fillingRule);
                PaintStrokePath(currentState);
            }

            _currentPath.Dispose();
            _currentPath = null;
        }

        /// <summary>
        /// Paint a path that is both filled and stroked by a single operator (<c>B</c>, <c>B*</c>,
        /// <c>b</c>, <c>b*</c>) as one atomic object when transparency is involved.
        /// <para>
        /// ISO 32000-2 11.7.4.4: "For the purpose of compositing with the backdrop, the filling and
        /// stroking operations performed by a single path-painting operator shall be treated as if
        /// they were a single graphic object", i.e. a knockout transparency group. Painting fill then
        /// stroke separately instead composites a semi-transparent stroke twice where it overlaps the
        /// fill, so the stroke shows a different colour over the fill than over the bare backdrop
        /// (visible on self-intersecting paths). Rendering both into one isolated layer and giving the
        /// stroke <see cref="SKBlendMode.Src"/> makes the stroke knock the fill out within the group,
        /// so it keeps a single uniform colour everywhere.
        /// </para>
        /// Returns <see langword="false"/> (leaving the caller to paint sequentially) for the opaque
        /// case, where the group is unnecessary, and for pattern colours / active soft masks, which
        /// keep their existing handling.
        /// </summary>
        private bool TryPaintAtomicFillStroke(CurrentGraphicsState currentState, FillingRule fillingRule)
        {
            if (_currentPath is null)
            {
                return false;
            }

            if (currentState.CurrentNonStrokingColor?.ColorSpace == ColorSpace.Pattern ||
                currentState.CurrentStrokingColor?.ColorSpace == ColorSpace.Pattern)
            {
                return false;
            }

            if (TryGetActiveSoftMask(out _))
            {
                return false;
            }

            bool hasTransparency = currentState.AlphaConstantNonStroking < 1.0 ||
                                   currentState.AlphaConstantStroking < 1.0 ||
                                   currentState.BlendMode != BlendMode.Normal;
            if (!hasTransparency)
            {
                // Opaque + Normal blend: the stroke fully hides the fill it covers, so sequential
                // fill-then-stroke is identical to the knockout group but cheaper (no layer).
                return false;
            }

            _currentPath.FillType = fillingRule.ToSKPathFillType();

            // The gs blend mode is applied once, when the group layer is composited back onto the
            // backdrop; the fill and stroke draw with Normal blend inside the (isolated) layer.
            using var groupPaint = new SKPaint { BlendMode = currentState.BlendMode.ToSKBlendMode() };
            _canvas.SaveLayer(groupPaint);
            try
            {
                var fillPaint = _paintCache.GetPaint(currentState.CurrentNonStrokingColor,
                    currentState.AlphaConstantNonStroking, false, null, null, null, null, BlendMode.Normal);
                _canvas.DrawPath(_currentPath, fillPaint);

                // SKBlendMode.Src: within the layer the stroke replaces (knocks out) the fill it
                // covers instead of compositing over it, giving the stroke a uniform colour.
                var knockoutStroke = _paintCache.GetPaint(currentState.CurrentStrokingColor,
                    currentState.AlphaConstantStroking, true, (float)currentState.LineWidth,
                    currentState.JoinStyle, currentState.CapStyle, currentState.LineDashPattern,
                    BlendMode.Normal, SKBlendMode.Src);
                _canvas.DrawPath(_currentPath, knockoutStroke);
            }
            finally
            {
                _canvas.Restore();
            }

            return true;
        }
    }
}
