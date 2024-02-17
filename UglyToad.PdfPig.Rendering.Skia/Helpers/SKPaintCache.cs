/*
 * Copyright 2024 BobLd
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *    http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

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

        private readonly float _minimumLinwWidth;

        private readonly Dictionary<int, SKPaint> _cache = new Dictionary<int, SKPaint>();

        private readonly SKPaint _antialiasingPaint;

#if DEBUG
        private readonly SKPaint _imageDebugPaint;
#endif

        public SKPaintCache(bool isAntialias, float minimumLinwWidth)
        {
            _isAntialias = isAntialias;
            _minimumLinwWidth = minimumLinwWidth;
            _antialiasingPaint = new SKPaint() { IsAntialias = _isAntialias };
#if DEBUG
            _imageDebugPaint = new SKPaint()
            {
                Style = SKPaintStyle.Fill,
                Color = new SKColor(SKColors.IndianRed.Red, SKColors.IndianRed.Green, SKColors.IndianRed.Blue, 150),
                IsAntialias = _isAntialias
            };
#endif
        }

        private static int GetPaintKey(IColor color, double alpha, bool stroke, float? strokeWidth, LineJoinStyle? joinStyle,
            LineCapStyle? capStyle, LineDashPattern? dashPattern)
        {
            // https://stackoverflow.com/questions/263400/what-is-the-best-algorithm-for-overriding-gethashcode?lq=1
            // https://thomaslevesque.com/2020/05/15/things-every-csharp-developer-should-know-1-hash-codes/
            unchecked
            {
                // 1430287 amd 7302013 are prime numbers
                int hashcode = 1430287;
                hashcode = hashcode * 7302013 ^ (color?.GetHashCode() ?? 0);
                hashcode = hashcode * 7302013 ^ alpha.GetHashCode();
                hashcode = hashcode * 7302013 ^ stroke.GetHashCode();
                hashcode = hashcode * 7302013 ^ (strokeWidth?.GetHashCode() ?? 0);
                hashcode = hashcode * 7302013 ^ (joinStyle?.GetHashCode() ?? 0);
                hashcode = hashcode * 7302013 ^ (capStyle?.GetHashCode() ?? 0);
                hashcode = hashcode * 7302013 ^ getHash(dashPattern);
                return hashcode;
            }
            //return HashCode.Combine(...)
        }

        public SKPaint GetPaint(IColor color, decimal alpha, bool stroke, float? strokeWidth, LineJoinStyle? joinStyle,
            LineCapStyle? capStyle, LineDashPattern? dashPattern, TransformationMatrix? matrix)
        {
            return GetPaint(color, (double)alpha, stroke, strokeWidth, joinStyle, capStyle, dashPattern, matrix);
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
                Style = stroke ? SKPaintStyle.Stroke : SKPaintStyle.Fill
            };

            if (stroke)
            {
                // Careful - we assume they all have values if stroke!
                float scaledWidth = (float)(strokeWidth.Value * matrix.Value.A); // A guess
                paint.StrokeWidth = Math.Max(_minimumLinwWidth, scaledWidth);
                paint.StrokeJoin = joinStyle.Value.ToSKStrokeJoin();
                paint.StrokeCap = capStyle.Value.ToSKStrokeCap();
                paint.PathEffect = dashPattern.Value.ToSKPathEffect(strokeWidth.Value);
            }

            _cache[key] = paint;

            return paint;
        }

        private static int getHash(LineDashPattern? dashPattern)
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
