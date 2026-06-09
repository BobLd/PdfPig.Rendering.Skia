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
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Rendering.Skia.Helpers;

namespace UglyToad.PdfPig.Rendering.Skia;

internal partial class SkiaStreamProcessor
{
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
}
