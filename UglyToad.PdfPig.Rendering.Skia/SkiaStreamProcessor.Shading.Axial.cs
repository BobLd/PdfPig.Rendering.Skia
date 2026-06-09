// Copyright BobLd
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
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Rendering.Skia.Helpers;

namespace UglyToad.PdfPig.Rendering.Skia;

internal partial class SkiaStreamProcessor
{
    private void RenderAxialShading(AxialShading shading, in SKMatrix patternTransformMatrix,
     bool isStroke = false, SKPath? path = null)
    {
        var currentState = GetCurrentState();

        var coords = shading.Coords;
        var domain = shading.Domain;

        float x0 = (float)coords[0];
        float y0 = (float)coords[1];
        float x1 = (float)coords[2];
        float y1 = (float)coords[3];

        float t0 = (float)domain[0];
        float t1 = (float)domain[1];

        if (shading.BBox.HasValue)
        {
            // TODO
        }

        if (shading.Background is not null)
        {
            // TODO
        }

        // Number of stops to sample from the colour function. The gradient line goes from
        // (x0, y0) to (x1, y1) in shading space — map that vector into device pixels and
        // aim for one stop per device pixel. Lower densities work for smooth functions
        // but smear any hard step-discontinuity (e.g. a Type 3 stitching function at one
        // of its Bounds) across many pixels: the gap between adjacent stops becomes the
        // width of the transition. One-per-pixel keeps the gap below the antialiasing
        // edge so steps render as cleanly as a clipped fill. Floored at 10 for degenerate
        // (zero-length) axes.
        float axisLength = MapToDevicePixels(patternTransformMatrix, x1 - x0, y1 - y0);
        int factor = Math.Max(10, (int)Math.Ceiling(axisLength));
        var colors = new SKColor[factor + 1];
        float[] colorPos = new float[factor + 1];

        Span<double> evalIn = stackalloc double[1];
        Span<double> evalOut = stackalloc double[ShadingEvalBufferSize];
        double alpha = currentState.AlphaConstantNonStroking;
        ColorSpaceDetails axialColorSpace = shading.ColorSpace;
        for (int t = 0; t <= factor; t++)
        {
            // Sample the parametric variable across the user-supplied Domain (NOT 0..t1
            // — the previous form silently broke whenever t0 ≠ 0).
            double frac = t / (double)factor;
            double tx = t0 + frac * (t1 - t0);
            evalIn[0] = tx;
            int written = shading.Eval(evalIn, evalOut);
            Span<double> v = evalOut.Slice(0, written);

            FixIncorrectValues(v, domain); // This is a hack, this should never happen, see GHOSTSCRIPT-693154-0

            colors[t] = axialColorSpace.GetSKColor(v, alpha); // TODO - is it non stroking??
            // Skia expects colorPos in [0,1] along the gradient line. The previous form
            // passed raw domain values (e.g. 0..161) which Skia then clamped to [0,1],
            // collapsing every intermediate stop onto position 1 and rendering the gradient
            // as a single colour (the most visible casualty was P.pdf's bottom-right
            // colour-bar legend).
            colorPos[t] = (float)frac;
        }

        using (var shader = SKShader.CreateLinearGradient(new SKPoint(x0, y0), new SKPoint(x1, y1), colors, colorPos, SKShaderTileMode.Clamp, patternTransformMatrix))
        using (var paint = new SKPaint())
        {
            paint.IsAntialias = shading.AntiAlias;
            paint.Shader = shader;
            paint.BlendMode = currentState.BlendMode.ToSKBlendMode();

            // check if bbox not null

            SKPathEffect? dash = null;
            if (isStroke)
            {
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = (float)currentState.LineWidth;
                paint.StrokeJoin = currentState.JoinStyle.ToSKStrokeJoin();
                paint.StrokeCap = currentState.CapStyle.ToSKStrokeCap();
                dash = currentState.LineDashPattern.ToSKPathEffect();
                paint.PathEffect = dash;
            }

            if (path is null)
            {
                _canvas.DrawPaint(paint);
            }
            else
            {
                _canvas.DrawPath(path, paint);
            }

            dash?.Dispose();
        }
    }
}
