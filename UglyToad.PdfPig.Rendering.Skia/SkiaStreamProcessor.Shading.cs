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
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using SkiaSharp;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Rendering.Skia.Helpers;
using UglyToad.PdfPig.Tokens;

namespace UglyToad.PdfPig.Rendering.Skia
{
    internal partial class SkiaStreamProcessor
    {
        /// <inheritdoc/>
        public override void PaintShading(NameToken shadingNameToken)
        {
            Shading shading = ResourceStore.GetShading(shadingNameToken);

            switch (shading.ShadingType)
            {
                case ShadingType.Axial:
                    RenderAxialShading(shading as AxialShading, in SKMatrix.Identity);
                    break;

                case ShadingType.Radial:
                    RenderRadialShading(shading as RadialShading, in SKMatrix.Identity);
                    break;

                case ShadingType.FunctionBased:
                    RenderFunctionBasedShading(shading as FunctionBasedShading, in SKMatrix.Identity);
                    break;

                case ShadingType.FreeFormGouraud:
                    RenderFreeFormGouraudShading(shading as FreeFormGouraudShading, in SKMatrix.Identity);
                    break;

                case ShadingType.LatticeFormGouraud:
                    RenderLatticeFormGouraudShading(shading as LatticeFormGouraudShading, in SKMatrix.Identity);
                    break;

                case ShadingType.CoonsPatch:
                    RenderCoonsPatchShading(shading as CoonsPatchMeshesShading, in SKMatrix.Identity);
                    break;

                case ShadingType.TensorProductPatch:
                    RenderTensorProductPatchShading(shading as TensorProductPatchMeshesShading, in SKMatrix.Identity);
                    break;
            }
        }

        /// <summary>
        /// This is very hackish, should never happen.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void FixIncorrectValues(Span<double> v, ReadOnlySpan<double> domain)
        {
            double fallback = domain[0];
            for (int i = 0; i < v.Length; i++)
            {
                ref double c = ref v[i];
                if (double.IsNaN(c) || double.IsInfinity(c))
                {
                    c = fallback;
                }
            }
        }

        /// <summary>
        /// Maps a vector from shading/pattern space into device pixels and returns its length.
        /// The full chain (canvas CTM × pattern transform) is composed so the result reflects
        /// the gradient's actual on-screen extent rather than the unit space the coords live in.
        /// Used to size the gradient colour-stop table for axial / radial shadings.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float MapToDevicePixels(in SKMatrix patternTransformMatrix, float dx, float dy)
        {
            SKMatrix toDevice = _canvas.TotalMatrix.PreConcat(patternTransformMatrix);
            float mappedDx = toDevice.ScaleX * dx + toDevice.SkewX * dy;
            float mappedDy = toDevice.SkewY * dx + toDevice.ScaleY * dy;
            return (float)Math.Sqrt(mappedDx * mappedDx + mappedDy * mappedDy);
        }

        /// <summary>
        /// Apply an affine matrix to a point without going through the P/Invoke
        /// <see cref="SKMatrix.MapPoint(float,float)"/>. Safe because every matrix we feed
        /// the shading pipeline (CTM, pattern transform, shading.Matrix) is constructed from
        /// PDF 2D transforms that have no perspective row.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SKPoint MapPointAffine(in SKMatrix m, float x, float y)
        {
            return new SKPoint(
                m.ScaleX * x + m.SkewX * y + m.TransX,
                m.SkewY * x + m.ScaleY * y + m.TransY);
        }

        /// <summary>
        /// Stack buffer size used for shading-Eval outputs in the hot loops. 32 doubles
        /// covers DeviceRGB / DeviceGray / DeviceCMYK / DeviceN cases without touching the heap.
        /// <para>Pathological wider colour spaces would need a heap fallback.</para>
        /// </summary>
        private const int ShadingEvalBufferSize = 32;
        
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

            float radialExtent =
                MapToDevicePixels(patternTransformMatrix, x1 - x0, y1 - y0)
                + MapToDevicePixels(patternTransformMatrix, r0, 0)
                + MapToDevicePixels(patternTransformMatrix, r1, 0);
            int factor = Math.Max(10, (int)Math.Ceiling(radialExtent));
            var colors = new SKColor[factor + 1];
            float[] colorPos = new float[factor + 1];

            Span<double> evalIn = stackalloc double[1];
            Span<double> evalOut = stackalloc double[ShadingEvalBufferSize];
            double alpha = currentState.AlphaConstantNonStroking;
            ColorSpaceDetails radialColorSpace = shading.ColorSpace;
            for (int t = 0; t <= factor; t++)
            {
                // See RenderAxialShading for the rationale of these two computations:
                //   - tx walks the user-supplied Domain (correct when t0 ≠ 0)
                //   - colorPos must be in [0,1] for Skia's gradient shader
                double frac = t / (double)factor;
                double tx = t0 + frac * (t1 - t0);
                evalIn[0] = tx;
                int written = shading.Eval(evalIn, evalOut);
                Span<double> v = evalOut.Slice(0, written);

                FixIncorrectValues(v, domain); // This is a hack, this should never happen

                colors[t] = radialColorSpace.GetSKColor(v, alpha);
                // TODO - is it non stroking??
                colorPos[t] = (float)frac;
            }

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

                // PDF Extend controls whether the gradient continues past the start/end circles.
                // Skia's tile mode on a two-point conical gradient is the closest equivalent:
                //   Both true   → Clamp  (t=0/t=1 colours bleed to infinity)
                //   Both false  → Decal  (areas outside the gradient are transparent)
                // Mixed extends have no exact tile-mode counterpart; Decal keeps at least the
                // non-extending side correct, and the extending side is rare enough in practice
                // that we accept the imperfection rather than rasterising by hand.
                SKShaderTileMode tileMode = (extend[0] && extend[1])
                    ? SKShaderTileMode.Clamp
                    : SKShaderTileMode.Decal;

                using (var shader = SKShader.CreateTwoPointConicalGradient(new SKPoint(x0, y0), r0, new SKPoint(x1, y1),
                           r1, colors, colorPos, tileMode, patternTransformMatrix))
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

        /// <summary>
        /// Renders a Type 4 Free-Form Gouraud-Shaded Triangle Mesh.
        /// <para>
        /// Parses vertex records from the shading stream (each record: BitsPerFlag flag bits,
        /// BitsPerCoordinate x bits, BitsPerCoordinate y bits, BitsPerComponent × n colour bits,
        /// padded to a byte boundary), decodes coordinates and colours using the Decode array,
        /// builds triangles according to the edge-flag rules (0 = new free triangle; 1 = share
        /// edge v[−2]–v[−1]; 2 = share edge v[−3]–v[−1]), and draws them via
        /// <see cref="SKCanvas.DrawVertices"/> for hardware-accelerated Gouraud interpolation.
        /// </para>
        /// Based on https://github.com/apache/pdfbox/blob/trunk/pdfbox/src/main/java/org/apache/pdfbox/pdmodel/graphics/shading/GouraudShadingContext.java
        /// and https://github.com/apache/pdfbox/blob/trunk/pdfbox/src/main/java/org/apache/pdfbox/pdmodel/graphics/shading/TriangleBasedShadingContext.java
        /// </summary>
        private void RenderFreeFormGouraudShading(FreeFormGouraudShading shading, in SKMatrix patternTransformMatrix,
            bool isStroke = false, SKPath? path = null)
        {
            if (shading.Data.IsEmpty)
            {
                return;
            }

            var currentState = GetCurrentState();

            int bitsPerFlag = shading.BitsPerFlag;
            int bitsPerCoordinate = shading.BitsPerCoordinate;
            int bitsPerComponent = shading.BitsPerComponent;
            var decode = shading.Decode;

            // Number of colour components encoded per vertex in the stream:
            // 1 if a Function is present (parametric variable), otherwise n = colour-space components.
            // Derived from the Decode array: [xmin xmax ymin ymax c1min c1max … cnmin cnmax]
            int numStreamColorComponents = (decode.Length - 4) / 2;

            double maxCoordRaw = (1L << bitsPerCoordinate) - 1.0;
            double maxColorRaw = (1L << bitsPerComponent) - 1.0;

            double xMin = decode[0], xMax = decode[1];
            double yMin = decode[2], yMax = decode[3];

            // When a non-trivial Function is present, the per-pixel colour is non-linear in the
            // parametric variable, so linear vertex interpolation by SkiaSharp would miss the
            // intermediate function output (rainbow gradients via stitched Type-3 functions,
            // or step gradients via Type-2 N=0 sub-functions). Defer the function eval and
            // subdivide each triangle when emitting so the per-sub-vertex colour captures the
            // function's non-linear behaviour.
            bool hasFunction = shading.Functions is { Length: > 0 };

            // Output buffer for batched DrawVertices calls. In the function path each parent
            // triangle subdivides into n² sub-triangles and the buffer is sized to fit exactly
            // one parent's output, so EmitGouraudTriangle flushes after every emit. The
            // no-function path packs 3-vertex triangles into the buffer and flushes when full
            // or when the patch loop ends. Sub-vertex scratch and the bilinear scratch buffer
            // are likewise hoisted out of the per-emit hot path.
            const int functionPerParentVerts = GouraudFunctionSubdivisions * GouraudFunctionSubdivisions * 3;
            const int gouraudNoFunctionBatchVerts = 4096 * 3;
            int outCapacity = hasFunction ? functionPerParentVerts : gouraudNoFunctionBatchVerts;
            var outPositions = new SKPoint[outCapacity];
            var outColors = new SKColor[outCapacity];
            int outCount = 0;

            // Barycentric sub-vertex grid for the function path; reused across all parent
            // triangles in this Render call so we don't pay the (n+1)(n+2)/2 allocation per
            // emit.
            int subVertCount = (GouraudFunctionSubdivisions + 1) * (GouraudFunctionSubdivisions + 2) / 2;
            SKPoint[]? subPts = hasFunction ? new SKPoint[subVertCount] : null;
            SKColor[]? subCols = hasFunction ? new SKColor[subVertCount] : null;

            // Reusable scratch buffer for the barycentric component blend inside
            // EmitGouraudTriangle's subdivision loop.
            double[] emitBuffer = hasFunction ? new double[numStreamColorComponents] : Array.Empty<double>();

            // No-function fast path doesn't need per-vertex raw components — only the
            // pre-evaluated SKColor is consulted, so we read every vertex into the same
            // scratch buffer and pass Array.Empty<double>() to GouraudVertex.
            // Function path stores the raw components on each vertex so the subdivision
            // loop can blend them barycentrically; allocations there are unavoidable.
            double[] noFuncScratch = hasFunction ? [] : new double[numStreamColorComponents];

            // The three corners of the most-recently completed triangle.
            // Required for edge-sharing flags 1 and 2. Each entry stores the pt, the raw
            // (decoded) colour components for subdivision, and the pre-evaluated colour for
            // the no-function fast path.
            var prevTri = new GouraudVertex[3];
            bool hasPrevTri = false;

            // Accumulator for consecutive flag-0 vertices (need 3 to form a free triangle).
            var flag0Buf = new GouraudVertex[3];
            int flag0Count = 0;

            var bitReader = new GouraudBitReader(shading.Data.Span);

            using var paint = new SKPaint();
            paint.IsAntialias = shading.AntiAlias;
            paint.BlendMode = currentState.BlendMode.ToSKBlendMode(); // White paint + Modulate: vertex_colour × (1,1,1,1) = vertex_colour.
            // Preserves the Gouraud-interpolated colours regardless of which role
            // (src / dst) SkiaSharp assigns to the vertex vs. paint colour.
            paint.Color = SKColors.White;

            if (path is not null)
            {
                _canvas.Save();
                _canvas.ClipPath(path);
            }

            Span<double> vertexEvalOut = stackalloc double[ShadingEvalBufferSize];
            double vertexAlpha = currentState.AlphaConstantNonStroking;
            ColorSpaceDetails freeFormColorSpace = shading.ColorSpace;

            try
            {
                while (bitReader.HasData)
                {
                    int flag;
                    long rawX, rawY;

                    // Per-vertex raw components only need to live on the heap when the
                    // function path will later barycentrically blend them. The no-function
                    // path consumes them immediately and shares one scratch buffer across
                    // every vertex.
                    double[] colorComponents = hasFunction
                        ? new double[numStreamColorComponents]
                        : noFuncScratch;

                    try
                    {
                        flag = (int)(bitReader.ReadBits(bitsPerFlag) & 3);
                        rawX = bitReader.ReadBits(bitsPerCoordinate);
                        rawY = bitReader.ReadBits(bitsPerCoordinate);
                        for (int i = 0; i < numStreamColorComponents; i++)
                        {
                            long raw = bitReader.ReadBits(bitsPerComponent);
                            double cMin = decode[4 + i * 2];
                            double cMax = decode[5 + i * 2];
                            colorComponents[i] = cMin + (raw / maxColorRaw) * (cMax - cMin);
                        }

                        // PDF spec: each vertex record occupies a whole number of bytes.
                        bitReader.AlignToByte();
                    }
                    catch
                    {
                        break;
                    }

                    // Decode coordinates via linear interpolation from raw integer range to Decode range.
                    double x = xMin + (rawX / maxCoordRaw) * (xMax - xMin);
                    double y = yMin + (rawY / maxCoordRaw) * (yMax - yMin);

                    // Evaluate optional function and convert to an SKColor through the colour space
                    // for the no-function fast path. When subdivision is needed the raw components
                    // are used instead.
                    int vertexWritten = shading.Eval(colorComponents, vertexEvalOut);
                    SKColor skColor = freeFormColorSpace.GetSKColor(vertexEvalOut.Slice(0, vertexWritten), vertexAlpha);

                    // Transform the vertex from shading/pattern space to canvas space.
                    SKPoint pt = patternTransformMatrix.MapPoint(new SKPoint((float)x, (float)y));
                    var vertex = new GouraudVertex(
                        pt,
                        hasFunction ? colorComponents : [],
                        skColor);

                    // Build triangles according to the edge-flag value (PDF spec Table 92).
                    switch (flag)
                    {
                        case 0:
                            // Accumulate free vertices; emit a triangle once three are collected.
                            flag0Buf[flag0Count] = vertex;
                            flag0Count++;

                            if (flag0Count == 3)
                            {
                                EmitGouraudTriangle(shading, currentState, hasFunction,
                                    flag0Buf[0], flag0Buf[1], flag0Buf[2],
                                    emitBuffer, subPts, subCols, outPositions, outColors, ref outCount, paint);
                                for (int i = 0; i < 3; i++)
                                {
                                    prevTri[i] = flag0Buf[i];
                                }
                                hasPrevTri = true;
                                flag0Count = 0;
                            }
                            break;

                        case 1:
                            // New triangle shares edge prevTri[1]–prevTri[2] with the previous triangle.
                            if (hasPrevTri)
                            {
                                EmitGouraudTriangle(shading, currentState, hasFunction,
                                    prevTri[1], prevTri[2], vertex,
                                    emitBuffer, subPts, subCols, outPositions, outColors, ref outCount, paint);

                                // Slide the window: new prevTri = [prevTri[1], prevTri[2], newVertex]
                                prevTri[0] = prevTri[1];
                                prevTri[1] = prevTri[2];
                                prevTri[2] = vertex;
                                flag0Count = 0;
                            }
                            break;

                        case 2:
                            // New triangle shares edge prevTri[0]–prevTri[2] with the previous triangle.
                            if (hasPrevTri)
                            {
                                EmitGouraudTriangle(shading, currentState, hasFunction,
                                    prevTri[0], prevTri[2], vertex,
                                    emitBuffer, subPts, subCols, outPositions, outColors, ref outCount, paint);

                                // Slide the window: new prevTri = [prevTri[0], prevTri[2], newVertex]
                                prevTri[1] = prevTri[2];
                                prevTri[2] = vertex;
                                flag0Count = 0;
                            }
                            break;
                    }
                }

                FlushGouraudBatch(outPositions, outColors, outCount, paint);
            }
            finally
            {
                if (path is not null)
                {
                    _canvas.Restore();
                }
            }
        }

        /// <summary>
        /// Submits any unflushed no-function-path triangles to the canvas. Allocates an
        /// exact-size temporary array when the batch is partial; passes the full-size buffer
        /// through unchanged on the common full-buffer path. Calls with <paramref name="outCount"/>
        /// equal to 0 (the function-path resting state) are a no-op.
        /// </summary>
        private void FlushGouraudBatch(SKPoint[] outPositions, SKColor[] outColors, int outCount, SKPaint paint)
        {
            if (outCount == 0)
            {
                return;
            }

            if (outCount == outPositions.Length)
            {
                _canvas.DrawVertices(SKVertexMode.Triangles, outPositions, null, outColors,
                    SKBlendMode.Modulate, null, paint);
                return;
            }

            var partialPts = new SKPoint[outCount];
            var partialCols = new SKColor[outCount];
            Array.Copy(outPositions, partialPts, outCount);
            Array.Copy(outColors, partialCols, outCount);
            _canvas.DrawVertices(SKVertexMode.Triangles, partialPts, null, partialCols,
                SKBlendMode.Modulate, null, paint);
        }

        /// <summary>
        /// Number of subdivisions per edge applied to each Gouraud triangle when a Function is
        /// present, so that piecewise-linear and step Function output is captured visibly.
        /// Total sub-triangles per parent = GouraudFunctionSubdivisions².
        /// <para>
        /// At 128 each sub-cell is ~1–2 output pixels wide on typical Type-4 patches, so
        /// vertex-colour Gouraud blending across a step boundary stays within sub-pixel range
        /// while curved boundaries (e.g. P.pdf pages 2/3/5 top-right) follow the parametric
        /// iso-line smoothly instead of the cell-grid sawtooth a uniform-per-cell approach
        /// would produce.
        /// </para>
        /// </summary>
        private const int GouraudFunctionSubdivisions = 128;

        /// <summary>
        /// Emits one parent Gouraud triangle. When <paramref name="hasFunction"/> is false
        /// the three vertices are appended to <paramref name="outPositions"/> / <paramref name="outColors"/>;
        /// when the next 3-vertex append would overflow the buffer it is first flushed via
        /// DrawVertices (the buffer capacity is a multiple of 3 so this means the buffer is
        /// exactly full). When <paramref name="hasFunction"/> is true the triangle is
        /// subdivided to capture non-linear function output, the n² sub-triangles fill
        /// <paramref name="outPositions"/> / <paramref name="outColors"/> exactly, and a
        /// single DrawVertices call is made before resetting <paramref name="outCount"/>.
        /// </summary>
        /// <param name="interpBuffer">
        /// Reusable buffer for the barycentric component blend; must have length ≥
        /// <c>a.Components.Length</c>. Owned by the caller so the per-sub-vertex allocation
        /// doesn't fall inside the n²/2 subdivision loop.
        /// </param>
        /// <param name="subPts">
        /// Sub-vertex grid scratch ((n+1)(n+2)/2 entries). Required when
        /// <paramref name="hasFunction"/> is true; may be null otherwise.
        /// </param>
        private void EmitGouraudTriangle(FreeFormGouraudShading shading, CurrentGraphicsState currentState,
            bool hasFunction, in GouraudVertex a, in GouraudVertex b, in GouraudVertex c,
            double[] interpBuffer, SKPoint[]? subPts, SKColor[]? subCols,
            SKPoint[] outPositions, SKColor[] outColors, ref int outCount, SKPaint paint)
        {
            if (!hasFunction)
            {
                if (outCount + 3 > outPositions.Length)
                {
                    // Capacity is a multiple of 3 and writes step by 3, so outCount must
                    // equal outPositions.Length here — the array is exactly full.
                    _canvas.DrawVertices(SKVertexMode.Triangles, outPositions, null, outColors,
                        SKBlendMode.Modulate, null, paint);
                    outCount = 0;
                }
                outPositions[outCount] = a.Pt; outColors[outCount] = a.Col; outCount++;
                outPositions[outCount] = b.Pt; outColors[outCount] = b.Col; outCount++;
                outPositions[outCount] = c.Pt; outColors[outCount] = c.Col; outCount++;
                return;
            }

            int components = a.Components.Length;
            double alpha = currentState.AlphaConstantNonStroking;
            ColorSpaceDetails subColorSpace = shading.ColorSpace;

            float ax = a.Pt.X, ay = a.Pt.Y;
            float bx = b.Pt.X, by = b.Pt.Y;
            float cx = c.Pt.X, cy = c.Pt.Y;

            const float invN = 1f / GouraudFunctionSubdivisions;

            // Per-sub-vertex Eval output lives in a single stackalloc buffer so the n²
            // (~16 K iters at default subdivisions) inner loop runs allocation-free.
            Span<double> subEvalOut = stackalloc double[ShadingEvalBufferSize];

            // Fill the (n+1)(n+2)/2 barycentric sub-vertex grid for this parent triangle.
            // subPts/subCols are caller-owned, reused across all parent triangles in the
            // current Render call so the ~100 KB grid scratch is allocated once.
            for (int i = 0; i <= GouraudFunctionSubdivisions; i++)
            {
                int rowOffset = i * (GouraudFunctionSubdivisions + 1) - i * (i - 1) / 2;
                for (int j = 0; j <= GouraudFunctionSubdivisions - i; j++)
                {
                    int k = GouraudFunctionSubdivisions - i - j;
                    float wa = i * invN;
                    float wb = j * invN;
                    float wc = k * invN;

                    SKPoint pt = new SKPoint(
                        wa * ax + wb * bx + wc * cx,
                        wa * ay + wb * by + wc * cy);

                    for (int ci = 0; ci < components; ci++)
                    {
                        interpBuffer[ci] = wa * a.Components[ci] + wb * b.Components[ci] + wc * c.Components[ci];
                    }

                    int subWritten = shading.Eval(new ReadOnlySpan<double>(interpBuffer, 0, components), subEvalOut);
                    SKColor col = subColorSpace.GetSKColor(subEvalOut.Slice(0, subWritten), alpha);

                    int idx = rowOffset + j;
                    subPts![idx] = pt;
                    subCols![idx] = col;
                }
            }

            // Stitch the sub-grid into 2 triangles per "rhombus" cell, plus boundary
            // triangles along the diagonal. For each row i (0..n-1), column j (0..n-1-i):
            //   upper-tri: (i,j), (i+1,j), (i,j+1)
            //   lower-tri: (i+1,j), (i+1,j+1), (i,j+1) — only when i+j < n-1
            // The writes fill outPositions/outColors exactly (length = n² × 3).
            int w = 0;
            for (int i = 0; i < GouraudFunctionSubdivisions; i++)
            {
                int rowOffset = i * (GouraudFunctionSubdivisions + 1) - i * (i - 1) / 2;
                int nextRowOffset = (i + 1) * (GouraudFunctionSubdivisions + 1) - (i + 1) * i / 2;
                for (int j = 0; j < GouraudFunctionSubdivisions - i; j++)
                {
                    int v0 = rowOffset + j;
                    int v1 = nextRowOffset + j;
                    int v2 = rowOffset + j + 1;
                    outPositions[w] = subPts![v0]; outColors[w] = subCols![v0]; w++;
                    outPositions[w] = subPts[v1]; outColors[w] = subCols[v1]; w++;
                    outPositions[w] = subPts[v2]; outColors[w] = subCols[v2]; w++;

                    if (i + j < GouraudFunctionSubdivisions - 1)
                    {
                        int v3 = nextRowOffset + j + 1;
                        outPositions[w] = subPts[v1]; outColors[w] = subCols[v1]; w++;
                        outPositions[w] = subPts[v3]; outColors[w] = subCols[v3]; w++;
                        outPositions[w] = subPts[v2]; outColors[w] = subCols[v2]; w++;
                    }
                }
            }

            _canvas.DrawVertices(SKVertexMode.Triangles, outPositions, null, outColors,
                SKBlendMode.Modulate, null, paint);
            outCount = 0;
        }

        /// <summary>
        /// One vertex of a Type 4 Gouraud triangle: device-space point, raw stream colour
        /// components (used when a Function is present and per-sub-vertex evaluation is needed),
        /// and the pre-evaluated SKColor used by the no-function fast path.
        /// </summary>
        private readonly struct GouraudVertex
        {
            public readonly SKPoint Pt;
            public readonly double[] Components;
            public readonly SKColor Col;

            public GouraudVertex(SKPoint pt, double[] components, SKColor col)
            {
                Pt = pt;
                Components = components;
                Col = col;
            }
        }

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

        private void RenderShadingPattern(SKPath path, ShadingPatternColor pattern, bool isStroke)
        {
            if (pattern.ExtGState is not null)
            {
                // TODO
            }

            // We cancel CTM, but not canvas' Y flip, as we still need it.
            var patternTransform = CurrentTransformationMatrix.ToSkMatrix().Invert()
                .PreConcat(_currentStreamOriginalTransforms.Peek())
                .PreConcat(pattern.Matrix.ToSkMatrix());

            switch (pattern.Shading.ShadingType)
            {
                case ShadingType.Axial:
                    RenderAxialShading(pattern.Shading as AxialShading, in patternTransform, isStroke, path);
                    break;

                case ShadingType.Radial:
                    RenderRadialShading(pattern.Shading as RadialShading, in patternTransform, isStroke, path);
                    break;

                case ShadingType.FunctionBased:
                    RenderFunctionBasedShading(pattern.Shading as FunctionBasedShading, in patternTransform, isStroke, path);
                    break;

                case ShadingType.FreeFormGouraud:
                    RenderFreeFormGouraudShading(pattern.Shading as FreeFormGouraudShading, in patternTransform, isStroke, path);
                    break;

                case ShadingType.LatticeFormGouraud:
                    RenderLatticeFormGouraudShading(pattern.Shading as LatticeFormGouraudShading, in patternTransform, path);
                    break;

                case ShadingType.CoonsPatch:
                    RenderCoonsPatchShading(pattern.Shading as CoonsPatchMeshesShading, in patternTransform, path);
                    break;

                case ShadingType.TensorProductPatch:
                    RenderTensorProductPatchShading(pattern.Shading as TensorProductPatchMeshesShading, in patternTransform, path);
                    break;
            }
        }

        // A page commonly paints the same Coons/Tensor mesh many times (e.g. a chart re-invokes the
        // `sh` operator for one shading dozens of times). The tessellated triangle list only depends
        // on the shading data and the transform in force, so cache it as an SKPicture and replay it
        // instead of re-tessellating ~1.4 K patches each time. Keyed on the shading instance, with
        // the transform re-checked on hit so pattern fills under a changed CTM rebuild correctly.
        private Dictionary<Shading, (SKMatrix Transform, SKPicture Picture)>? _meshPictureCache;

        // Advisory cull hint for recording a mesh picture. The recorded geometry lives in pattern
        // space (arbitrary range), so a tight rect could clip it — keep it effectively unbounded.
        private static readonly SKRect MeshPictureCullRect =
            new SKRect(-1_000_000f, -1_000_000f, 1_000_000f, 1_000_000f);

        /// <summary>
        /// Returns the cached tessellated mesh picture for <paramref name="shading"/> under
        /// <paramref name="transform"/>, recording it via <paramref name="drawMesh"/> on first use.
        /// The picture stores the geometry in pattern space; the caller's canvas transform (and any
        /// clip) is applied when it is replayed, so one picture serves every invocation that shares
        /// the same transform.
        /// </summary>
        private SKPicture GetOrBuildMeshPicture(Shading shading, in SKMatrix transform, Action drawMesh)
        {
            if (_meshPictureCache is not null
                && _meshPictureCache.TryGetValue(shading, out var entry)
                && entry.Transform.Equals(transform))
            {
                return entry.Picture;
            }

            using var recorder = new SKPictureRecorder();
            SKCanvas saved = _canvas;
            _canvas = recorder.BeginRecording(MeshPictureCullRect, true);
            try
            {
                drawMesh();
                _canvas.Flush();
            }
            finally
            {
                _canvas = saved;
            }

            SKPicture picture = recorder.EndRecording();

            _meshPictureCache ??= new Dictionary<Shading, (SKMatrix, SKPicture)>();
            if (_meshPictureCache.TryGetValue(shading, out var stale))
            {
                // Same shading, different transform: the old picture is now unreachable.
                stale.Picture.Dispose();
            }

            _meshPictureCache[shading] = (transform, picture);
            return picture;
        }

        /// <summary>Replays a cached mesh picture, optionally clipped to <paramref name="path"/>.</summary>
        private void DrawCachedMesh(SKPicture mesh, SKPath? path)
        {
            if (path is not null)
            {
                _canvas.Save();
                _canvas.ClipPath(path);
                _canvas.DrawPicture(mesh);
                _canvas.Restore();
            }
            else
            {
                _canvas.DrawPicture(mesh);
            }
        }

        /// <summary>
        /// Number of subdivisions per axis used when sampling the patch surface geometry
        /// for Coons / Tensor patches. Geometric accuracy only — colour accuracy comes
        /// either from per-vertex Gouraud (no-function path) or from texture sampling
        /// (function path). 32 keeps the linear-per-cell approximation of cubic surfaces
        /// well under one pixel for typical render scales while keeping triangle counts low.
        /// </summary>
        private const int PatchSubdivisions = 32;

        /// <summary>
        /// Target edge length, in pattern-space units, of a single tessellation cell. A patch is
        /// subdivided just enough that each cell is roughly this size, so a mesh made of many tiny
        /// patches (e.g. a 1.4 K-patch gradient banner) produces a few thousand triangles instead
        /// of 1.4 K × 32² ≈ 1.5 M. Without this, large finely-tessellated meshes dominate both
        /// render time and the native memory held by the recorded picture.
        /// </summary>
        private const float PatchCellSize = 4f;

        /// <summary>
        /// Chooses a per-patch subdivision count proportional to the patch's control-polygon
        /// extent, clamped to [1, <see cref="PatchSubdivisions"/>]. Small patches collapse to a
        /// handful of cells; only patches that genuinely span a large area pay the full 32×32.
        /// </summary>
        private static int ComputePatchSubdivisions(ReadOnlySpan<SKPoint> controlPoints)
        {
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            foreach (SKPoint cp in controlPoints)
            {
                if (cp.X < minX) minX = cp.X;
                if (cp.X > maxX) maxX = cp.X;
                if (cp.Y < minY) minY = cp.Y;
                if (cp.Y > maxY) maxY = cp.Y;
            }

            float extent = Math.Max(maxX - minX, maxY - minY);
            if (!(extent > 0f))
            {
                return 1;
            }

            int n = (int)Math.Ceiling(extent / PatchCellSize);
            if (n < 1)
            {
                n = 1;
            }
            else if (n > PatchSubdivisions)
            {
                n = PatchSubdivisions;
            }
            
            return n;
        }

        /// <summary>
        /// Resolution of the per-patch colour texture used for function-based shadings.
        /// 512² with nearest-neighbour sampling means each texel maps to ~1 output pixel at
        /// typical chart scales — step-function transitions stay pixel-sharp while smooth
        /// gradients show no visible texel blockiness.
        /// </summary>
        private const int PatchTextureSize = 512;

        /// <summary>
        /// Renders a Type 5 Lattice-form Gouraud-shaded triangle mesh.
        /// Vertices are arranged in a row-major lattice, <paramref name="VerticesPerRow"/> wide;
        /// each pair of adjacent rows is triangulated into 2·(VerticesPerRow − 1) triangles.
        /// No edge-flag bits are present in the stream.
        /// </summary>
        private void RenderLatticeFormGouraudShading(LatticeFormGouraudShading shading,
            in SKMatrix patternTransformMatrix, SKPath? path = null)
        {
            if (shading.Data.IsEmpty)
            {
                return;
            }

            int verticesPerRow = shading.VerticesPerRow;
            if (verticesPerRow < 2)
            {
                return;
            }

            var currentState = GetCurrentState();
            int bitsPerCoordinate = shading.BitsPerCoordinate;
            int bitsPerComponent = shading.BitsPerComponent;
            var decode = shading.Decode;

            int numStreamColorComponents = (decode.Length - 4) / 2;
            double maxCoordRaw = (1L << bitsPerCoordinate) - 1.0;
            double maxColorRaw = (1L << bitsPerComponent) - 1.0;
            double xMin = decode[0], xMax = decode[1];
            double yMin = decode[2], yMax = decode[3];

            // Stream the lattice row by row: hold only the previous and current row of
            // vertices, and submit each row-pair (2·(verticesPerRow − 1) triangles) via its
            // own DrawVertices call. Peak memory stays bounded — no full-mesh accumulation.
            int rowPairVertexCount = (verticesPerRow - 1) * 6;
            var rowA = new (SKPoint pt, SKColor col)[verticesPerRow];
            var rowB = new (SKPoint pt, SKColor col)[verticesPerRow];
            var outPositions = new SKPoint[rowPairVertexCount];
            var outColors = new SKColor[rowPairVertexCount];

            // Reusable per-vertex buffer — Eval() needs a heap double[], but it doesn't keep
            // the reference (it returns it directly when no Function is present, otherwise
            // returns a fresh array), so reusing is safe.
            double[] colorBuffer = new double[numStreamColorComponents];
            var bitReader = new GouraudBitReader(shading.Data.Span);

            // No first row means no triangles can ever be produced — bail before allocating
            // the paint or touching canvas clipping state.
            if (!TryReadLatticeRow(ref bitReader, rowA, bitsPerCoordinate, bitsPerComponent,
                    maxCoordRaw, maxColorRaw, decode, numStreamColorComponents,
                    xMin, xMax, yMin, yMax, in patternTransformMatrix,
                    shading, currentState, colorBuffer))
            {
                return;
            }

            using var paint = new SKPaint
            {
                IsAntialias = shading.AntiAlias,
                BlendMode = currentState.BlendMode.ToSKBlendMode(),
                Color = SKColors.White,
            };

            if (path is not null)
            {
                _canvas.Save();
                _canvas.ClipPath(path);
            }

            try
            {
                while (TryReadLatticeRow(ref bitReader, rowB, bitsPerCoordinate, bitsPerComponent,
                           maxCoordRaw, maxColorRaw, decode, numStreamColorComponents,
                           xMin, xMax, yMin, yMax, in patternTransformMatrix,
                           shading, currentState, colorBuffer))
                {
                    EmitRowPairTriangles(rowA, rowB, verticesPerRow, outPositions, outColors);
                    _canvas.DrawVertices(SKVertexMode.Triangles, outPositions, null, outColors,
                        SKBlendMode.Modulate, null, paint);

                    (rowA, rowB) = (rowB, rowA);
                }
            }
            finally
            {
                if (path is not null)
                {
                    _canvas.Restore();
                }
            }
        }

        /// <summary>
        /// Renders a Type 6 Coons-patch mesh.
        /// Each patch is bounded by four cubic Bézier curves; the surface S(u,v) blends
        /// the boundary curves and corners per PDF 32000-1:2008 §8.7.4.5.6.
        /// </summary>
        private void RenderCoonsPatchShading(CoonsPatchMeshesShading shading,
            in SKMatrix patternTransformMatrix, SKPath? path = null)
        {
            if (shading.Data.IsEmpty)
            {
                return;
            }

            // Tessellate once into an SKPicture and replay it on repeated invocations. See
            // RenderTensorProductPatchShading / GetOrBuildMeshPicture.
            SKMatrix transform = patternTransformMatrix;
            SKPicture mesh = GetOrBuildMeshPicture(shading, in transform,
                () => DrawCoonsMeshUnclipped(shading, transform));
            DrawCachedMesh(mesh, path);
        }

        private void DrawCoonsMeshUnclipped(CoonsPatchMeshesShading shading,
            SKMatrix patternTransformMatrix)
        {
            var currentState = GetCurrentState();
            int bitsPerCoordinate = shading.BitsPerCoordinate;
            int bitsPerComponent = shading.BitsPerComponent;
            int bitsPerFlag = shading.BitsPerFlag;
            var decode = shading.Decode;

            int numStreamColorComponents = (decode.Length - 4) / 2;
            double maxCoordRaw = (1L << bitsPerCoordinate) - 1.0;
            double maxColorRaw = (1L << bitsPerComponent) - 1.0;
            double xMin = decode[0], xMax = decode[1];
            double yMin = decode[2], yMax = decode[3];

            // When a Function is present, the colour is non-linear in the bilinear-interpolated
            // parameter (most visibly: stitched Type-3 functions and Type-2 N=0 step functions).
            // Per-vertex Gouraud interpolation can't represent these correctly inside a cell —
            // a cell straddling a step boundary smears the two output colours together.
            // So for the Function path we draw each patch with a pre-evaluated colour texture
            // and texture-coordinate mapping, getting per-pixel function output.
            bool hasFunction = shading.Functions is { Length: > 0 };

            // Pre-evaluate the Function + colour-space conversion (which for a Separation/DeviceN
            // colour space invokes a per-vertex PostScript tint transform) into a lookup table
            // shared by every patch and texel, so the hot loops do a table lookup instead. See
            // ShadingColorCache.
            ShadingColorCache? colorCache = ShadingColorCache.TryBuild(shading, decode,
                numStreamColorComponents, currentState.AlphaConstantNonStroking);

            // Per-shading scratch for the no-function (vertex-colour Gouraud) path. Each
            // patch tessellates into the same fixed-size triangle arrays and is submitted
            // via its own DrawVertices call, so memory stays bounded regardless of mesh size.
            const int gridCount = (PatchSubdivisions + 1) * (PatchSubdivisions + 1);
            SKPoint[]? grid = null;
            SKColor[]? gridCol = null;
            double[]? interpBuffer = null;
            SKPaint? gouraudPaint = null;

            if (!hasFunction)
            {
                grid = new SKPoint[gridCount];
                gridCol = new SKColor[gridCount];
                interpBuffer = new double[numStreamColorComponents];
                gouraudPaint = new SKPaint
                {
                    IsAntialias = shading.AntiAlias,
                    BlendMode = currentState.BlendMode.ToSKBlendMode(),
                    Color = SKColors.White,
                };
            }

            try
            {
                // Patch buffers are alternated between the current and previous patch via a
                // two-slot pool: the implicit-edge flags (1/2/3) require keeping the previous
                // patch alive, but at most one previous and one current patch are live at a
                // time. Pre-allocating both pairs lifts ~12 SKPoint slots + 4 component
                // arrays out of the per-patch hot loop.
                var ptsBufA = new SKPoint[12];
                var ptsBufB = new SKPoint[12];
                var colorsBufA = new double[4][];
                var colorsBufB = new double[4][];
                for (int i = 0; i < 4; i++)
                {
                    colorsBufA[i] = new double[numStreamColorComponents];
                    colorsBufB[i] = new double[numStreamColorComponents];
                }

                SKPoint[] points = ptsBufA;
                double[][] cornerColors = colorsBufA;
                SKPoint[]? prevPts = null;
                double[][]? prevColors = null;
                var bitReader = new GouraudBitReader(shading.Data.Span);

                while (bitReader.HasData)
                {
                    int flag;
                    try
                    {
                        flag = (int)(bitReader.ReadBits(bitsPerFlag) & 3);
                    }
                    catch
                    {
                        break;
                    }

                    int newPointCount = flag == 0 ? 12 : 8;
                    int newColorCount = flag == 0 ? 4 : 2;

                    if (flag == 0)
                    {
                        if (!ReadPatchPoints(ref bitReader, bitsPerCoordinate, maxCoordRaw, xMin, xMax, yMin, yMax,
                                in patternTransformMatrix, points, 0, newPointCount))
                        {
                            break;
                        }
                        if (!ReadPatchColorsInto(ref bitReader, bitsPerComponent, maxColorRaw, decode, numStreamColorComponents,
                                cornerColors, 0, newColorCount))
                        {
                            break;
                        }
                    }
                    else
                    {
                        if (prevPts is null || prevColors is null)
                        {
                            // No previous patch — malformed stream; bail out gracefully.
                            break;
                        }

                        // Per PDF spec Table 90: the implicit edge of the new patch is the C2 curve of the
                        // previous patch, the right curve, or the left curve, depending on the flag value.
                        int p11Idx, p12Idx, p13Idx, p14Idx;        // previous patch boundary points re-used as new patch corners
                        int newC1ColorIdx, newC2ColorIdx;          // previous patch corner colours that become new patch corner colours
                        switch (flag)
                        {
                            case 1: p11Idx = 3; p12Idx = 4; p13Idx = 5; p14Idx = 6; newC1ColorIdx = 1; newC2ColorIdx = 2; break;
                            case 2: p11Idx = 6; p12Idx = 7; p13Idx = 8; p14Idx = 9; newC1ColorIdx = 2; newC2ColorIdx = 3; break;
                            case 3: p11Idx = 9; p12Idx = 10; p13Idx = 11; p14Idx = 0; newC1ColorIdx = 3; newC2ColorIdx = 0; break;
                            default: return;
                        }

                        points[0] = prevPts[p11Idx];
                        points[1] = prevPts[p12Idx];
                        points[2] = prevPts[p13Idx];
                        points[3] = prevPts[p14Idx];

                        // Copy component values from prev's slot into current's slot — the
                        // destination array is already owned by `cornerColors`, so we don't
                        // reassign the slot reference (that would alias prev's buffer and the
                        // next patch would overwrite both).
                        Array.Copy(prevColors[newC1ColorIdx], cornerColors[0], numStreamColorComponents);
                        Array.Copy(prevColors[newC2ColorIdx], cornerColors[1], numStreamColorComponents);

                        if (!ReadPatchPoints(ref bitReader, bitsPerCoordinate, maxCoordRaw, xMin, xMax, yMin, yMax,
                                in patternTransformMatrix, points, 4, newPointCount))
                        {
                            break;
                        }
                        if (!ReadPatchColorsInto(ref bitReader, bitsPerComponent, maxColorRaw, decode, numStreamColorComponents,
                                cornerColors, 2, newColorCount))
                        {
                            break;
                        }
                    }

                    bitReader.AlignToByte();

                    if (hasFunction)
                    {
                        DrawCoonsPatchTextured(shading, currentState, points, cornerColors, colorCache);
                    }
                    else
                    {
                        TessellateAndDrawCoonsPatch(shading, currentState, points, cornerColors,
                            grid!, gridCol!, interpBuffer!, gouraudPaint!, colorCache);
                    }

                    prevPts = points;
                    prevColors = cornerColors;
                    // Alternate the active buffer so prev stays valid while we fill current.
                    points = ReferenceEquals(points, ptsBufA) ? ptsBufB : ptsBufA;
                    cornerColors = ReferenceEquals(cornerColors, colorsBufA) ? colorsBufB : colorsBufA;
                }
            }
            finally
            {
                gouraudPaint?.Dispose();
            }
        }

        /// <summary>
        /// Renders a Type 7 Tensor-product patch mesh.
        /// Each patch is a bicubic Bézier surface defined by 16 control points.
        /// The corner colour assignment matches Type 6, so only the boundary geometry differs.
        /// </summary>
        private void RenderTensorProductPatchShading(TensorProductPatchMeshesShading shading,
            in SKMatrix patternTransformMatrix, SKPath? path = null)
        {
            if (shading.Data.IsEmpty)
            {
                return;
            }

            // The same mesh is frequently painted many times (e.g. a chart re-invokes the `sh`
            // operator for one shading dozens of times). Re-tessellating ~1.4 K patches and
            // rebuilding the colour LUT on every call is what makes such pages take tens of
            // seconds. Tessellate once into an SKPicture and replay it — clipped to the current
            // path — on subsequent invocations. See GetOrBuildMeshPicture.
            SKMatrix transform = patternTransformMatrix;
            SKPicture mesh = GetOrBuildMeshPicture(shading, in transform,
                () => DrawTensorMeshUnclipped(shading, transform));
            DrawCachedMesh(mesh, path);
        }

        private void DrawTensorMeshUnclipped(TensorProductPatchMeshesShading shading,
            SKMatrix patternTransformMatrix)
        {
            var currentState = GetCurrentState();
            int bitsPerCoordinate = shading.BitsPerCoordinate;
            int bitsPerComponent = shading.BitsPerComponent;
            int bitsPerFlag = shading.BitsPerFlag;
            var decode = shading.Decode;

            int numStreamColorComponents = (decode.Length - 4) / 2;
            double maxCoordRaw = (1L << bitsPerCoordinate) - 1.0;
            double maxColorRaw = (1L << bitsPerComponent) - 1.0;
            double xMin = decode[0], xMax = decode[1];
            double yMin = decode[2], yMax = decode[3];

            // See RenderCoonsPatchShading for why the function path uses textured drawing.
            bool hasFunction = shading.Functions is { Length: > 0 };

            // Pre-evaluate the Function + colour-space conversion into a lookup table shared by
            // every patch (see ShadingColorCache / RenderCoonsPatchShading).
            ShadingColorCache? colorCache = ShadingColorCache.TryBuild(shading, decode,
                numStreamColorComponents, currentState.AlphaConstantNonStroking);

            // Per-shading scratch for the no-function (vertex-colour Gouraud) path. See
            // RenderCoonsPatchShading for the per-patch-DrawVertices rationale.
            const int gridCount = (PatchSubdivisions + 1) * (PatchSubdivisions + 1);
            SKPoint[]? grid = null;
            SKColor[]? gridCol = null;
            double[]? interpBuffer = null;
            SKPaint? gouraudPaint = null;

            if (!hasFunction)
            {
                grid = new SKPoint[gridCount];
                gridCol = new SKColor[gridCount];
                interpBuffer = new double[numStreamColorComponents];
                gouraudPaint = new SKPaint
                {
                    IsAntialias = shading.AntiAlias,
                    BlendMode = currentState.BlendMode.ToSKBlendMode(),
                    Color = SKColors.White,
                };
            }

            try
            {
                // See RenderCoonsPatchShading for the two-slot ring-buffer rationale.
                var ptsBufA = new SKPoint[16];
                var ptsBufB = new SKPoint[16];
                var colorsBufA = new double[4][];
                var colorsBufB = new double[4][];
                for (int i = 0; i < 4; i++)
                {
                    colorsBufA[i] = new double[numStreamColorComponents];
                    colorsBufB[i] = new double[numStreamColorComponents];
                }

                SKPoint[] points = ptsBufA;
                double[][] cornerColors = colorsBufA;
                SKPoint[]? prevPts = null;
                double[][]? prevColors = null;
                var bitReader = new GouraudBitReader(shading.Data.Span);

                while (bitReader.HasData)
                {
                    int flag;
                    try
                    {
                        flag = (int)(bitReader.ReadBits(bitsPerFlag) & 3);
                    }
                    catch
                    {
                        break;
                    }

                    int newPointCount = flag == 0 ? 16 : 12;
                    int newColorCount = flag == 0 ? 4 : 2;

                    if (flag == 0)
                    {
                        if (!ReadPatchPoints(ref bitReader, bitsPerCoordinate, maxCoordRaw, xMin, xMax, yMin, yMax,
                                in patternTransformMatrix, points, 0, newPointCount))
                        {
                            break;
                        }
                        if (!ReadPatchColorsInto(ref bitReader, bitsPerComponent, maxColorRaw, decode, numStreamColorComponents,
                                cornerColors, 0, newColorCount))
                        {
                            break;
                        }
                    }
                    else
                    {
                        if (prevPts is null || prevColors is null)
                        {
                            break;
                        }

                        // For Type 7, only the four boundary corners (indices 0, 3, 6, 9 in the 16-point sequence)
                        // are reused; the four interior control points (indices 12-15) are always new.
                        int p11Idx, p12Idx, p13Idx, p14Idx;
                        int newC1ColorIdx, newC2ColorIdx;
                        switch (flag)
                        {
                            case 1: p11Idx = 3; p12Idx = 4; p13Idx = 5; p14Idx = 6; newC1ColorIdx = 1; newC2ColorIdx = 2; break;
                            case 2: p11Idx = 6; p12Idx = 7; p13Idx = 8; p14Idx = 9; newC1ColorIdx = 2; newC2ColorIdx = 3; break;
                            case 3: p11Idx = 9; p12Idx = 10; p13Idx = 11; p14Idx = 0; newC1ColorIdx = 3; newC2ColorIdx = 0; break;
                            default: return;
                        }

                        points[0] = prevPts[p11Idx];
                        points[1] = prevPts[p12Idx];
                        points[2] = prevPts[p13Idx];
                        points[3] = prevPts[p14Idx];
                        Array.Copy(prevColors[newC1ColorIdx], cornerColors[0], numStreamColorComponents);
                        Array.Copy(prevColors[newC2ColorIdx], cornerColors[1], numStreamColorComponents);

                        if (!ReadPatchPoints(ref bitReader, bitsPerCoordinate, maxCoordRaw, xMin, xMax, yMin, yMax,
                                in patternTransformMatrix, points, 4, newPointCount))
                        {
                            break;
                        }
                        if (!ReadPatchColorsInto(ref bitReader, bitsPerComponent, maxColorRaw, decode, numStreamColorComponents,
                                cornerColors, 2, newColorCount))
                        {
                            break;
                        }
                    }

                    bitReader.AlignToByte();

                    if (hasFunction)
                    {
                        DrawTensorPatchTextured(shading, currentState, points, cornerColors, colorCache);
                    }
                    else
                    {
                        TessellateAndDrawTensorPatch(shading, currentState, points, cornerColors,
                            grid!, gridCol!, interpBuffer!, gouraudPaint!, colorCache);
                    }

                    prevPts = points;
                    prevColors = cornerColors;
                    points = ReferenceEquals(points, ptsBufA) ? ptsBufB : ptsBufA;
                    cornerColors = ReferenceEquals(cornerColors, colorsBufA) ? colorsBufB : colorsBufA;
                }
            }
            finally
            {
                gouraudPaint?.Dispose();
            }
        }

        /// <summary>
        /// Reads <paramref name="count"/> point records from the bit stream into <paramref name="dest"/> starting
        /// at <paramref name="destOffset"/>, applying the Decode array and the pattern transform matrix.
        /// Returns false if the stream is truncated mid-record.
        /// </summary>
        private static bool ReadPatchPoints(ref GouraudBitReader bitReader, int bitsPerCoordinate, double maxCoordRaw,
            double xMin, double xMax, double yMin, double yMax,
            in SKMatrix patternTransformMatrix,
            Span<SKPoint> dest, int destOffset, int count)
        {
            double xScale = (xMax - xMin) / maxCoordRaw;
            double yScale = (yMax - yMin) / maxCoordRaw;
            for (int i = 0; i < count; i++)
            {
                long rawX, rawY;
                try
                {
                    rawX = bitReader.ReadBits(bitsPerCoordinate);
                    rawY = bitReader.ReadBits(bitsPerCoordinate);
                }
                catch
                {
                    return false;
                }

                double x = xMin + rawX * xScale;
                double y = yMin + rawY * yScale;
                dest[destOffset + i] = patternTransformMatrix.MapPoint(new SKPoint((float)x, (float)y));
            }
            return true;
        }

        /// <summary>
        /// Reads <paramref name="count"/> corner-colour records from the bit stream into the
        /// pre-allocated double[] slots of <paramref name="dest"/> starting at
        /// <paramref name="destOffset"/>. The slots are not reassigned — each existing inner
        /// array is overwritten in place so the caller can use a two-buffer ring across
        /// successive patches without aliasing the previous patch's components.
        /// Each colour is stored as the per-vertex stream components (n components if no
        /// Function, 1 parametric value otherwise) decoded via the Decode array. Function
        /// evaluation is deferred until the patch is tessellated so that the per-pixel
        /// function eval can capture non-linear / stitched / step functions correctly.
        /// </summary>
        private static bool ReadPatchColorsInto(ref GouraudBitReader bitReader, int bitsPerComponent, double maxColorRaw,
            double[] decode, int numStreamColorComponents,
            double[][] dest, int destOffset, int count)
        {
            double invMaxColorRaw = 1.0 / maxColorRaw;
            for (int i = 0; i < count; i++)
            {
                double[] components = dest[destOffset + i];
                try
                {
                    for (int k = 0; k < numStreamColorComponents; k++)
                    {
                        long raw = bitReader.ReadBits(bitsPerComponent);
                        double cMin = decode[4 + k * 2];
                        double cMax = decode[5 + k * 2];
                        components[k] = cMin + (raw * invMaxColorRaw) * (cMax - cMin);
                    }
                }
                catch
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Samples a Coons patch surface on a (PatchSubdivisions+1)² UV grid, builds the
        /// triangle list into the supplied exact-size buffers, and submits a single
        /// DrawVertices call. Corner-colour bilinear interpolation matches PDFBox:
        /// cornerColors[0..3] correspond to (u,v) = (0,0), (1,0), (1,1), (0,1).
        /// <para>
        /// All scratch (<paramref name="grid"/>, <paramref name="gridCol"/>,
        /// <paramref name="interpBuffer"/>) and output buffers are owned by the caller and
        /// reused across every patch in the mesh, so the per-patch loop runs without
        /// allocations.
        /// </para>
        /// </summary>
        private void TessellateAndDrawCoonsPatch(Shading shading, CurrentGraphicsState currentState,
            ReadOnlySpan<SKPoint> pts, double[][] cornerColors,
            SKPoint[] grid, SKColor[] gridCol, double[] interpBuffer,
            SKPaint paint, ShadingColorCache? colorCache)
        {
            // Subdivide proportionally to the patch size — a fine mesh of tiny patches needs only
            // a cell or two each rather than the full 32×32. See ComputePatchSubdivisions.
            int n = ComputePatchSubdivisions(pts);
            System.Diagnostics.Debug.Assert(n <= PatchSubdivisions);

            // The four Coons boundary curves only depend on either u or v, not both, so
            // evaluating them once per axis turns the (n+1)² cubic-Bezier-pair workload
            // into (n+1) × 4 evaluations — a ~17× drop at n = 32. Sampled values land in
            // stackalloc Span<SKPoint> tables (≤ 132 entries each, ~1 KB total).
            int axisLen = n + 1;
            Span<SKPoint> sBottom = stackalloc SKPoint[axisLen];
            Span<SKPoint> sTop = stackalloc SKPoint[axisLen];
            Span<SKPoint> sLeft = stackalloc SKPoint[axisLen];
            Span<SKPoint> sRight = stackalloc SKPoint[axisLen];

            SKPoint p0 = pts[0], p1 = pts[1], p2 = pts[2], p3 = pts[3];
            SKPoint p4 = pts[4], p5 = pts[5], p6 = pts[6], p7 = pts[7];
            SKPoint p8 = pts[8], p9 = pts[9], p10 = pts[10], p11 = pts[11];

            float invN = 1f / n;
            for (int i = 0; i < axisLen; i++)
            {
                float u = i * invN;
                sBottom[i] = CubicBezier(p0, p1, p2, p3, u);
                sTop[i] = CubicBezier(p9, p8, p7, p6, u);
            }

            for (int j = 0; j < axisLen; j++)
            {
                float v = j * invN;
                sLeft[j] = CubicBezier(p0, p11, p10, p9, v);
                sRight[j] = CubicBezier(p3, p4, p5, p6, v);
            }

            float p00x = p0.X, p00y = p0.Y;
            float p10x = p3.X, p10y = p3.Y;
            float p11x = p6.X, p11y = p6.Y;
            float p01x = p9.X, p01y = p9.Y;

            double alpha = currentState.AlphaConstantNonStroking;
            Span<double> coonsEvalBuffer = stackalloc double[ShadingEvalBufferSize];

            for (int j = 0; j < axisLen; j++)
            {
                float v = j * invN;
                float oneMinusV = 1f - v;
                SKPoint sLj = sLeft[j];
                SKPoint sRj = sRight[j];
                int rowOffset = j * axisLen;
                for (int i = 0; i < axisLen; i++)
                {
                    float u = i * invN;
                    float oneMinusU = 1f - u;
                    SKPoint sBi = sBottom[i];
                    SKPoint sTi = sTop[i];

                    float x = oneMinusV * sBi.X + v * sTi.X
                              + oneMinusU * sLj.X + u * sRj.X
                              - oneMinusU * oneMinusV * p00x - u * oneMinusV * p10x
                              - u * v * p11x - oneMinusU * v * p01x;
                    float y = oneMinusV * sBi.Y + v * sTi.Y
                              + oneMinusU * sLj.Y + u * sRj.Y
                              - oneMinusU * oneMinusV * p00y - u * oneMinusV * p10y
                              - u * v * p11y - oneMinusU * v * p01y;

                    grid[rowOffset + i] = new SKPoint(x, y);
                    gridCol[rowOffset + i] = EvaluatePatchColor(shading, alpha, cornerColors, u, v, interpBuffer, coonsEvalBuffer, colorCache);
                }
            }

            DrawGridTriangles(grid, gridCol, n, paint);
        }

        /// <summary>
        /// Samples a Tensor-product patch surface on a (PatchSubdivisions+1)² UV grid,
        /// builds the triangle list into the supplied exact-size buffers, and submits a
        /// single DrawVertices call. The 4×4 control grid follows the PDFBox layout:
        /// rows indexed by v, columns by u. See <see cref="TessellateAndDrawCoonsPatch"/>
        /// for the buffer-ownership rationale.
        /// </summary>
        private void TessellateAndDrawTensorPatch(Shading shading, CurrentGraphicsState currentState,
            SKPoint[] tcp, double[][] cornerColors,
            SKPoint[] grid, SKColor[] gridCol, double[] interpBuffer,
            SKPaint paint, ShadingColorCache? colorCache)
        {
            // Map the 16 stream points into a 4×4 grid stored row-major in a stackalloc
            // span. Indexing is row * 4 + col (col is u, row is v). See the comment on
            // BuildTensorControlGrid for the layout.
            Span<SKPoint> p = stackalloc SKPoint[16];
            BuildTensorControlGrid(tcp, p);

            // Subdivide proportionally to the patch size: a fine mesh of tiny patches needs only a
            // cell or two each, not the full 32×32 (which would blow up triangle count and memory).
            int n = ComputePatchSubdivisions(p);
            System.Diagnostics.Debug.Assert(n <= PatchSubdivisions);
            
            // Precompute Bernstein basis values for each sampled u and v into flat 4×(n+1)
            // spans so the inner sampling loop reads contiguous memory and skips the
            // jagged-array allocations the previous float[][] form required.
            int bCount = 4 * (n + 1);
            Span<float> bU = stackalloc float[bCount];
            Span<float> bV = stackalloc float[bCount];

            for (int i = 0; i <= n; i++)
            {
                BernsteinCubic((float)i / n, bU.Slice(i * 4, 4));
            }
            for (int j = 0; j <= n; j++)
            {
                BernsteinCubic((float)j / n, bV.Slice(j * 4, 4));
            }

            double alpha = currentState.AlphaConstantNonStroking;
            Span<double> tensorEvalBuffer = stackalloc double[ShadingEvalBufferSize];

            for (int j = 0; j <= n; j++)
            {
                float v = (float)j / n;
                ReadOnlySpan<float> bv = bV.Slice(j * 4, 4);
                for (int i = 0; i <= n; i++)
                {
                    float u = (float)i / n;
                    ReadOnlySpan<float> bu = bU.Slice(i * 4, 4);

                    float x = 0, y = 0;
                    for (int row = 0; row < 4; row++)
                    {
                        int rowBase = row * 4;
                        float bvr = bv[row];
                        for (int col = 0; col < 4; col++)
                        {
                            // u indexes columns, v indexes rows.
                            float w = bu[col] * bvr;
                            SKPoint cp = p[rowBase + col];
                            x += cp.X * w;
                            y += cp.Y * w;
                        }
                    }

                    grid[j * (n + 1) + i] = new SKPoint(x, y);
                    gridCol[j * (n + 1) + i] = EvaluatePatchColor(shading, alpha, cornerColors, u, v, interpBuffer, tensorEvalBuffer, colorCache);
                }
            }

            DrawGridTriangles(grid, gridCol, n, paint);
        }

        /// <summary>
        /// Emits the n×n quad grid as a triangle list and submits it in a single DrawVertices call.
        /// The vertex arrays are allocated at exactly n²×6 (n is the adaptive subdivision count, so
        /// typically only a handful) — DrawVertices/SKVertices copy the whole array, so a fixed
        /// max-size buffer would record thousands of stale triangles and bloat the picture.
        /// </summary>
        private void DrawGridTriangles(SKPoint[] grid, SKColor[] gridCol, int n, SKPaint paint)
        {
            int vertexCount = n * n * 6;
            var positions = new SKPoint[vertexCount];
            var colors = new SKColor[vertexCount];
            EmitTrianglesFromGrid(grid, gridCol, n, positions, colors);
            _canvas.DrawVertices(SKVertexMode.Triangles, positions, null, colors,
                SKBlendMode.Modulate, null, paint);
        }

        /// <summary>
        /// Bilinear interpolation of corner colour components followed by Function evaluation
        /// (when present) and colour-space conversion. cornerColors index convention:
        /// [0] = (u=0, v=0), [1] = (u=1, v=0), [2] = (u=1, v=1), [3] = (u=0, v=1).
        /// <para>
        /// <paramref name="interpBuffer"/> must have length ≥ cornerColors[0].Length and is
        /// overwritten in place. The caller owns it so the per-grid-vertex allocation that
        /// would otherwise dominate this hot loop is moved to once-per-patch.
        /// </para>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SKColor EvaluatePatchColor(Shading shading, double alpha,
            double[][] cornerColors, float u, float v, double[] interpBuffer, Span<double> evalBuffer,
            ShadingColorCache? colorCache)
        {
            // Cache the four corner arrays once per call so the inner k-loop walks four
            // contiguous double[] strides rather than re-dereferencing cornerColors[...]
            // on every k. Called PatchSubdivisions² × patches times — small per-call win
            // adds up.
            double[] cc0 = cornerColors[0];
            double[] cc1 = cornerColors[1];
            double[] cc2 = cornerColors[2];
            double[] cc3 = cornerColors[3];
            int components = cc0.Length;
            float oneMinusU = 1f - u;
            float oneMinusV = 1f - v;
            double w00 = oneMinusU * oneMinusV;
            double w10 = u * oneMinusV;
            double w11 = u * v;
            double w01 = oneMinusU * v;
            for (int k = 0; k < components; k++)
            {
                interpBuffer[k] = w00 * cc0[k] + w10 * cc1[k] + w11 * cc2[k] + w01 * cc3[k];
            }

            // The cache pre-bakes shading.Eval + colour-space conversion; this is the hot path for
            // Separation/DeviceN meshes whose tint transform is an expensive PostScript function.
            if (colorCache is not null)
            {
                return colorCache.GetColor(new ReadOnlySpan<double>(interpBuffer, 0, components));
            }

            int written = shading.Eval(new ReadOnlySpan<double>(interpBuffer, 0, components), evalBuffer);
            return shading.ColorSpace.GetSKColor(evalBuffer.Slice(0, written), alpha);
        }

        /// <summary>
        /// Writes two triangles per grid cell into the supplied exact-size output arrays.
        /// Cell (i,j) connects vertices at (i, j), (i+1, j), (i, j+1), (i+1, j+1).
        /// The output arrays must have length ≥ n² × 6; the first n² × 6 entries are
        /// overwritten in scan order, matching the contract DrawVertices expects.
        /// </summary>
        private static void EmitTrianglesFromGrid(SKPoint[] grid, SKColor[] gridCol, int n,
            SKPoint[] outPositions, SKColor[] outColors)
        {
            int stride = n + 1;
            int w = 0;
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    int i00 = j * stride + i;
                    int i10 = i00 + 1;
                    int i01 = i00 + stride;
                    int i11 = i01 + 1;

                    outPositions[w] = grid[i00]; outColors[w] = gridCol[i00]; w++;
                    outPositions[w] = grid[i10]; outColors[w] = gridCol[i10]; w++;
                    outPositions[w] = grid[i01]; outColors[w] = gridCol[i01]; w++;

                    outPositions[w] = grid[i10]; outColors[w] = gridCol[i10]; w++;
                    outPositions[w] = grid[i11]; outColors[w] = gridCol[i11]; w++;
                    outPositions[w] = grid[i01]; outColors[w] = gridCol[i01]; w++;
                }
            }
        }

        /// <summary>
        /// Reads <paramref name="row"/>.Length consecutive vertex records (coordinates +
        /// decoded colour, byte-aligned per record) from a Type 5 lattice-form stream into
        /// <paramref name="row"/>. Returns false if the stream ends mid-record so the
        /// caller can stop without producing a partial row of triangles.
        /// </summary>
        private static bool TryReadLatticeRow(ref GouraudBitReader bitReader,
            (SKPoint pt, SKColor col)[] row,
            int bitsPerCoordinate, int bitsPerComponent,
            double maxCoordRaw, double maxColorRaw,
            double[] decode, int numStreamColorComponents,
            double xMin, double xMax, double yMin, double yMax,
            in SKMatrix patternTransformMatrix,
            Shading shading, CurrentGraphicsState currentState,
            double[] colorBuffer)
        {
            double alpha = currentState.AlphaConstantNonStroking;
            ColorSpaceDetails colorSpace = shading.ColorSpace;
            Span<double> evalOut = stackalloc double[ShadingEvalBufferSize];
            for (int c = 0; c < row.Length; c++)
            {
                if (!bitReader.HasData)
                {
                    return false;
                }

                long rawX, rawY;
                try
                {
                    rawX = bitReader.ReadBits(bitsPerCoordinate);
                    rawY = bitReader.ReadBits(bitsPerCoordinate);
                    for (int i = 0; i < numStreamColorComponents; i++)
                    {
                        long raw = bitReader.ReadBits(bitsPerComponent);
                        double cMin = decode[4 + i * 2];
                        double cMax = decode[5 + i * 2];
                        colorBuffer[i] = cMin + (raw / maxColorRaw) * (cMax - cMin);
                    }
                    bitReader.AlignToByte();
                }
                catch
                {
                    return false;
                }

                double x = xMin + (rawX / maxCoordRaw) * (xMax - xMin);
                double y = yMin + (rawY / maxCoordRaw) * (yMax - yMin);

                int written = shading.Eval(new ReadOnlySpan<double>(colorBuffer, 0, numStreamColorComponents), evalOut);
                SKColor skColor = colorSpace.GetSKColor(evalOut.Slice(0, written), alpha);
                SKPoint pt = MapPointAffine(in patternTransformMatrix, (float)x, (float)y);
                row[c] = (pt, skColor);
            }

            return true;
        }

        /// <summary>
        /// Emits 2·(verticesPerRow − 1) triangles for the row-pair (<paramref name="rowA"/>,
        /// <paramref name="rowB"/>) into the supplied exact-size output arrays.
        /// Cell c connects vertices at (rowA[c], rowA[c+1], rowB[c], rowB[c+1]).
        /// </summary>
        private static void EmitRowPairTriangles(
            (SKPoint pt, SKColor col)[] rowA,
            (SKPoint pt, SKColor col)[] rowB,
            int verticesPerRow,
            SKPoint[] outPositions, SKColor[] outColors)
        {
            int w = 0;
            for (int c = 0; c < verticesPerRow - 1; c++)
            {
                var v00 = rowA[c];
                var v10 = rowA[c + 1];
                var v01 = rowB[c];
                var v11 = rowB[c + 1];

                outPositions[w] = v00.pt; outColors[w] = v00.col; w++;
                outPositions[w] = v10.pt; outColors[w] = v10.col; w++;
                outPositions[w] = v01.pt; outColors[w] = v01.col; w++;

                outPositions[w] = v10.pt; outColors[w] = v10.col; w++;
                outPositions[w] = v11.pt; outColors[w] = v11.col; w++;
                outPositions[w] = v01.pt; outColors[w] = v01.col; w++;
            }
        }

        /// <summary>
        /// Evaluates the Tensor-product Bezier surface using precomputed Bernstein bases.
        /// <paramref name="p"/> is the 4×4 control grid stored row-major (16 entries,
        /// indexed as row * 4 + col where col is u and row is v).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SKPoint EvaluateTensorSurface(ReadOnlySpan<SKPoint> p, ReadOnlySpan<float> bU, ReadOnlySpan<float> bV)
        {
            float x = 0, y = 0;
            for (int row = 0; row < 4; row++)
            {
                int rowBase = row * 4;
                float bvr = bV[row];
                for (int col = 0; col < 4; col++)
                {
                    float w = bU[col] * bvr;
                    SKPoint cp = p[rowBase + col];
                    x += cp.X * w;
                    y += cp.Y * w;
                }
            }
            return new SKPoint(x, y);
        }

        /// <summary>
        /// Fills the 16-entry row-major 4×4 Tensor control grid in <paramref name="grid"/>
        /// from the 16 stream points, per the PDF spec / PDFBox layout (rows indexed by v,
        /// columns by u). Caller-owned buffer avoids the per-patch <c>new SKPoint[4,4]</c>
        /// allocation that the previous form paid on every patch in the mesh.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void BuildTensorControlGrid(ReadOnlySpan<SKPoint> tcp, Span<SKPoint> grid)
        {
            grid[0]  = tcp[0];  grid[1]  = tcp[1];  grid[2]  = tcp[2];  grid[3]  = tcp[3];
            grid[4]  = tcp[11]; grid[5]  = tcp[12]; grid[6]  = tcp[13]; grid[7]  = tcp[4];
            grid[8]  = tcp[10]; grid[9]  = tcp[15]; grid[10] = tcp[14]; grid[11] = tcp[5];
            grid[12] = tcp[9];  grid[13] = tcp[8];  grid[14] = tcp[7];  grid[15] = tcp[6];
        }

        /// <summary>
        /// Builds an SKBitmap of size <paramref name="texSize"/>² where each pixel holds the
        /// final SKColor for the patch at that (u,v). Each (u,v) pixel applies the bilinear
        /// corner-component blend, then the shading Function and colour-space conversion —
        /// so step-function and other non-linear outputs are sampled per pixel.
        /// <para>
        /// Pixel bytes are written directly into the bitmap's backing buffer (Rgba8888,
        /// unpremul, 1 byte per channel), avoiding a temporary <c>SKColor[texSize²]</c>
        /// staging array. The bilinear blend buffer is allocated once for the whole texture.
        /// </para>
        /// </summary>
        private static SKBitmap BuildPatchTexture(Shading shading, CurrentGraphicsState currentState,
            double[][] cornerColors, int texSize, ShadingColorCache? colorCache)
        {
            var bitmap = new SKBitmap(texSize, texSize, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            int components = cornerColors[0].Length;
            double[] interp = new double[components];
            float invDen = 1f / (texSize - 1);
            double alpha = currentState.AlphaConstantNonStroking;
            ColorSpaceDetails colorSpace = shading.ColorSpace;

            // Hoist the 4 corner component arrays out of the inner loop — index per slot
            // once, blend per k. Reads are sequential through cc0..cc3, friendlier to the
            // prefetcher than the previous cornerColors[0..3][k] pattern.
            double[] cc0 = cornerColors[0];
            double[] cc1 = cornerColors[1];
            double[] cc2 = cornerColors[2];
            double[] cc3 = cornerColors[3];

            Span<byte> pixelBytes = bitmap.GetPixelSpan();
            int rowStride = texSize * 4;

            // Fast path: the shading Function (if any) and the colour-space conversion — which for
            // a Separation/DeviceN colour space invokes a per-texel PostScript tint transform —
            // are pre-baked into colorCache, so this 262 K-iteration loop becomes a bilinear blend
            // plus a table lookup instead of a full evaluation per texel. The difference between
            // seconds and milliseconds for mesh shadings with many patches.
            if (colorCache is not null)
            {
                ReadOnlySpan<double> interpSpanFast = interp;
                for (int j = 0; j < texSize; j++)
                {
                    float v = j * invDen;
                    float oneMinusV = 1f - v;
                    int rowOffset = j * rowStride;
                    for (int i = 0; i < texSize; i++)
                    {
                        float u = i * invDen;
                        float oneMinusU = 1f - u;
                        double w00 = oneMinusU * oneMinusV;
                        double w10 = u * oneMinusV;
                        double w11 = u * v;
                        double w01 = oneMinusU * v;
                        for (int k = 0; k < components; k++)
                        {
                            interp[k] = w00 * cc0[k] + w10 * cc1[k] + w11 * cc2[k] + w01 * cc3[k];
                        }

                        SKColor c = colorCache.GetColor(interpSpanFast.Slice(0, components));

                        int idx = rowOffset + i * 4;
                        pixelBytes[idx] = c.Red;
                        pixelBytes[idx + 1] = c.Green;
                        pixelBytes[idx + 2] = c.Blue;
                        pixelBytes[idx + 3] = c.Alpha;
                    }
                }

                return bitmap;
            }

            // Per-pixel Eval buffer — keeps the 262 K-iteration inner loop allocation-free.
            Span<double> patchEvalOut = stackalloc double[ShadingEvalBufferSize];
            ReadOnlySpan<double> interpSpan = interp;

            for (int j = 0; j < texSize; j++)
            {
                float v = j * invDen;
                float oneMinusV = 1f - v;
                int rowOffset = j * rowStride;
                for (int i = 0; i < texSize; i++)
                {
                    float u = i * invDen;
                    float oneMinusU = 1f - u;

                    double w00 = oneMinusU * oneMinusV;
                    double w10 = u * oneMinusV;
                    double w11 = u * v;
                    double w01 = oneMinusU * v;
                    for (int k = 0; k < components; k++)
                    {
                        interp[k] = w00 * cc0[k] + w10 * cc1[k] + w11 * cc2[k] + w01 * cc3[k];
                    }

                    int written = shading.Eval(interpSpan, patchEvalOut);
                    SKColor c = colorSpace.GetSKColor(patchEvalOut.Slice(0, written), alpha);

                    int idx = rowOffset + i * 4;
                    pixelBytes[idx] = c.Red;
                    pixelBytes[idx + 1] = c.Green;
                    pixelBytes[idx + 2] = c.Blue;
                    pixelBytes[idx + 3] = c.Alpha;
                }
            }

            return bitmap;
        }

        /// <summary>
        /// A precomputed N-D lookup table that maps a mesh-shading vertex's interpolated stream
        /// colour components to a final <see cref="SKColor"/>, with the shading Function (if any)
        /// and the colour-space conversion already applied.
        /// <para>
        /// Mesh shadings (PDF 1.7 §8.7.4.5.5–.7) evaluate, for every tessellated vertex/texel, the
        /// shading Function and then the colour space. When the colour space is a Separation or
        /// DeviceN with a PostScript (Type 4) tint transform, that per-vertex evaluation dominates
        /// rendering time — a single tensor mesh can ask for tens of millions of evaluations. The
        /// inputs are low-dimensional (1 component for the function/Separation case, 2–4 for
        /// DeviceN), so sampling the whole pipeline once onto a grid and looking it up per vertex
        /// turns minutes into milliseconds. Nearest-neighbour sampling is sufficient: the patch is
        /// tessellated densely and the GPU interpolates colours between grid vertices anyway.
        /// </para>
        /// </summary>
        private sealed class ShadingColorCache
        {
            private readonly SKColor[] _table;
            private readonly double[] _lo;
            private readonly double[] _scale; // per-dim (sizePerDim - 1) / (hi - lo)
            private readonly int[] _stride;
            private readonly int _dims;
            private readonly int _sizePerDim;

            private ShadingColorCache(SKColor[] table, double[] lo, double[] scale, int[] stride,
                int dims, int sizePerDim)
            {
                _table = table;
                _lo = lo;
                _scale = scale;
                _stride = stride;
                _dims = dims;
                _sizePerDim = sizePerDim;
            }

            /// <summary>
            /// Builds a cache for a mesh shading, or returns <see langword="null"/> when caching is
            /// not worthwhile/possible (unsupported component count or malformed Decode) so the
            /// caller keeps evaluating per vertex.
            /// </summary>
            public static ShadingColorCache? TryBuild(Shading shading, double[] decode,
                int numComponents, double alpha)
            {
                // Only worth caching — and only quantisation-safe — when per-vertex colour
                // resolution is the bottleneck. That happens when a PostScript/stitching tint
                // transform runs per vertex: either the shading carries its own Function, or its
                // colour space is a Separation/DeviceN (whose conversion invokes a tint-transform
                // function). For plain Device colour spaces the conversion is cheap arithmetic, so
                // a table would only add visible banding. Restrict to 1–2 inputs as well, where a
                // 1024-/256-entry-per-axis table is finer than the 8-bit output it feeds.
                bool expensiveColorSpace = shading.ColorSpace is SeparationColorSpaceDetails
                    or DeviceNColorSpaceDetails;
                bool hasShadingFunction = shading.Functions is { Length: > 0 };
                if (!expensiveColorSpace && !hasShadingFunction)
                {
                    return null;
                }

                if (numComponents < 1 || numComponents > 2 || decode.Length < 4 + 2 * numComponents)
                {
                    return null;
                }

                // Per-dimension resolution: fine enough for smooth gradients, cheap to populate
                // once per shading (≤64 K entries).
                int sizePerDim = numComponents == 1 ? 1024 : 256;

                var lo = new double[numComponents];
                var scale = new double[numComponents];
                var stride = new int[numComponents];
                int total = 1;
                for (int d = 0; d < numComponents; d++)
                {
                    double cLo = decode[4 + d * 2];
                    double cHi = decode[5 + d * 2];
                    if (double.IsNaN(cLo) || double.IsNaN(cHi))
                    {
                        return null;
                    }

                    lo[d] = cLo;
                    double span = cHi - cLo;
                    scale[d] = Math.Abs(span) < 1e-12 ? 0.0 : (sizePerDim - 1) / span;
                    stride[d] = total;
                    total *= sizePerDim;
                }

                var table = new SKColor[total];
                ColorSpaceDetails colorSpace = shading.ColorSpace;
                Span<double> evalOut = stackalloc double[ShadingEvalBufferSize];
                Span<double> input = stackalloc double[4];
                Span<int> counter = stackalloc int[4];
                counter.Clear();
                double invDen = sizePerDim > 1 ? 1.0 / (sizePerDim - 1) : 0.0;

                for (int flat = 0; flat < total; flat++)
                {
                    for (int d = 0; d < numComponents; d++)
                    {
                        double cLo = decode[4 + d * 2];
                        double cHi = decode[5 + d * 2];
                        input[d] = cLo + (cHi - cLo) * (counter[d] * invDen);
                    }

                    int written = shading.Eval(input.Slice(0, numComponents), evalOut);
                    table[flat] = colorSpace.GetSKColor(evalOut.Slice(0, written), alpha);

                    // Increment the mixed-radix counter (dimension 0 is the fastest-varying, matching
                    // stride[0] == 1 so `flat` and `counter` stay in lock-step).
                    for (int d = 0; d < numComponents; d++)
                    {
                        if (++counter[d] < sizePerDim)
                        {
                            break;
                        }
                        counter[d] = 0;
                    }
                }

                return new ShadingColorCache(table, lo, scale, stride, numComponents, sizePerDim);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public SKColor GetColor(ReadOnlySpan<double> components)
            {
                int idx = 0;
                int last = _sizePerDim - 1;
                for (int d = 0; d < _dims; d++)
                {
                    int i = (int)((components[d] - _lo[d]) * _scale[d] + 0.5);
                    if (i < 0)
                    {
                        i = 0;
                    }
                    else if (i > last)
                    {
                        i = last;
                    }

                    idx += i * _stride[d];
                }

                return _table[idx];
            }
        }

        /// <summary>
        /// Draws a Coons patch via texture mapping: builds a per-pixel-evaluated colour bitmap,
        /// triangulates the patch surface with texture coordinates, and lets Skia sample the
        /// bitmap at every output pixel. This gives correct step-function / stitched-Type-3
        /// rendering that vertex-colour Gouraud cannot.
        /// </summary>
        private void DrawCoonsPatchTextured(Shading shading, CurrentGraphicsState currentState,
            ReadOnlySpan<SKPoint> pts, double[][] cornerColors, ShadingColorCache? colorCache)
        {
            using var bitmap = BuildPatchTexture(shading, currentState, cornerColors, PatchTextureSize, colorCache);

            const int gridLen = (PatchSubdivisions + 1) * (PatchSubdivisions + 1);
            const int triVertexCount = PatchSubdivisions * PatchSubdivisions * 6;

            // The (n+1)² grid arrays are scratch — rent from the shared pool to avoid the
            // ~17 KB heap allocation per patch. Triangle arrays are passed straight to
            // DrawVertices which uses Array.Length as the vertex count, so they must be
            // allocated to the exact size and cannot be pooled.
            var pool = ArrayPool<SKPoint>.Shared;
            SKPoint[] positions = pool.Rent(gridLen);
            SKPoint[] texCoords = pool.Rent(gridLen);
            try
            {
                const int stride = PatchSubdivisions + 1;
                const float texScale = PatchTextureSize - 1;

                // See TessellateAndDrawCoonsPatch for the per-axis Bezier precompute rationale.
                const int axisLen = PatchSubdivisions + 1;
                Span<SKPoint> sBottom = stackalloc SKPoint[axisLen];
                Span<SKPoint> sTop = stackalloc SKPoint[axisLen];
                Span<SKPoint> sLeft = stackalloc SKPoint[axisLen];
                Span<SKPoint> sRight = stackalloc SKPoint[axisLen];

                SKPoint p0 = pts[0], p1 = pts[1], p2 = pts[2], p3 = pts[3];
                SKPoint p4 = pts[4], p5 = pts[5], p6 = pts[6], p7 = pts[7];
                SKPoint p8 = pts[8], p9 = pts[9], p10 = pts[10], p11 = pts[11];

                const float invN = 1f / PatchSubdivisions;
                for (int i2 = 0; i2 < axisLen; i2++)
                {
                    float u = i2 * invN;
                    sBottom[i2] = CubicBezier(p0, p1, p2, p3, u);
                    sTop[i2] = CubicBezier(p9, p8, p7, p6, u);
                }
                for (int j2 = 0; j2 < axisLen; j2++)
                {
                    float v = j2 * invN;
                    sLeft[j2] = CubicBezier(p0, p11, p10, p9, v);
                    sRight[j2] = CubicBezier(p3, p4, p5, p6, v);
                }

                float p00x = p0.X, p00y = p0.Y;
                float p10x = p3.X, p10y = p3.Y;
                float p11x = p6.X, p11y = p6.Y;
                float p01x = p9.X, p01y = p9.Y;

                for (int j = 0; j < axisLen; j++)
                {
                    float v = j * invN;
                    float oneMinusV = 1f - v;
                    SKPoint sLj = sLeft[j];
                    SKPoint sRj = sRight[j];
                    int rowOffset = j * stride;
                    for (int i = 0; i < axisLen; i++)
                    {
                        float u = i * invN;
                        float oneMinusU = 1f - u;
                        SKPoint sBi = sBottom[i];
                        SKPoint sTi = sTop[i];

                        float x = oneMinusV * sBi.X + v * sTi.X
                                  + oneMinusU * sLj.X + u * sRj.X
                                  - oneMinusU * oneMinusV * p00x - u * oneMinusV * p10x
                                  - u * v * p11x - oneMinusU * v * p01x;
                        float y = oneMinusV * sBi.Y + v * sTi.Y
                                  + oneMinusU * sLj.Y + u * sRj.Y
                                  - oneMinusU * oneMinusV * p00y - u * oneMinusV * p10y
                                  - u * v * p11y - oneMinusU * v * p01y;

                        int idx = rowOffset + i;
                        positions[idx] = new SKPoint(x, y);
                        texCoords[idx] = new SKPoint(u * texScale, v * texScale);
                    }
                }

                var posArray = new SKPoint[triVertexCount];
                var texArray = new SKPoint[triVertexCount];
                BuildPatchTriangleArrays(positions, texCoords, PatchSubdivisions, posArray, texArray);
                DrawTexturedPatchVertices(shading, currentState, bitmap, posArray, texArray);
            }
            finally
            {
                pool.Return(positions);
                pool.Return(texCoords);
            }
        }

        /// <summary>
        /// Draws a Tensor-product patch via texture mapping. See <see cref="DrawCoonsPatchTextured"/>.
        /// </summary>
        private void DrawTensorPatchTextured(Shading shading, CurrentGraphicsState currentState,
            ReadOnlySpan<SKPoint> tcp, double[][] cornerColors, ShadingColorCache? colorCache)
        {
            using SKBitmap bitmap = BuildPatchTexture(shading, currentState, cornerColors, PatchTextureSize, colorCache);

            // Row-major 4×4 control grid lives on the stack — saves the heap allocation the
            // SKPoint[,] form paid per patch.
            Span<SKPoint> p = stackalloc SKPoint[16];
            BuildTensorControlGrid(tcp, p);

            const int gridLen = (PatchSubdivisions + 1) * (PatchSubdivisions + 1);
            const int triVertexCount = PatchSubdivisions * PatchSubdivisions * 6;

            var pool = ArrayPool<SKPoint>.Shared;
            SKPoint[] positions = pool.Rent(gridLen);
            SKPoint[] texCoords = pool.Rent(gridLen);

            try
            {
                const int stride = PatchSubdivisions + 1;
                const float texScale = PatchTextureSize - 1;

                // Flat 4×(n+1) Bernstein tables stack-allocated — no per-row jagged-array
                // allocations the previous float[][] form required.
                const int bCount = 4 * (PatchSubdivisions + 1);
                Span<float> bU = stackalloc float[bCount];
                Span<float> bV = stackalloc float[bCount];

                for (int i = 0; i <= PatchSubdivisions; i++)
                {
                    BernsteinCubic((float)i / PatchSubdivisions, bU.Slice(i * 4, 4));
                }

                for (int j = 0; j <= PatchSubdivisions; j++)
                {
                    BernsteinCubic((float)j / PatchSubdivisions, bV.Slice(j * 4, 4));
                }

                for (int j = 0; j <= PatchSubdivisions; j++)
                {
                    float v = (float)j / PatchSubdivisions;
                    ReadOnlySpan<float> bv = bV.Slice(j * 4, 4);
                    for (int i = 0; i <= PatchSubdivisions; i++)
                    {
                        float u = (float)i / PatchSubdivisions;
                        ReadOnlySpan<float> bu = bU.Slice(i * 4, 4);
                        int idx = j * stride + i;
                        positions[idx] = EvaluateTensorSurface(p, bu, bv);
                        texCoords[idx] = new SKPoint(u * texScale, v * texScale);
                    }
                }

                var posArray = new SKPoint[triVertexCount];
                var texArray = new SKPoint[triVertexCount];
                BuildPatchTriangleArrays(positions, texCoords, PatchSubdivisions, posArray, texArray);
                DrawTexturedPatchVertices(shading, currentState, bitmap, posArray, texArray);
            }
            finally
            {
                pool.Return(positions);
                pool.Return(texCoords);
            }
        }

        /// <summary>
        /// Expands the (n+1)² grid of <paramref name="positions"/> / <paramref name="texCoords"/>
        /// into flat triangle vertex arrays — two triangles per cell, three vertices each.
        /// </summary>
        private static void BuildPatchTriangleArrays(SKPoint[] positions, SKPoint[] texCoords, int n,
            SKPoint[] posArray, SKPoint[] texArray)
        {
            int stride = n + 1;
            int t = 0;
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    int i00 = j * stride + i;
                    int i10 = i00 + 1;
                    int i01 = i00 + stride;
                    int i11 = i01 + 1;

                    posArray[t] = positions[i00]; texArray[t++] = texCoords[i00];
                    posArray[t] = positions[i10]; texArray[t++] = texCoords[i10];
                    posArray[t] = positions[i01]; texArray[t++] = texCoords[i01];

                    posArray[t] = positions[i10]; texArray[t++] = texCoords[i10];
                    posArray[t] = positions[i11]; texArray[t++] = texCoords[i11];
                    posArray[t] = positions[i01]; texArray[t++] = texCoords[i01];
                }
            }
        }

        /// <summary>
        /// Submits the texture-mapped triangle list to the canvas with a nearest-neighbour
        /// bitmap shader. Nearest sampling preserves sharp step-function transitions stored
        /// in the colour texture (linear filtering would smear them into a multi-pixel band).
        /// </summary>
        private void DrawTexturedPatchVertices(Shading shading, CurrentGraphicsState currentState,
            SKBitmap bitmap, SKPoint[] posArray, SKPoint[] texArray)
        {
            using var image = SKImage.FromBitmap(bitmap);
            var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
            using var shader = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling);
            using var paint = new SKPaint();
            paint.Shader = shader;
            paint.IsAntialias = shading.AntiAlias;
            paint.BlendMode = currentState.BlendMode.ToSKBlendMode();

            _canvas.DrawVertices(SKVertexMode.Triangles, posArray, texArray, null,
                SKBlendMode.SrcOver, null, paint);
        }

        /// <summary>De Casteljau evaluation of a cubic Bézier curve at parameter <paramref name="t"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SKPoint CubicBezier(SKPoint p0, SKPoint p1, SKPoint p2, SKPoint p3, float t)
        {
            float u = 1 - t;
            float uu = u * u;
            float tt = t * t;
            float w0 = uu * u;
            float w1 = 3 * uu * t;
            float w2 = 3 * u * tt;
            float w3 = tt * t;
            return new SKPoint(
                w0 * p0.X + w1 * p1.X + w2 * p2.X + w3 * p3.X,
                w0 * p0.Y + w1 * p1.Y + w2 * p2.Y + w3 * p3.Y);
        }

        /// <summary>
        /// Writes the four cubic Bernstein basis values [B0(t), B1(t), B2(t), B3(t)] into
        /// <paramref name="result"/> (must have at least 4 elements). Span output avoids the
        /// per-call allocation that would otherwise dominate the inner Tensor sampling loop.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void BernsteinCubic(float t, Span<float> result)
        {
            float u = 1 - t;
            result[0] = u * u * u;
            result[1] = 3 * u * u * t;
            result[2] = 3 * u * t * t;
            result[3] = t * t * t;
        }

        /// <summary>
        /// Reads a packed bit-stream MSB-first, as required by PDF Type 4–7 shading vertex data.
        /// Each vertex record is padded to a whole number of bytes (<see cref="AlignToByte"/>).
        /// </summary>
        private ref struct GouraudBitReader
        {
            private readonly ReadOnlySpan<byte> _data;
            private int _bytePos;
            private int _bitPos; // 7 = MSB of current byte, 0 = LSB

            public GouraudBitReader(ReadOnlySpan<byte> data)
            {
                _data = data;
                _bytePos = 0;
                _bitPos = 7;
            }

            /// <summary>Returns <see langword="true"/> when there is at least one more byte to read.</summary>
            public readonly bool HasData
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => _bytePos < _data.Length;
            }

            /// <summary>
            /// Reads <paramref name="count"/> bits and returns them as a non-negative <see cref="long"/>, MSB first.
            /// <para>
            /// Pulls whole-byte chunks where possible rather than walking one bit at a time —
            /// shading streams routinely ask for 8/16/24 bits per field, so the per-bit loop
            /// was paying eight loop iterations and eight bounds-checks where one byte read
            /// would do. <paramref name="count"/> is bounded by the shading's BitsPerCoordinate
            /// (≤ 32 per PDF spec), well under the 63-bit ceiling implied by the shift below.
            /// </para>
            /// </summary>
            public long ReadBits(int count)
            {
                long result = 0;
                while (count > 0)
                {
                    if (_bytePos >= _data.Length)
                    {
                        throw new InvalidOperationException("Unexpected end of shading stream.");
                    }

                    int available = _bitPos + 1; // bits still left in current byte starting at _bitPos
                    int take = count < available ? count : available;
                    int shift = available - take;
                    int mask = (1 << take) - 1;
                    int bits = (_data[_bytePos] >> shift) & mask;
                    result = (result << take) | (uint)bits;
                    count -= take;

                    if (shift == 0)
                    {
                        _bytePos++;
                        _bitPos = 7;
                    }
                    else
                    {
                        _bitPos -= take;
                    }
                }
                return result;
            }

            /// <summary>
            /// Advances the read position to the start of the next byte,
            /// discarding any remaining bits in the current byte.
            /// No-op when already at a byte boundary.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void AlignToByte()
            {
                if (_bitPos != 7)
                {
                    _bitPos = 7;
                    _bytePos++;
                }
            }
        }

        private void RenderTilingPattern(SKPath path, TilingPatternColor pattern, bool isStroke)
        {
            // See:
            // - 22060_A1_01_Plans-1.pdf
            // - Apitron.PDF.Kit.Samples_patternFill.pdf

            // For uncoloured tiling pattern, see:
            // - 2_uncolor_tiling.pdf
            // - gs-bugzilla694385.pdf

            var operations = PageContentParser.Parse(PageNumber, new MemoryInputBytes(pattern.Data), ParsingOptions.Logger);
            bool hasResources = pattern.PatternStream.StreamDictionary.TryGet(NameToken.Resources, PdfScanner, out DictionaryToken? resourcesDictionary);

            if (hasResources)
            {
                ResourceStore.LoadResourceDictionary(resourcesDictionary!);
            }

            try
            {
                TransformationMatrix initialMatrix = pattern.GetTilingPatterInitialMatrix();

                var processor = new SkiaStreamProcessor(PageNumber, ResourceStore, PdfScanner, PageContentParser,
                    FilterProvider, new CropBox(pattern.BBox), UserSpaceUnit, Rotation,
                    initialMatrix, ParsingOptions, null, _fontCache, _token);

                if (pattern.PaintType == PatternPaintType.Uncoloured)
                {
                    // For uncoloured tiling patterns, the colour to paint with is supplied as
                    // operands to the SCN/scn operator alongside the pattern name. Resolve those
                    // operands against the underlying color space and seed the sub-processor's
                    // current colours so the pattern's content stream paints in the right colour.
                    IColor? color = GetUncolouredPatternColor(isStroke);
                    if (color is not null)
                    {
                        var subState = processor.GetCurrentState();
                        subState.CurrentStrokingColor = color;
                        subState.CurrentNonStrokingColor = color;
                    }
                }

                // Installs the graphics state that was in effect at the beginning of the pattern’s parent content stream,
                // with the current transformation matrix altered by the pattern matrix as described in 8.7.2, "General properties of patterns"
                float xStep = Math.Abs((float)pattern.XStep);
                float yStep = Math.Abs((float)pattern.YStep);
                SKRect rect = SKRect.Create(xStep, yStep);
                SKMatrix transformMatrix = CurrentTransformationMatrix.ToSkMatrix().Invert()
                    .PreConcat(_currentStreamOriginalTransforms.Peek())
                    .PreConcat(pattern.GetTilingPatterAdjMatrix());

                using (var picture = processor.Process(PageNumber, operations))
                {
                    // Fast path for patterns that do not actually repeat within the region being
                    // filled. Producers commonly use a very large XStep/YStep (e.g. 99999) to mean
                    // "paint the cell once". Handing such a tile to SKShader.CreatePicture makes Skia
                    // rasterise a gigantic, almost-empty tile that it then clamps to a maximum size,
                    // collapsing the real content (which only occupies the BBox corner of the tile)
                    // to a handful of pixels — the cell, typically a full-page image, renders badly
                    // blurred. Drawing the cell picture directly, clipped to the path, keeps it at
                    // full output resolution.
                    if (TryDrawNonRepeatingTilingPattern(path, picture, in transformMatrix, xStep, yStep))
                    {
                        return;
                    }

                    using (var shader = SKShader.CreatePicture(picture, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, SKFilterMode.Linear, transformMatrix, rect))
                    using (var paint = new SKPaint())
                    {
                        paint.IsAntialias = _antiAliasing;
                        paint.Shader = shader;
                        paint.BlendMode = GetCurrentState().BlendMode.ToSKBlendMode();
                        _canvas.DrawPath(path, paint);
                    }
                }
            }
            finally
            {
                if (hasResources)
                {
                    ResourceStore.UnloadResourceDictionary();
                }
            }
        }

        /// <summary>
        /// Draws a tiling pattern that does not repeat within the region being filled by rendering
        /// its cell picture a single time, clipped to <paramref name="path"/>, at full output
        /// resolution. Returns <see langword="false"/> (drawing nothing) when the pattern does
        /// repeat across the filled region and must therefore go through the picture shader.
        /// </summary>
        private bool TryDrawNonRepeatingTilingPattern(SKPath path, SKPicture picture,
            in SKMatrix transformMatrix, float xStep, float yStep)
        {
            const float epsilon = 1e-3f;

            // Degenerate step: cannot reason about repetition, let the shader handle it.
            if (xStep <= epsilon || yStep <= epsilon)
            {
                return false;
            }

            // transformMatrix maps picture/tile space → canvas-local (page) space; invert it to
            // express the filled region's bounds in tile space.
            if (!transformMatrix.TryInvert(out SKMatrix inverse))
            {
                return false;
            }

            SKRect tileSpaceBounds = inverse.MapRect(path.Bounds);

            // The cell repeats every (xStep, yStep) in tile space. Only take the direct path when
            // the whole filled region falls inside a single period window in both axes; otherwise
            // more than one cell could be visible and the shader must tile it.
            double nxLeft = Math.Floor((tileSpaceBounds.Left + epsilon) / xStep);
            double nxRight = Math.Floor((tileSpaceBounds.Right - epsilon) / xStep);
            double nyTop = Math.Floor((tileSpaceBounds.Top + epsilon) / yStep);
            double nyBottom = Math.Floor((tileSpaceBounds.Bottom - epsilon) / yStep);

            if (nxLeft != nxRight || nyTop != nyBottom)
            {
                return false;
            }

            // Position the single cell into the period window the region lives in (usually 0,0).
            SKMatrix drawMatrix = transformMatrix.PreConcat(
                SKMatrix.CreateTranslation((float)(nxLeft * xStep), (float)(nyTop * yStep)));

            SKBlendMode blendMode = GetCurrentState().BlendMode.ToSKBlendMode();

            using (new SKAutoCanvasRestore(_canvas, true))
            {
                _canvas.ClipPath(path, SKClipOperation.Intersect, _antiAliasing);
                _canvas.Concat(in drawMatrix);

                if (blendMode == SKBlendMode.SrcOver)
                {
                    _canvas.DrawPicture(picture);
                }
                else
                {
                    using var paint = new SKPaint { BlendMode = blendMode };
                    _canvas.DrawPicture(picture, paint);
                }
            }

            return true;
        }

        private IColor? GetUncolouredPatternColor(bool isStroke)
        {
            var parentState = GetCurrentState();

            if (parentState.ColorSpaceContext is not PatternAwareColorSpaceContext parentContext)
            {
                return null;
            }

            PatternColorSpaceDetails? patternCs;
            IReadOnlyList<double>? operands;

            if (isStroke)
            {
                patternCs = parentContext.CurrentStrokingColorSpace as PatternColorSpaceDetails;
                operands = parentContext.LastStrokingPatternOperands;
            }
            else
            {
                patternCs = parentContext.CurrentNonStrokingColorSpace as PatternColorSpaceDetails;
                operands = parentContext.LastNonStrokingPatternOperands;
            }

            ColorSpaceDetails? underlying = patternCs?.UnderlyingColourSpace;
            if (underlying is null || underlying is UnsupportedColorSpaceDetails)
            {
                return null;
            }

            double[] components = operands?.ToArray() ?? Array.Empty<double>();
            if (components.Length == 0)
            {
                return underlying.GetInitializeColor();
            }

            return underlying.GetColor(components);
        }
    }
}
