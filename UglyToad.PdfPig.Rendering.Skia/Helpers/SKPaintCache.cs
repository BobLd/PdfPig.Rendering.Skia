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
using System.Collections.Generic;
using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Graphics.Core;

namespace UglyToad.PdfPig.Rendering.Skia.Helpers
{
    internal sealed class SKPaintCache : IDisposable
    {
        private readonly bool _isAntialias;

        private readonly Dictionary<int, SKPaint> _cache = new Dictionary<int, SKPaint>();

        private readonly SKPaint _antialiasingPaint;

#if DEBUG
        private readonly SKPaint _imageDebugPaint;
#endif

        public SKPaintCache(bool isAntialias, float minimumLineWidth)
        {
            _isAntialias = isAntialias;
            // minimumLineWidth not in use
            _antialiasingPaint = new SKPaint()
            {
                IsAntialias = _isAntialias,
                FilterQuality = SKFilterQuality.High,
                SubpixelText = true
            };
#if DEBUG
            _imageDebugPaint = new SKPaint()
            {
                Style = SKPaintStyle.StrokeAndFill,
                Color = new SKColor(SKColors.IndianRed.Red, SKColors.IndianRed.Green, SKColors.IndianRed.Blue, 150),
                IsAntialias = _isAntialias,
                StrokeWidth = 2
            };
#endif
        }

        private static int GetPaintKey(IColor color, double alpha, bool stroke, float? strokeWidth, LineJoinStyle? joinStyle,
            LineCapStyle? capStyle, LineDashPattern? dashPattern)
        {
            return HashCode.Combine(color, alpha, stroke, strokeWidth, joinStyle, capStyle, GetHash(dashPattern));
        }

        public SKPaint GetPaint(IColor color, double alpha, bool stroke, float? strokeWidth, LineJoinStyle? joinStyle,
            LineCapStyle? capStyle, LineDashPattern? dashPattern, TransformationMatrix? matrix)
        {
            var key = GetPaintKey(color, alpha, stroke, strokeWidth, joinStyle, capStyle, dashPattern);

            if (_cache.TryGetValue(key, out var paint))
            {
                return paint;
            }

            paint = new SKPaint()
            {
                IsAntialias = _isAntialias,
                Color = color.ToSKColor(alpha),
                Style = stroke ? SKPaintStyle.Stroke : SKPaintStyle.Fill,
                FilterQuality = SKFilterQuality.High,
                SubpixelText = true
            };
            
            if (stroke)
            {
                float scalingFactor = matrix.Value.GetScalingFactor();
                
                // Careful - we assume they all have values if stroke!
                paint.StrokeWidth = strokeWidth.Value * scalingFactor;
                paint.StrokeJoin = joinStyle.Value.ToSKStrokeJoin();
                paint.StrokeCap = capStyle.Value.ToSKStrokeCap();
                paint.PathEffect = dashPattern.Value.ToSKPathEffect(scalingFactor);
            }

            _cache[key] = paint;

            return paint;
        }

        private static int GetHash(LineDashPattern? dashPattern)
        {
            if (!dashPattern.HasValue)
            {
                return 0;
            }

            int key = dashPattern.Value.Phase;
            for (int n = 0; n < dashPattern.Value.Array.Count; n++)
            {
                key = (key * 31) ^ dashPattern.Value.Array[n].GetHashCode();
            }
            return key;
        }

        public SKPaint GetAntialiasing()
        {
            return _antialiasingPaint;
        }

#if DEBUG
        public SKPaint GetImageDebug()
        {
            return _imageDebugPaint;
        }
#endif

        public void Dispose()
        {
#if DEBUG
            _imageDebugPaint.Dispose();
#endif
            _antialiasingPaint.Dispose();

            foreach (var pair in _cache)
            {
                pair.Value.PathEffect?.Dispose();
                pair.Value.Dispose();
            }
            _cache.Clear();
        }
    }
}
