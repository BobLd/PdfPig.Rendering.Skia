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

        // Size the colour LUT to the gradient's on-screen extent — one texel per device pixel —
        // mapping the axis vector (x0,y0)->(x1,y1) from shading space into device pixels. This
        // matches the resolution the old per-stop sampling used so a hard step-discontinuity
        // (e.g. a Type 3 stitching function at one of its Bounds) stays within a pixel rather than
        // smearing across the sweep. The SKSL axial shader then computes the parametric position
        // exactly per pixel (no stop quantization) and honours the Extend flags directly — the old
        // CreateLinearGradient path always clamped (i.e. behaved as Extend = [true, true]).
        //
        // NOTE: the common sub-case of a single Type 2 (exponential) function in a device RGB / Gray
        // space is C0 + t^N·(C1−C0) and could be evaluated closed-form directly in SkSL, skipping the
        // LUT bitmap / texture entirely. Left on the generic LUT path for now (one code path, exact
        // match with Shading.Eval for every function / colour space).
        float axisLength = MapToDevicePixels(patternTransformMatrix, x1 - x0, y1 - y0);
        int lutWidth = RampStopsForExtent(axisLength);

        double alpha = currentState.AlphaConstantNonStroking;
        bool[] extend = shading.Extend;

        using (SKImage lut = BuildShadingRampImage(shading, alpha, t0, t1, lutWidth, domain))
        using (SKShader shader = CreateAxialShader(x0, y0, x1, y1, extend[0], extend[1], lutWidth, lut, patternTransformMatrix))
        using (SKPaint paint = new SKPaint())
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
