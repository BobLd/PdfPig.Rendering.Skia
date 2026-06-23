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
    private void RenderRadialShading(RadialShading shading, in SKMatrix patternTransformMatrix,
        bool isStroke = false, SKPath? path = null)
    {
        var currentState = GetCurrentState();

        var coords = shading.Coords;

        float r0 = (float)coords[2];
        float r1 = (float)coords[5];

        // If one radius is 0, the corresponding circle shall be treated as a point;
        // if both are 0, nothing shall be painted.
        if (r0 == 0 && r1 == 0)
        {
            return;
        }

        var domain = shading.Domain;

        float x0 = (float)coords[0];
        float y0 = (float)coords[1];
        float x1 = (float)coords[3];
        float y1 = (float)coords[4];

        float t0 = (float)domain[0];
        float t1 = (float)domain[1];

        // Size the colour LUT to the radial sweep's on-screen extent (axis distance plus both
        // radii, in device pixels) — one texel per device pixel, as the old per-stop sampling did,
        // so step-function transitions stay pixel-sharp. See RenderAxialShading for the rationale.
        float radialExtent =
            MapToDevicePixels(patternTransformMatrix, x1 - x0, y1 - y0)
            + MapToDevicePixels(patternTransformMatrix, r0, 0)
            + MapToDevicePixels(patternTransformMatrix, r1, 0);
        int lutWidth = RampStopsForExtent(radialExtent);

        double alpha = currentState.AlphaConstantNonStroking;

        // PDF 1.7 §8.7.4.3: BBox is a temporary clipping boundary applied on top of the
        // current clipping path. For a Type 2 (shading) pattern it is given in pattern
        // space, so we push it through patternTransformMatrix to bring it into the canvas
        // input coordinate space (the same space the path is drawn in). For the direct
        // `sh` operator the matrix is identity and the BBox is already in user space.
        bool bboxClipPushed = false;
        if (shading.BBox.HasValue)
        {
            using var bboxPath = new SKPath();
            bboxPath.AddRect(shading.BBox.Value.ToSKRect());
            bboxPath.Transform(patternTransformMatrix);
            _canvas.Save();
            _canvas.ClipPath(bboxPath, SKClipOperation.Intersect, true);
            bboxClipPushed = true;
        }

        try
        {
            // PDF 1.7 §8.7.4.5.4: when Background is present, paint that colour over the
            // shading-object's painted area before drawing the gradient. Without this the
            // page shows through the bounded gradient (when Extend is false) instead of the
            // declared Background colour. Skipped when both Extend flags are true because the
            // gradient covers everything and the background would never be visible.
            bool[] extend = shading.Extend;
            if (shading.Background is not null && !(extend[0] && extend[1]))
            {
                using var bgPaint = new SKPaint();
                bgPaint.IsAntialias = shading.AntiAlias;
                bgPaint.Color = shading.ColorSpace.GetColor(shading.Background)
                    .ToSKColor(currentState.AlphaConstantNonStroking);
                bgPaint.BlendMode = currentState.BlendMode.ToSKBlendMode();

                if (path is null)
                {
                    _canvas.DrawPaint(bgPaint);
                }
                else
                {
                    _canvas.DrawPath(path, bgPaint);
                }
            }

            // PDF Extend controls whether the gradient continues past the start/end circles. The
            // SKSL radial shader honours each flag independently and solves the PDF circle-family
            // equation exactly. The old two-point conical gradient could only approximate this via a
            // single tile mode (Clamp when both extend, else Decal), with no exact counterpart for
            // mixed extends — that imperfection is now gone.
            using (SKImage lut = BuildShadingRampImage(shading, alpha, t0, t1, lutWidth, domain))
            using (var shader = CreateRadialShader(x0, y0, r0, x1, y1, r1, extend[0], extend[1], lutWidth, lut, patternTransformMatrix))
            using (var paint = new SKPaint())
            {
                paint.IsAntialias = shading.AntiAlias;
                paint.Shader = shader;
                paint.BlendMode = currentState.BlendMode.ToSKBlendMode();

                SKPathEffect? dash = null;
                if (isStroke)
                {
                    // TODO - To finish
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
        finally
        {
            if (bboxClipPushed)
            {
                _canvas.Restore();
            }
        }
    }
}
