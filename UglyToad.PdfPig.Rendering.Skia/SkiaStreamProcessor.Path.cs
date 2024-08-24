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
using System.Runtime.CompilerServices;
using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Rendering.Skia.Helpers;

namespace UglyToad.PdfPig.Rendering.Skia
{
    internal partial class SkiaStreamProcessor
    {
        private SKPath _currentPath;

        public override void BeginSubpath()
        {
            if (_currentPath == null)
            {
                _currentPath = new SKPath();
            }
        }

        public override PdfPoint? CloseSubpath()
        {
            if (_currentPath == null)
            {
                return null;
            }

            _currentPath.Close();
            return null;
        }

        public override void MoveTo(double x, double y)
        {
            BeginSubpath();

            if (_currentPath == null)
            {
                return;
            }

            var point = CurrentTransformationMatrix.Transform(x, y);
            float xs = (float)point.x;
            float ys = (float)(_height - point.y);

            _currentPath.MoveTo(xs, ys);
        }

        public override void LineTo(double x, double y)
        {
            if (_currentPath == null)
            {
                return;
            }

            var point = CurrentTransformationMatrix.Transform(x, y);
            float xs = (float)point.x;
            float ys = (float)(_height - point.y);

            _currentPath.LineTo(xs, ys);
        }

        public override void BezierCurveTo(double x2, double y2, double x3, double y3)
        {
            if (_currentPath == null)
            {
                return;
            }

            var (c2x, c2y) = CurrentTransformationMatrix.Transform(x2, y2);
            var (endx, endy) = CurrentTransformationMatrix.Transform(x3, y3);
            float x2s = (float)c2x;
            float y2s = (float)(_height - c2y);
            float x3s = (float)endx;
            float y3s = (float)(_height - endy);

            _currentPath.QuadTo(x2s, y2s, x3s, y3s);
        }

        public override void BezierCurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
        {
            if (_currentPath == null)
            {
                return;
            }

            var (c1x, c1y) = CurrentTransformationMatrix.Transform(x1, y1);
            var (c2x, c2y) = CurrentTransformationMatrix.Transform(x2, y2);
            var (endx, endy) = CurrentTransformationMatrix.Transform(x3, y3);
            float x1s = (float)c1x;
            float y1s = (float)(_height - c1y);
            float x2s = (float)c2x;
            float y2s = (float)(_height - c2y);
            float x3s = (float)endx;
            float y3s = (float)(_height - endy);

            _currentPath.CubicTo(x1s, y1s, x2s, y2s, x3s, y3s);
        }

        public override void ClosePath()
        {
            // No op
        }

        public override void EndPath()
        {
            if (_currentPath == null)
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

            var lowerLeft = CurrentTransformationMatrix.Transform(x, y);
            var upperRight = CurrentTransformationMatrix.Transform(x + width, y + height);

            float left = (float)lowerLeft.x;
            float top = (float)(this._height - upperRight.y);
            float right = (float)upperRight.x;
            float bottom = (float)(this._height - lowerLeft.y);
            _currentPath.AddRect(new SKRect(left, top, right, bottom));
        }

        public override void StrokePath(bool close)
        {
            if (_currentPath == null)
            {
                return;
            }

            if (close)
            {
                _currentPath.Close();
            }

            if (!_currentPath.IsEmpty)
            {
                var currentState = GetCurrentState();
                PaintStrokePath(_currentPath, currentState);
            }

            _currentPath.Dispose();
            _currentPath = null;
        }

        public override void FillPath(FillingRule fillingRule, bool close)
        {
            if (_currentPath == null)
            {
                return;
            }

            if (close)
            {
                _currentPath.Close();
            }

            if (!_currentPath.IsEmpty)
            {
                var currentState = GetCurrentState();
                PaintFillPath(_currentPath, currentState, fillingRule);
            }

            _currentPath.Dispose();
            _currentPath = null;
        }

        public override void FillStrokePath(FillingRule fillingRule, bool close)
        {
            if (_currentPath == null)
            {
                return;
            }

            if (close)
            {
                _currentPath.Close();
            }

            if (!_currentPath.IsEmpty)
            {
                var currentState = GetCurrentState();
                PaintFillPath(_currentPath, currentState, fillingRule);
                PaintStrokePath(_currentPath, currentState);
            }

            _currentPath.Dispose();
            _currentPath = null;
        }

        private void PaintStrokePath(SKPath path, CurrentGraphicsState currentState)
        {
            if (currentState.CurrentStrokingColor?.ColorSpace == ColorSpace.Pattern)
            {
                if (!(currentState.CurrentStrokingColor is PatternColor pattern))
                {
                    throw new ArgumentNullException(
                        $"Expecting a {nameof(PatternColor)} but got {currentState.CurrentStrokingColor.GetType()}");
                }

                switch (pattern.PatternType)
                {
                    case PatternType.Tiling:
                        RenderTilingPatternCurrentPath(path, pattern as TilingPatternColor, true);
                        break;

                    case PatternType.Shading:
                        RenderShadingPatternCurrentPath(path, pattern as ShadingPatternColor, true);
                        break;
                }
            }
            else
            {
                var paint = _paintCache.GetPaint(currentState.CurrentStrokingColor, currentState.AlphaConstantStroking,
                    true, (float)currentState.LineWidth, currentState.JoinStyle, currentState.CapStyle,
                    currentState.LineDashPattern, currentState.CurrentTransformationMatrix);

                DrawPath(_canvas, path, paint);
            }
        }

        private void PaintFillPath(SKPath path, CurrentGraphicsState currentState, FillingRule fillingRule)
        {
            if (path == null)
            {
                return;
            }

            path.FillType = fillingRule.ToSKPathFillType();

            if (currentState.CurrentNonStrokingColor?.ColorSpace == ColorSpace.Pattern)
            {
                if (!(currentState.CurrentNonStrokingColor is PatternColor pattern))
                {
                    throw new ArgumentNullException(
                        $"Expecting a {nameof(PatternColor)} but got {currentState.CurrentStrokingColor.GetType()}");
                }

                switch (pattern.PatternType)
                {
                    case PatternType.Tiling:
                        RenderTilingPatternCurrentPath(path, pattern as TilingPatternColor, false);
                        break;

                    case PatternType.Shading:
                        RenderShadingPatternCurrentPath(path, pattern as ShadingPatternColor, false);
                        break;
                }
            }
            else
            {
                var paint = _paintCache.GetPaint(currentState.CurrentNonStrokingColor,
                    currentState.AlphaConstantNonStroking, false, null, null, null, null, null);

                DrawPath(_canvas, path, paint);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DrawPath(SKCanvas canvas, SKPath path, SKPaint paint)
        {
            // https://groups.google.com/g/skia-discuss/c/Ko6JbkvN1fQ
            if (path.IsLine)
            {
                var line = path.GetLine();
                System.Diagnostics.Debug.Assert(line.Length == 2);
                canvas.DrawLine(line[0], line[1], paint);
            }
            else if (path.IsRect)
            {
                canvas.DrawRect(path.GetRect(), paint);
            }
            else if (path.IsRoundRect)
            {
                canvas.DrawRoundRect(path.GetRoundRect(), paint);
            }
            else if (path.IsOval)
            {
                canvas.DrawOval(path.GetOvalBounds(), paint);
            }
            else
            {
                canvas.DrawPath(path, paint);

                /*
                using (var measure = new SKPathMeasure(path))
                {
                    if (measure.IsClosed)
                    {
                        using (var simplified = path.Simplify())
                        {
                            canvas.DrawPath(simplified, paint);
                        }
                    }
                    else
                    {
                        canvas.DrawPath(path, paint);
                    }
                }
                */
            }
        }
    }
}
