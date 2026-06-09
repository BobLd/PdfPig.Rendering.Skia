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
    /// <summary>
    /// see PDFBOX-1869-4.pdf
    /// </summary>
    private void RenderFunctionBasedShading(FunctionBasedShading shading, in SKMatrix patternTransformMatrix,
        bool isStroke = false, SKPath? path = null)
    {
        // Based on https://github.com/apache/pdfbox/blob/trunk/pdfbox/src/main/java/org/apache/pdfbox/pdmodel/graphics/shading/Type1ShadingContext.java
        // Strategy: pre-rasterise the colour function into a bitmap that covers the
        // Domain rectangle, then let Skia map it onto the page through the shading.Matrix
        // (and the pattern transform) with bilinear filtering. The previous form sized
        // the bitmap to (int)(x1-x0) × (int)(y1-y0), which collapsed to 2×2 for the typical
        // [-1,1]×[-1,1] domain — far too few samples for a non-trivial Type 4 function.

        var domain = shading.Domain;

        double x0 = domain[0];
        double x1 = domain[1];
        double y0 = domain[2];
        double y1 = domain[3];

        double xExtent = x1 - x0;
        double yExtent = y1 - y0;

        if (xExtent <= 0 || yExtent <= 0)
        {
            return;
        }

        // Size the rasterised bitmap to roughly match the device-space footprint of the
        // rendered Domain rectangle. A fixed texture aliased rapidly oscillating Type 4
        // functions when the device footprint exceeded it, and over-allocated when the
        // shading was tiny. We compose the full chain (canvas CTM × pattern × shading.Matrix)
        // so the size reflects the final on-screen pixel count, then clamp to a reasonable
        // band so memory and rasterisation cost stay bounded for unusual transforms.
        var domainRect = new SKRect((float)x0, (float)y0, (float)x1, (float)y1);
        var deviceMatrix = _canvas.TotalMatrix
            .PreConcat(patternTransformMatrix)
            .PreConcat(shading.Matrix.ToSkMatrix());
        var deviceRect = deviceMatrix.MapRect(domainRect);
        int w = Math.Max(64, Math.Min(2048, (int)Math.Ceiling(Math.Abs(deviceRect.Width))));
        int h = Math.Max(64, Math.Min(2048, (int)Math.Ceiling(Math.Abs(deviceRect.Height))));

        using SKBitmap shaderBitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        var raster = shaderBitmap.GetPixelSpan();

        ColorSpaceDetails? shadingColorSpace = shading.ColorSpace;
        // Per-pixel scratch: input is (x, y), output is the remapped function result.
        // Both live on the stack so the up-to-2048² inner loop never touches the heap
        // for Eval; GetRgb's span overload also avoids the per-pixel IColor allocation.
        Span<double> values = stackalloc double[2];
        Span<double> evalOut = stackalloc double[ShadingEvalBufferSize];

        // Hoist the per-pixel divisions out of the loop: each pixel walks the Domain in
        // linear steps, so xi = xBase + i*xStep and yi = yBase + j*yStep is exactly the
        // same texel-centre sample without a `/ w` or `/ h` in the inner body.
        double xStep = xExtent / w;
        double yStep = yExtent / h;
        double xBase = x0 + 0.5 * xStep;
        double yBase = y0 + 0.5 * yStep;
        int rowStride = w * 4;

        for (int j = 0; j < h; j++)
        {
            double yi = yBase + j * yStep;
            int rowOffset = j * rowStride;
            for (int i = 0; i < w; i++)
            {
                values[0] = xBase + i * xStep;
                values[1] = yi;

                int index = rowOffset + i * 4;

                int written;
                try
                {
                    written = shading.EvalWithRangeRemap(values, evalOut);
                }
                catch (Exception e)
                {
                    System.Diagnostics.Debug.WriteLine("error while processing a function {0}", e);
                    continue;
                }

                ReadOnlySpan<double> components = evalOut.Slice(0, written);
                double rOut, gOut, bOut;
                if (shadingColorSpace is not null)
                {
                    try
                    {
                        shadingColorSpace.GetRgb(components, out rOut, out gOut, out bOut);
                    }
                    catch (Exception e)
                    {
                        // PDF could be malformed: function output count may not match the
                        // colour space's component count (e.g. one 1-out function for a
                        // 3-component DeviceRGB space). Skip the pixel rather than crash.
                        System.Diagnostics.Debug.WriteLine("function/color-space component mismatch {0}", e);
                        continue;
                    }
                }
                else
                {
                    rOut = components.Length > 0 ? components[0] : 0;
                    gOut = components.Length > 1 ? components[1] : rOut;
                    bOut = components.Length > 2 ? components[2] : rOut;
                }

                raster[index] = (rOut * 255.0).ToByte();
                raster[index + 1] = (gOut * 255.0).ToByte();
                raster[index + 2] = (bOut * 255.0).ToByte();
                raster[index + 3] = 255;
            }
        }

        // Compose the localMatrix that Skia uses to sample the bitmap:
        //   bitmap pixel (i,j)
        //     ──[bitmap→domain]──▶ (x0+i·xExtent/w,  y0+j·yExtent/h)
        //     ──[shading.Matrix]──▶ shading target coords
        //     ──[patternTransform]─▶ canvas-input coords (where the path lives)
        // PreConcat builds the matrix product so the rightmost transform runs first.
        var bitmapToDomain = SKMatrix
            .CreateScale((float)(xExtent / w), (float)(yExtent / h))
            .PostConcat(SKMatrix.CreateTranslation((float)x0, (float)y0));

        var finalShadingMatrix = patternTransformMatrix
            .PreConcat(shading.Matrix.ToSkMatrix())
            .PreConcat(bitmapToDomain);

        var currentState = GetCurrentState();

        // PDF 1.7 §8.7.4.3: the shading's BBox is a temporary clip in the shading's target
        // coordinate space. patternTransformMatrix already maps that space into canvas
        // input coords (identity for the direct `sh` operator).
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
            // PDF 1.7 §8.7.4.5.4: paint the Background colour over the area first so that
            // pixels outside the rasterised Domain rectangle (Decal-clipped to transparent)
            // fall through to the declared Background instead of the page beneath.
            if (shading.Background is not null && shadingColorSpace is not null)
            {
                using var bgPaint = new SKPaint();
                bgPaint.IsAntialias = shading.AntiAlias;
                bgPaint.Color = shadingColorSpace.GetColor(shading.Background)
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

            using (var shader = SKShader.CreateBitmap(shaderBitmap, SKShaderTileMode.Decal, SKShaderTileMode.Decal, finalShadingMatrix))
            using (var paint = new SKPaint())
            {
                paint.IsAntialias = shading.AntiAlias;
                paint.Shader = shader;
                paint.BlendMode = currentState.BlendMode.ToSKBlendMode();

                SKPathEffect? dash = null;
                if (isStroke)
                {
                    // TODO - To Check
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