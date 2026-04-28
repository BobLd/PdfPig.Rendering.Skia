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
using System.IO;
using System.Linq;
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

            float maxX = _canvas.DeviceClipBounds.Right;
            float maxY = _canvas.DeviceClipBounds.Top;
            float minX = _canvas.DeviceClipBounds.Left;
            float minY = _canvas.DeviceClipBounds.Bottom;

            switch (shading.ShadingType)
            {
                case ShadingType.Axial:
                    RenderAxialShading(shading as AxialShading, in SKMatrix.Identity, minX, minY, maxX, maxY);
                    break;

                case ShadingType.Radial:
                    RenderRadialShading(shading as RadialShading, in SKMatrix.Identity, minX, minY, maxX, maxY);
                    break;

                case ShadingType.FunctionBased:
                    RenderFunctionBasedShading(shading as FunctionBasedShading, in SKMatrix.Identity, minX, minY, maxX, maxY);
                    break;

                case ShadingType.FreeFormGouraud:
                    RenderFreeFormGouraudShading(shading as FreeFormGouraudShading, in SKMatrix.Identity, minX, minY, maxX, maxY);
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
        private static void FixIncorrectValues(double[] v, double[] domain)
        {
            for (int i = 0; i < v.Length; i++)
            {
                ref double c = ref v[i];
                if (double.IsNaN(c) || double.IsInfinity(c))
                {
                    c = domain[0];
                }
            }
        }

        private void RenderRadialShading(RadialShading shading, in SKMatrix patternTransformMatrix, float minX,
            float minY, float maxX, float maxY,
            bool isStroke = false, SKPath? path = null)
        {
            var currentState = GetCurrentState();

            // Not correct
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

            // worst case for the number of steps is opposite diagonal corners, so use that
            float dist = (float)Math.Sqrt(Math.Pow(maxX - minX, 2) + Math.Pow(maxY - minY, 2));
            int factor = Math.Max(10, (int)Math.Ceiling(dist / 10.0f)); // too much?
            var colors = new SKColor[factor + 1];
            float[] colorPos = new float[factor + 1];

            for (int t = 0; t <= factor; t++)
            {
                // See RenderAxialShading for the rationale of these two computations:
                //   - tx walks the user-supplied Domain (correct when t0 ≠ 0)
                //   - colorPos must be in [0,1] for Skia's gradient shader
                double frac = t / (double)factor;
                double tx = t0 + frac * (t1 - t0);
                double[] v = shading.Eval(tx);

                FixIncorrectValues(v, domain); // This is a hack, this should never happen

                colors[t] = shading.ColorSpace.GetColor(v).ToSKColor(currentState.AlphaConstantNonStroking);
                // TODO - is it non stroking??
                colorPos[t] = (float)frac;
            }

            if (shading.BBox.HasValue)
            {
                // TODO
            }

            if (shading.Background is not null)
            {
                // TODO
            }

            using (var shader = SKShader.CreateTwoPointConicalGradient(new SKPoint(x0, y0), r0, new SKPoint(x1, y1), r1,
                       colors, colorPos, SKShaderTileMode.Clamp, patternTransformMatrix))
            using (var paint = new SKPaint())
            {
                paint.IsAntialias = shading.AntiAlias;
                paint.Shader = shader;
                paint.BlendMode = currentState.BlendMode.ToSKBlendMode();

                // check if bbox not null

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

        private void RenderAxialShading(AxialShading shading, in SKMatrix patternTransformMatrix, float minX, float minY, float maxX, float maxY,
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

            // worst case for the number of steps is opposite diagonal corners, so use that
            float dist = (float)Math.Sqrt(Math.Pow(maxX - minX, 2) + Math.Pow(maxY - minY, 2));
            int factor = Math.Max(10, (int)Math.Ceiling(dist / 10.0f)); // too much? - Min of 10
            var colors = new SKColor[factor + 1];
            float[] colorPos = new float[factor + 1];

            for (int t = 0; t <= factor; t++)
            {
                // Sample the parametric variable across the user-supplied Domain (NOT 0..t1
                // — the previous form silently broke whenever t0 ≠ 0).
                double frac = t / (double)factor;
                double tx = t0 + frac * (t1 - t0);
                double[] v = shading.Eval(tx);

                FixIncorrectValues(v, domain); // This is a hack, this should never happen, see GHOSTSCRIPT-693154-0

                colors[t] = shading.ColorSpace.GetColor(v).ToSKColor(currentState.AlphaConstantNonStroking); // TODO - is it non stroking??
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
            float minX, float minY, float maxX, float maxY, bool isStroke = false, SKPath? path = null)
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

            // Storage for the triangles that will be passed to DrawVertices.
            // Each triangle contributes exactly 3 entries (one per corner).
            var positions = new List<SKPoint>();
            var colors = new List<SKColor>();

            // Reusable scratch buffer for the barycentric component blend inside
            // EmitGouraudTriangle's subdivision loop. Allocated once here instead of per
            // sub-vertex (153 sub-vertices × N triangles can otherwise add up).
            double[] emitBuffer = hasFunction ? new double[numStreamColorComponents] : Array.Empty<double>();

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

            while (bitReader.HasData)
            {
                int flag;
                long rawX, rawY;

                // colorComponents is the per-vertex output (stored on the GouraudVertex), so
                // it must live on the heap. Read directly into it — no separate rawC stage.
                double[] colorComponents = new double[numStreamColorComponents];

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
                double[] evalResult = shading.Eval(colorComponents);
                SKColor skColor = shading.ColorSpace.GetColor(evalResult).ToSKColor(currentState.AlphaConstantNonStroking);

                // Transform the vertex from shading/pattern space to canvas space.
                SKPoint pt = patternTransformMatrix.MapPoint(new SKPoint((float)x, (float)y));
                var vertex = new GouraudVertex(pt, colorComponents, skColor);

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
                                flag0Buf[0], flag0Buf[1], flag0Buf[2], emitBuffer, positions, colors);
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
                                prevTri[1], prevTri[2], vertex, emitBuffer, positions, colors);

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
                                prevTri[0], prevTri[2], vertex, emitBuffer, positions, colors);

                            // Slide the window: new prevTri = [prevTri[0], prevTri[2], newVertex]
                            prevTri[1] = prevTri[2];
                            prevTri[2] = vertex;
                            flag0Count = 0;
                        }
                        break;
                }
            }

            if (positions.Count == 0)
            {
                return;
            }

            SKPoint[] posArray = positions.ToArray();
            SKColor[] colArray = colors.ToArray();

            using var paint = new SKPaint();
            paint.IsAntialias = shading.AntiAlias;
            paint.BlendMode = currentState.BlendMode.ToSKBlendMode();
            // White paint + Modulate: vertex_colour × (1,1,1,1) = vertex_colour.
            // This preserves the Gouraud-interpolated colours regardless of which role
            // (src / dst) SkiaSharp assigns to the vertex vs. paint colour.
            paint.Color = SKColors.White;

            if (path is not null)
            {
                _canvas.Save();
                _canvas.ClipPath(path);
            }

            // paint.BlendMode handles compositing the result with the existing canvas content.
            _canvas.DrawVertices(SKVertexMode.Triangles, posArray, null, colArray, SKBlendMode.Modulate, null, paint);

            if (path is not null)
            {
                _canvas.Restore();
            }
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
        /// Emits one Gouraud triangle into the position/colour lists, subdividing it when
        /// <paramref name="hasFunction"/> is true so per-pixel function output is visible.
        /// </summary>
        /// <param name="interpBuffer">
        /// Reusable buffer for the barycentric component blend; must have length ≥
        /// <c>a.Components.Length</c>. Owned by the caller so the per-sub-vertex allocation
        /// doesn't fall inside the n²/2 subdivision loop.
        /// </param>
        private static void EmitGouraudTriangle(FreeFormGouraudShading shading, CurrentGraphicsState currentState,
            bool hasFunction, in GouraudVertex a, in GouraudVertex b, in GouraudVertex c,
            double[] interpBuffer, List<SKPoint> outPositions, List<SKColor> outColors)
        {
            if (!hasFunction)
            {
                outPositions.Add(a.Pt); outPositions.Add(b.Pt); outPositions.Add(c.Pt);
                outColors.Add(a.Col); outColors.Add(b.Col); outColors.Add(c.Col);
                return;
            }

            int components = a.Components.Length;
            double alpha = currentState.AlphaConstantNonStroking;

            float ax = a.Pt.X, ay = a.Pt.Y;
            float bx = b.Pt.X, by = b.Pt.Y;
            float cx = c.Pt.X, cy = c.Pt.Y;

            const float invN = 1f / GouraudFunctionSubdivisions;

            // (n+1)*(n+2)/2 sub-vertices on a barycentric grid (i + j ≤ n, with k = n − i − j).
            // At n=128 that's 8385 SKPoints + 8385 SKColors per parent triangle. SKPoint is 8 B
            // (~67 KB) and SKColor 4 B (~33 KB); both rented from the shared pool to keep heap
            // pressure flat regardless of how many Type-4 triangles a shading produces.
            int subVertCount = (GouraudFunctionSubdivisions + 1) * (GouraudFunctionSubdivisions + 2) / 2;
            var ptsPool = ArrayPool<SKPoint>.Shared;
            var colsPool = ArrayPool<SKColor>.Shared;
            SKPoint[] subPts = ptsPool.Rent(subVertCount);
            SKColor[] subCols = colsPool.Rent(subVertCount);

            try
            {
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

                        double[] eval = shading.Eval(interpBuffer);
                        SKColor col = shading.ColorSpace.GetColor(eval).ToSKColor(alpha);

                        int idx = rowOffset + j;
                        subPts[idx] = pt;
                        subCols[idx] = col;
                    }
                }

                // Stitch the sub-grid into 2 triangles per "rhombus" cell, plus boundary
                // triangles along the diagonal. For each row i (0..n-1), column j (0..n-1-i):
                //   upper-tri: (i,j), (i+1,j), (i,j+1)
                //   lower-tri: (i+1,j), (i+1,j+1), (i,j+1) — only when i+j < n-1
                for (int i = 0; i < GouraudFunctionSubdivisions; i++)
                {
                    int rowOffset = i * (GouraudFunctionSubdivisions + 1) - i * (i - 1) / 2;
                    int nextRowOffset = (i + 1) * (GouraudFunctionSubdivisions + 1) - (i + 1) * i / 2;
                    for (int j = 0; j < GouraudFunctionSubdivisions - i; j++)
                    {
                        int v0 = rowOffset + j;
                        int v1 = nextRowOffset + j;
                        int v2 = rowOffset + j + 1;
                        outPositions.Add(subPts[v0]); outPositions.Add(subPts[v1]); outPositions.Add(subPts[v2]);
                        outColors.Add(subCols[v0]); outColors.Add(subCols[v1]); outColors.Add(subCols[v2]);

                        if (i + j < GouraudFunctionSubdivisions - 1)
                        {
                            int v3 = nextRowOffset + j + 1;
                            outPositions.Add(subPts[v1]); outPositions.Add(subPts[v3]); outPositions.Add(subPts[v2]);
                            outColors.Add(subCols[v1]); outColors.Add(subCols[v3]); outColors.Add(subCols[v2]);
                        }
                    }
                }
            }
            finally
            {
                ptsPool.Return(subPts);
                colsPool.Return(subCols);
            }
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
            float minX, float minY, float maxX, float maxY, bool isStroke = false, SKPath? path = null)
        {
            /*
             * TODO - Not finished, need more document samples
             */

            var domain = shading.Domain;

            double x0 = domain[0];
            double x1 = domain[1];
            double y0 = domain[2];
            double y1 = domain[3];

            // Based on https://github.com/apache/pdfbox/blob/trunk/pdfbox/src/main/java/org/apache/pdfbox/pdmodel/graphics/shading/Type1ShadingContext.java

            int w = (int)(x1 - x0);
            int h = (int)(y1 - y0);

            using (SKBitmap shaderBitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul))
            {
                var raster = shaderBitmap.GetPixelSpan();

                double[] values = new double[2]; // TODO - stackalloc

                for (int j = 0; j < h; j++)
                {
                    for (int i = 0; i < w; i++)
                    {
                        int index = (j * w + i) * 4;
                        bool useBackground = false;
                        values[0] = x0 + i;
                        values[1] = y0 + j;
                        //rat.transform(values, 0, values, 0, 1);
                        if (values[0] < domain[0] || values[0] > domain[1] ||
                            values[1] < domain[2] || values[1] > domain[3])
                        {
                            if (shading.Background is null)
                            {
                                continue;
                            }
                            useBackground = true;
                        }

                        // evaluate function
                        double[] tmpValues; // "values" can't be reused due to different length
                        if (useBackground && shading.Background is not null)
                        {
                            tmpValues = shading.Background;
                        }
                        else
                        {
                            try
                            {
                                tmpValues = shading.Eval(values);
                            }
                            catch (IOException e)
                            {
                                System.Diagnostics.Debug.WriteLine("error while processing a function {0}", e);
                                continue;
                            }
                        }

                        // convert color values from shading color space to RGB
                        ColorSpaceDetails? shadingColorSpace = shading.ColorSpace;
                        if (shadingColorSpace is not null)
                        {
                            try
                            {
                                (double r, double g, double b) = shadingColorSpace.GetColor(tmpValues).ToRGBValues(); // To improve
                                tmpValues = [r, g, b];
                            }
                            catch (IOException e)
                            {
                                System.Diagnostics.Debug.WriteLine("error processing color space {0}", e);
                                continue;
                            }
                        }
                        raster[index] = (byte)(tmpValues[0] * 255);
                        raster[index + 1] = (byte)(tmpValues[1] * 255);
                        raster[index + 2] = (byte)(tmpValues[2] * 255);
                        raster[index + 3] = 255;
                    }
                }

                var finalShadingMatrix = patternTransformMatrix.PreConcat(shading.Matrix.ToSkMatrix());

                var currentState = GetCurrentState();

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
        }

        private void RenderShadingPatternCurrentPath(ShadingPatternColor pattern, bool isStroke)
        {
            RenderShadingPattern(_currentPath, pattern, isStroke);
        }

        private void RenderShadingPattern(SKPath path, ShadingPatternColor pattern, bool isStroke)
        {
            if (pattern.ExtGState is not null)
            {
                // TODO
            }

            float maxX = path.Bounds.Right;
            float maxY = path.Bounds.Top;
            float minX = path.Bounds.Left;
            float minY = path.Bounds.Bottom;

            // We cancel CTM, but not canvas' Y flip, as we still need it.
            var patternTransform = CurrentTransformationMatrix.ToSkMatrix().Invert()
                .PreConcat(_currentStreamOriginalTransforms.Peek())
                .PreConcat(pattern.Matrix.ToSkMatrix());

            switch (pattern.Shading.ShadingType)
            {
                case ShadingType.Axial:
                    RenderAxialShading(pattern.Shading as AxialShading, in patternTransform, minX, minY, maxX, maxY, isStroke, path);
                    break;

                case ShadingType.Radial:
                    RenderRadialShading(pattern.Shading as RadialShading, in patternTransform, minX, minY, maxX, maxY, isStroke, path);
                    break;

                case ShadingType.FunctionBased:
                    RenderFunctionBasedShading(pattern.Shading as FunctionBasedShading, in patternTransform, minX, minY, maxX, maxY, isStroke, path);
                    break;

                case ShadingType.FreeFormGouraud:
                    RenderFreeFormGouraudShading(pattern.Shading as FreeFormGouraudShading, in patternTransform, minX, minY, maxX, maxY, isStroke, path);
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

        private void RenderTilingPatternCurrentPath(TilingPatternColor pattern, bool isStroke)
        {
            RenderTilingPattern(_currentPath, pattern, isStroke);
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

            // Read every vertex in stream order. Estimate the vertex count from the stream
            // size to pre-size the list (avoids the ~10 doubling re-allocations a default
            // List<T> would do).
            int bytesPerVertex = (bitsPerCoordinate * 2 + bitsPerComponent * numStreamColorComponents + 7) / 8;
            int estimatedVerts = bytesPerVertex > 0 ? shading.Data.Length / bytesPerVertex : 16;
            var verts = new List<(SKPoint pt, SKColor col)>(estimatedVerts);

            // Reusable per-vertex buffer — Eval() needs a heap double[], but it doesn't keep
            // the reference (it returns it directly when no Function is present, otherwise
            // returns a fresh array), so reusing is safe.
            double[] colorBuffer = new double[numStreamColorComponents];
            var bitReader = new GouraudBitReader(shading.Data.Span);

            while (bitReader.HasData)
            {
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
                    break;
                }

                double x = xMin + (rawX / maxCoordRaw) * (xMax - xMin);
                double y = yMin + (rawY / maxCoordRaw) * (yMax - yMin);

                double[] evalResult = shading.Eval(colorBuffer);
                SKColor skColor = shading.ColorSpace.GetColor(evalResult).ToSKColor(currentState.AlphaConstantNonStroking);
                SKPoint pt = patternTransformMatrix.MapPoint(new SKPoint((float)x, (float)y));
                verts.Add((pt, skColor));
            }

            int totalRows = verts.Count / verticesPerRow;
            if (totalRows < 2)
            {
                return;
            }

            // For each (row, col) cell, emit two triangles between (row, col)/(row, col+1)/(row+1, col)
            // and (row, col+1)/(row+1, col+1)/(row+1, col).
            int triCount = (totalRows - 1) * (verticesPerRow - 1) * 2;
            var positions = new List<SKPoint>(triCount * 3);
            var colors = new List<SKColor>(triCount * 3);

            for (int r = 0; r < totalRows - 1; r++)
            {
                int rowBase = r * verticesPerRow;
                int nextRowBase = rowBase + verticesPerRow;
                for (int c = 0; c < verticesPerRow - 1; c++)
                {
                    var v00 = verts[rowBase + c];
                    var v10 = verts[rowBase + c + 1];
                    var v01 = verts[nextRowBase + c];
                    var v11 = verts[nextRowBase + c + 1];

                    positions.Add(v00.pt); positions.Add(v10.pt); positions.Add(v01.pt);
                    colors.Add(v00.col); colors.Add(v10.col); colors.Add(v01.col);

                    positions.Add(v10.pt); positions.Add(v11.pt); positions.Add(v01.pt);
                    colors.Add(v10.col); colors.Add(v11.col); colors.Add(v01.col);
                }
            }

            DrawShadingVertices(positions, colors, path, shading.AntiAlias, currentState.BlendMode.ToSKBlendMode());
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

            // Each Coons patch tessellates to PatchSubdivisions² × 6 triangle vertices.
            // Pre-size for the common single-patch case so the no-function path doesn't
            // pay log₂(N) doubling re-allocations.
            const int perPatchVertexCount = PatchSubdivisions * PatchSubdivisions * 6;
            var positions = hasFunction ? null : new List<SKPoint>(perPatchVertexCount);
            var colors = hasFunction ? null : new List<SKColor>(perPatchVertexCount);

            if (hasFunction && path is not null)
            {
                _canvas.Save();
                _canvas.ClipPath(path);
            }

            // Previous patch state — corners (12 points) and 4 corner colour components — used by flag 1/2/3.
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

                SKPoint[] points = new SKPoint[12];
                double[][] cornerColors = new double[4][];

                if (flag == 0)
                {
                    if (!ReadPatchPoints(ref bitReader, bitsPerCoordinate, maxCoordRaw, xMin, xMax, yMin, yMax,
                            in patternTransformMatrix, points, 0, newPointCount))
                    {
                        break;
                    }
                    if (!ReadPatchColors(ref bitReader, bitsPerComponent, maxColorRaw, decode, numStreamColorComponents,
                            cornerColors, 0, newColorCount))
                    {
                        break;
                    }
                    bitReader.AlignToByte();
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
                    cornerColors[0] = prevColors[newC1ColorIdx];
                    cornerColors[1] = prevColors[newC2ColorIdx];

                    if (!ReadPatchPoints(ref bitReader, bitsPerCoordinate, maxCoordRaw, xMin, xMax, yMin, yMax,
                            in patternTransformMatrix, points, 4, newPointCount))
                    {
                        break;
                    }
                    if (!ReadPatchColors(ref bitReader, bitsPerComponent, maxColorRaw, decode, numStreamColorComponents,
                            cornerColors, 2, newColorCount))
                    {
                        break;
                    }
                    bitReader.AlignToByte();
                }

                if (hasFunction)
                {
                    DrawCoonsPatchTextured(shading, currentState, points, cornerColors);
                }
                else
                {
                    TessellateCoonsPatch(shading, currentState, points, cornerColors, positions!, colors!);
                }

                prevPts = points;
                prevColors = cornerColors;
            }

            if (hasFunction)
            {
                if (path is not null)
                {
                    _canvas.Restore();
                }
            }
            else
            {
                DrawShadingVertices(positions!, colors!, path, shading.AntiAlias, currentState.BlendMode.ToSKBlendMode());
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

            const int perPatchVertexCount = PatchSubdivisions * PatchSubdivisions * 6;
            var positions = hasFunction ? null : new List<SKPoint>(perPatchVertexCount);
            var colors = hasFunction ? null : new List<SKColor>(perPatchVertexCount);

            if (hasFunction && path is not null)
            {
                _canvas.Save();
                _canvas.ClipPath(path);
            }

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

                SKPoint[] points = new SKPoint[16];
                double[][] cornerColors = new double[4][];

                if (flag == 0)
                {
                    if (!ReadPatchPoints(ref bitReader, bitsPerCoordinate, maxCoordRaw, xMin, xMax, yMin, yMax,
                            in patternTransformMatrix, points, 0, newPointCount))
                    {
                        break;
                    }
                    if (!ReadPatchColors(ref bitReader, bitsPerComponent, maxColorRaw, decode, numStreamColorComponents,
                            cornerColors, 0, newColorCount))
                    {
                        break;
                    }
                    bitReader.AlignToByte();
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
                    cornerColors[0] = prevColors[newC1ColorIdx];
                    cornerColors[1] = prevColors[newC2ColorIdx];

                    if (!ReadPatchPoints(ref bitReader, bitsPerCoordinate, maxCoordRaw, xMin, xMax, yMin, yMax,
                            in patternTransformMatrix, points, 4, newPointCount))
                    {
                        break;
                    }
                    if (!ReadPatchColors(ref bitReader, bitsPerComponent, maxColorRaw, decode, numStreamColorComponents,
                            cornerColors, 2, newColorCount))
                    {
                        break;
                    }
                    bitReader.AlignToByte();
                }

                if (hasFunction)
                {
                    DrawTensorPatchTextured(shading, currentState, points, cornerColors);
                }
                else
                {
                    TessellateTensorPatch(shading, currentState, points, cornerColors, positions!, colors!);
                }

                prevPts = points;
                prevColors = cornerColors;
            }

            if (hasFunction)
            {
                if (path is not null)
                {
                    _canvas.Restore();
                }
            }
            else
            {
                DrawShadingVertices(positions!, colors!, path, shading.AntiAlias, currentState.BlendMode.ToSKBlendMode());
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
            SKPoint[] dest, int destOffset, int count)
        {
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

                double x = xMin + (rawX / maxCoordRaw) * (xMax - xMin);
                double y = yMin + (rawY / maxCoordRaw) * (yMax - yMin);
                dest[destOffset + i] = patternTransformMatrix.MapPoint(new SKPoint((float)x, (float)y));
            }
            return true;
        }

        /// <summary>
        /// Reads <paramref name="count"/> corner-colour records from the bit stream into <paramref name="dest"/>
        /// starting at <paramref name="destOffset"/>. Each colour is stored as the per-vertex stream components
        /// (n components if no Function, 1 parametric value otherwise) decoded via the Decode array.
        /// Function evaluation is deferred until the patch is tessellated so that the per-pixel function eval
        /// can capture non-linear / stitched / step functions correctly.
        /// </summary>
        private static bool ReadPatchColors(ref GouraudBitReader bitReader, int bitsPerComponent, double maxColorRaw,
            double[] decode, int numStreamColorComponents,
            double[][] dest, int destOffset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                double[] components = new double[numStreamColorComponents];
                try
                {
                    for (int k = 0; k < numStreamColorComponents; k++)
                    {
                        long raw = bitReader.ReadBits(bitsPerComponent);
                        double cMin = decode[4 + k * 2];
                        double cMax = decode[5 + k * 2];
                        components[k] = cMin + (raw / maxColorRaw) * (cMax - cMin);
                    }
                }
                catch
                {
                    return false;
                }

                dest[destOffset + i] = components;
            }
            return true;
        }

        /// <summary>
        /// Samples a Coons patch surface on a (PatchSubdivisions+1)² UV grid and emits two triangles
        /// per cell into the supplied vertex/colour lists.
        /// Corner-colour bilinear interpolation matches PDFBox: cornerColors[0..3] correspond to
        /// (u,v) = (0,0), (1,0), (1,1), (0,1).
        /// </summary>
        private void TessellateCoonsPatch(Shading shading, CurrentGraphicsState currentState,
            SKPoint[] pts, double[][] cornerColors,
            List<SKPoint> outPositions, List<SKColor> outColors)
        {
            const int gridCount = (PatchSubdivisions + 1) * (PatchSubdivisions + 1);
            var grid = new SKPoint[gridCount];
            var gridCol = new SKColor[gridCount];

            // Single reusable buffer for the bilinear corner-colour blend; passed to
            // EvaluatePatchColor for every grid sample so we allocate once instead of (n+1)².
            double[] interpBuffer = new double[cornerColors[0].Length];

            for (int j = 0; j <= PatchSubdivisions; j++)
            {
                float v = (float)j / PatchSubdivisions;
                for (int i = 0; i <= PatchSubdivisions; i++)
                {
                    float u = (float)i / PatchSubdivisions;

                    grid[j * (PatchSubdivisions + 1) + i] = EvaluateCoonsSurface(pts, u, v);
                    gridCol[j * (PatchSubdivisions + 1) + i] = EvaluatePatchColor(shading, currentState, cornerColors, u, v, interpBuffer);
                }
            }

            EmitTrianglesFromGrid(grid, gridCol, PatchSubdivisions, outPositions, outColors);
        }

        /// <summary>
        /// Samples a Tensor-product patch surface on a (PatchSubdivisions+1)² UV grid.
        /// The 4×4 control grid follows the PDFBox layout: rows indexed by v, columns by u.
        /// </summary>
        private void TessellateTensorPatch(Shading shading, CurrentGraphicsState currentState,
            SKPoint[] tcp, double[][] cornerColors,
            List<SKPoint> outPositions, List<SKColor> outColors)
        {
            // Map the 16 stream points into a 4×4 grid indexed [row][col] where col is u and row is v.
            // Layout from PDF spec § 8.7.4.5.7 / PDFBox TensorPatch:
            //   row v=0 (forward in u): tcp[0..3]
            //   row v=1 (forward in u): tcp[9, 8, 7, 6]
            //   col u=0 interior (v=1/3, v=2/3): tcp[11], tcp[10]
            //   col u=1 interior (v=1/3, v=2/3): tcp[4], tcp[5]
            //   inner row v=1/3: tcp[12], tcp[13]
            //   inner row v=2/3: tcp[15], tcp[14]
            var p = new SKPoint[4, 4];
            p[0, 0] = tcp[0]; p[0, 1] = tcp[1]; p[0, 2] = tcp[2]; p[0, 3] = tcp[3];
            p[1, 0] = tcp[11]; p[1, 1] = tcp[12]; p[1, 2] = tcp[13]; p[1, 3] = tcp[4];
            p[2, 0] = tcp[10]; p[2, 1] = tcp[15]; p[2, 2] = tcp[14]; p[2, 3] = tcp[5];
            p[3, 0] = tcp[9]; p[3, 1] = tcp[8]; p[3, 2] = tcp[7]; p[3, 3] = tcp[6];

            const int gridCount = (PatchSubdivisions + 1) * (PatchSubdivisions + 1);
            var grid = new SKPoint[gridCount];
            var gridCol = new SKColor[gridCount];

            // Precompute Bernstein basis values for each sampled u and v into flat 4×(n+1)
            // spans so the inner sampling loop reads contiguous memory and skips the
            // jagged-array allocations the previous float[][] form required.
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

            // Single reusable buffer for the bilinear corner-colour blend; passed to
            // EvaluatePatchColor for every grid sample so we allocate once instead of (n+1)².
            double[] interpBuffer = new double[cornerColors[0].Length];

            for (int j = 0; j <= PatchSubdivisions; j++)
            {
                float v = (float)j / PatchSubdivisions;
                ReadOnlySpan<float> bv = bV.Slice(j * 4, 4);
                for (int i = 0; i <= PatchSubdivisions; i++)
                {
                    float u = (float)i / PatchSubdivisions;
                    ReadOnlySpan<float> bu = bU.Slice(i * 4, 4);

                    float x = 0, y = 0;
                    for (int row = 0; row < 4; row++)
                    {
                        for (int col = 0; col < 4; col++)
                        {
                            // u indexes columns, v indexes rows.
                            float w = bu[col] * bv[row];
                            x += p[row, col].X * w;
                            y += p[row, col].Y * w;
                        }
                    }

                    grid[j * (PatchSubdivisions + 1) + i] = new SKPoint(x, y);
                    gridCol[j * (PatchSubdivisions + 1) + i] = EvaluatePatchColor(shading, currentState, cornerColors, u, v, interpBuffer);
                }
            }

            EmitTrianglesFromGrid(grid, gridCol, PatchSubdivisions, outPositions, outColors);
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
        private static SKColor EvaluatePatchColor(Shading shading, CurrentGraphicsState currentState,
            double[][] cornerColors, float u, float v, double[] interpBuffer)
        {
            int components = cornerColors[0].Length;
            double w00 = (1 - u) * (1 - v);
            double w10 = u * (1 - v);
            double w11 = u * v;
            double w01 = (1 - u) * v;
            for (int k = 0; k < components; k++)
            {
                interpBuffer[k] = w00 * cornerColors[0][k] + w10 * cornerColors[1][k]
                                  + w11 * cornerColors[2][k] + w01 * cornerColors[3][k];
            }

            double[] evalResult = shading.Eval(interpBuffer);
            return shading.ColorSpace.GetColor(evalResult).ToSKColor(currentState.AlphaConstantNonStroking);
        }

        /// <summary>
        /// Emits two triangles for each grid cell into the supplied lists.
        /// Cell (i,j) connects vertices at (i, j), (i+1, j), (i, j+1), (i+1, j+1).
        /// </summary>
        private static void EmitTrianglesFromGrid(SKPoint[] grid, SKColor[] gridCol, int n,
            List<SKPoint> outPositions, List<SKColor> outColors)
        {
            int stride = n + 1;
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    int i00 = j * stride + i;
                    int i10 = i00 + 1;
                    int i01 = i00 + stride;
                    int i11 = i01 + 1;

                    outPositions.Add(grid[i00]); outPositions.Add(grid[i10]); outPositions.Add(grid[i01]);
                    outColors.Add(gridCol[i00]); outColors.Add(gridCol[i10]); outColors.Add(gridCol[i01]);

                    outPositions.Add(grid[i10]); outPositions.Add(grid[i11]); outPositions.Add(grid[i01]);
                    outColors.Add(gridCol[i10]); outColors.Add(gridCol[i11]); outColors.Add(gridCol[i01]);
                }
            }
        }

        /// <summary>
        /// Submits the accumulated triangle list to the canvas, optionally clipped to <paramref name="clipPath"/>.
        /// White paint × Modulate preserves the per-vertex colours regardless of the src/dst role assignment.
        /// </summary>
        private void DrawShadingVertices(List<SKPoint> positions, List<SKColor> colors,
            SKPath? clipPath, bool antiAlias, SKBlendMode blendMode)
        {
            if (positions.Count == 0)
            {
                return;
            }

            using var paint = new SKPaint();
            paint.IsAntialias = antiAlias;
            paint.BlendMode = blendMode;
            paint.Color = SKColors.White;

            if (clipPath is not null)
            {
                _canvas.Save();
                _canvas.ClipPath(clipPath);
            }

            _canvas.DrawVertices(SKVertexMode.Triangles, positions.ToArray(), null, colors.ToArray(),
                SKBlendMode.Modulate, null, paint);

            if (clipPath is not null)
            {
                _canvas.Restore();
            }
        }

        /// <summary>
        /// Evaluates the Coons surface S(u,v) for the supplied 12-control-point patch.
        /// </summary>
        private static SKPoint EvaluateCoonsSurface(SKPoint[] pts, float u, float v)
        {
            SKPoint sBottom = CubicBezier(pts[0], pts[1], pts[2], pts[3], u);
            SKPoint sTop = CubicBezier(pts[9], pts[8], pts[7], pts[6], u);
            SKPoint sLeft = CubicBezier(pts[0], pts[11], pts[10], pts[9], v);
            SKPoint sRight = CubicBezier(pts[3], pts[4], pts[5], pts[6], v);

            float p00x = pts[0].X, p00y = pts[0].Y;
            float p10x = pts[3].X, p10y = pts[3].Y;
            float p11x = pts[6].X, p11y = pts[6].Y;
            float p01x = pts[9].X, p01y = pts[9].Y;

            float x = (1 - v) * sBottom.X + v * sTop.X
                      + (1 - u) * sLeft.X + u * sRight.X
                      - (1 - u) * (1 - v) * p00x - u * (1 - v) * p10x
                      - u * v * p11x - (1 - u) * v * p01x;
            float y = (1 - v) * sBottom.Y + v * sTop.Y
                      + (1 - u) * sLeft.Y + u * sRight.Y
                      - (1 - u) * (1 - v) * p00y - u * (1 - v) * p10y
                      - u * v * p11y - (1 - u) * v * p01y;
            return new SKPoint(x, y);
        }

        /// <summary>
        /// Evaluates the Tensor-product Bezier surface using precomputed Bernstein bases.
        /// <paramref name="p"/> is the 4×4 control grid laid out [row=v, col=u].
        /// </summary>
        private static SKPoint EvaluateTensorSurface(SKPoint[,] p, ReadOnlySpan<float> bU, ReadOnlySpan<float> bV)
        {
            float x = 0, y = 0;
            for (int row = 0; row < 4; row++)
            {
                for (int col = 0; col < 4; col++)
                {
                    float w = bU[col] * bV[row];
                    x += p[row, col].X * w;
                    y += p[row, col].Y * w;
                }
            }
            return new SKPoint(x, y);
        }

        /// <summary>
        /// Constructs the 4×4 Tensor control grid from the 16 stream points,
        /// per the PDF spec / PDFBox layout (rows indexed by v, columns by u).
        /// </summary>
        private static SKPoint[,] BuildTensorControlGrid(SKPoint[] tcp)
        {
            return new SKPoint[4, 4]
            {
                { tcp[0],  tcp[1],  tcp[2],  tcp[3]  },
                { tcp[11], tcp[12], tcp[13], tcp[4]  },
                { tcp[10], tcp[15], tcp[14], tcp[5]  },
                { tcp[9],  tcp[8],  tcp[7],  tcp[6]  },
            };
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
            double[][] cornerColors, int texSize)
        {
            var bitmap = new SKBitmap(texSize, texSize, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            int components = cornerColors[0].Length;
            double[] interp = new double[components];
            float invDen = 1f / (texSize - 1);

            Span<byte> pixelBytes = bitmap.GetPixelSpan();

            for (int j = 0; j < texSize; j++)
            {
                float v = j * invDen;
                int rowOffset = j * texSize * 4;
                for (int i = 0; i < texSize; i++)
                {
                    float u = i * invDen;

                    double w00 = (1 - u) * (1 - v);
                    double w10 = u * (1 - v);
                    double w11 = u * v;
                    double w01 = (1 - u) * v;
                    for (int k = 0; k < components; k++)
                    {
                        interp[k] = w00 * cornerColors[0][k] + w10 * cornerColors[1][k]
                                    + w11 * cornerColors[2][k] + w01 * cornerColors[3][k];
                    }

                    double[] evalResult = shading.Eval(interp);
                    SKColor c = shading.ColorSpace.GetColor(evalResult).ToSKColor(currentState.AlphaConstantNonStroking);

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
        /// Draws a Coons patch via texture mapping: builds a per-pixel-evaluated colour bitmap,
        /// triangulates the patch surface with texture coordinates, and lets Skia sample the
        /// bitmap at every output pixel. This gives correct step-function / stitched-Type-3
        /// rendering that vertex-colour Gouraud cannot.
        /// </summary>
        private void DrawCoonsPatchTextured(Shading shading, CurrentGraphicsState currentState,
            SKPoint[] pts, double[][] cornerColors)
        {
            using var bitmap = BuildPatchTexture(shading, currentState, cornerColors, PatchTextureSize);

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
                
                for (int j = 0; j <= PatchSubdivisions; j++)
                {
                    float v = (float)j / PatchSubdivisions;
                    for (int i = 0; i <= PatchSubdivisions; i++)
                    {
                        float u = (float)i / PatchSubdivisions;
                        int idx = j * stride + i;
                        positions[idx] = EvaluateCoonsSurface(pts, u, v);
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
            SKPoint[] tcp, double[][] cornerColors)
        {
            using SKBitmap bitmap = BuildPatchTexture(shading, currentState, cornerColors, PatchTextureSize);
            SKPoint[,] p = BuildTensorControlGrid(tcp);

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
            public readonly bool HasData => _bytePos < _data.Length;

            /// <summary>Reads <paramref name="count"/> bits and returns them as a non-negative <see cref="long"/>, MSB first.</summary>
            public long ReadBits(int count)
            {
                long result = 0;
                for (int i = 0; i < count; i++)
                {
                    if (_bytePos >= _data.Length)
                    {
                        throw new InvalidOperationException("Unexpected end of shading stream.");
                    }

                    result = (result << 1) | (long)((_data[_bytePos] >> _bitPos) & 1);
                    if (--_bitPos < 0)
                    {
                        _bitPos = 7;
                        _bytePos++;
                    }
                }
                return result;
            }

            /// <summary>
            /// Advances the read position to the start of the next byte,
            /// discarding any remaining bits in the current byte.
            /// No-op when already at a byte boundary.
            /// </summary>
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
                    initialMatrix, ParsingOptions, null, _fontCache);

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
                SKRect rect = SKRect.Create(Math.Abs((float)pattern.XStep), Math.Abs((float)pattern.YStep));
                SKMatrix transformMatrix = CurrentTransformationMatrix.ToSkMatrix().Invert()
                    .PreConcat(_currentStreamOriginalTransforms.Peek())
                    .PreConcat(pattern.GetTilingPatterAdjMatrix());

                using (var picture = processor.Process(PageNumber, operations))
                using (var shader = SKShader.CreatePicture(picture, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, SKFilterMode.Linear, transformMatrix, rect))
                using (var paint = new SKPaint())
                {
                    paint.IsAntialias = _antiAliasing;
                    paint.Shader = shader;
                    paint.BlendMode = GetCurrentState().BlendMode.ToSKBlendMode();
                    _canvas.DrawPath(path, paint);
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
