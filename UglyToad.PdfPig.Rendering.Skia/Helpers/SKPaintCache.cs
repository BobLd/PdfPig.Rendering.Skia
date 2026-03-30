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
using System.Collections.Generic;
using SkiaSharp;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Graphics.Core;

namespace UglyToad.PdfPig.Rendering.Skia.Helpers
{
    internal sealed class SKPaintCache : IDisposable
    {
        private readonly bool _isAntialias;

        private readonly Dictionary<int, SKPaint> _cache = new();
        private readonly Dictionary<(bool, BlendMode), SKPaint> _imagePaintCache = new();

#if DEBUG
        private readonly SKPaint _imageDebugPaint;
#endif

        public SKPaintCache(bool isAntialias, float minimumLineWidth)
        {
            _isAntialias = isAntialias;
            // minimumLineWidth not in use

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
            LineCapStyle? capStyle, LineDashPattern? dashPattern, BlendMode blendMode)
        {
            return HashCode.Combine(color, alpha, stroke, strokeWidth, joinStyle, capStyle, GetHash(dashPattern), blendMode);
        }

        public SKPaint GetPaint(IColor? color, double alpha, bool stroke, float? strokeWidth, LineJoinStyle? joinStyle,
            LineCapStyle? capStyle, LineDashPattern? dashPattern, BlendMode blendMode)
        {
            color ??= RGBColor.Black;
            var key = GetPaintKey(color, alpha, stroke, strokeWidth, joinStyle, capStyle, dashPattern, blendMode);

            if (_cache.TryGetValue(key, out var paint))
            {
                return paint;
            }

            paint = new SKPaint()
            {
                IsAntialias = _isAntialias,
                Color = color.ToSKColor(alpha),
                Style = stroke ? SKPaintStyle.Stroke : SKPaintStyle.Fill,
                BlendMode = blendMode.ToSKBlendMode()
            };
            
            if (stroke)
            {
                // Careful - we assume they all have values if stroke!
                paint.StrokeWidth = strokeWidth.Value;
                paint.StrokeJoin = joinStyle.Value.ToSKStrokeJoin();
                paint.StrokeCap = capStyle.Value.ToSKStrokeCap();
                paint.PathEffect = dashPattern.Value.ToSKPathEffect();
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

        public SKPaint GetPaint(IPdfImage pdfImage, BlendMode blendMode)
        {
            // For non-Normal blend modes, use general cache with ValueTuple key
            var key = (pdfImage.Interpolate, blendMode);

            if (_imagePaintCache.TryGetValue(key, out var paint))
            {
                return paint;
            }

            paint = new SKPaint
            {
                IsAntialias = pdfImage.Interpolate,
                BlendMode = blendMode.ToSKBlendMode()
            };
            
            _imagePaintCache[key] = paint;

            return paint;
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
            foreach (var pair in _cache)
            {
                pair.Value.PathEffect?.Dispose();
                pair.Value.Dispose();
            }
            _cache.Clear();

            foreach (var pair in _imagePaintCache)
            {
                pair.Value.Dispose();
            }
            _imagePaintCache.Clear();
        }
    }
}
