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
using System.Buffers;
using System.Runtime.CompilerServices;
using SkiaSharp;
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Rendering.Skia.Helpers;

namespace UglyToad.PdfPig.Rendering.Skia;

internal partial class SkiaStreamProcessor
{
    /// <summary>
    /// Renders a Type 6 Coons-patch mesh.
    /// Each patch is bounded by four cubic Bézier curves; the surface S(u,v) blends
    /// the boundary curves and corners per PDF 32000-1:2008 §8.7.4.5.6.
    /// </summary>
    private void RenderCoonsPatchShading(CoonsPatchMeshesShading shading, in SKMatrix patternTransformMatrix, SKPath? path = null)
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

    private void DrawCoonsMeshUnclipped(CoonsPatchMeshesShading shading, SKMatrix patternTransformMatrix)
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

        // Function path: the per-vertex stream carries a single parametric value, so the colour is
        // a 1-D function of it. Pre-evaluate that mapping once into a shading-global LUT and reuse
        // it for every patch texture instead of calling the Function per texel. See BuildPatchTexture.
        uint[]? patchLut = null;
        double domainLo = 0d, domainHi = 0d;
        if (hasFunction && numStreamColorComponents == 1)
        {
            domainLo = decode[4];
            domainHi = decode[5];
            patchLut = BuildParametricColorLut(shading, currentState, domainLo, domainHi);
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
                    DrawCoonsPatchTextured(shading, currentState, points, cornerColors, patchLut, domainLo, domainHi);
                }
                else
                {
                    TessellateAndDrawCoonsPatch(shading, currentState, points, cornerColors,
                        grid!, gridCol!, interpBuffer!, gouraudPaint!);
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
        SKPaint paint)
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
                gridCol[rowOffset + i] = EvaluatePatchColor(shading, alpha, cornerColors, u, v, interpBuffer, coonsEvalBuffer);
            }
        }

        DrawGridTriangles(grid, gridCol, n, paint);
    }

    /// <summary>
    /// Draws a Coons patch via texture mapping: builds a per-pixel-evaluated colour bitmap,
    /// triangulates the patch surface with texture coordinates, and lets Skia sample the
    /// bitmap at every output pixel. This gives correct step-function / stitched-Type-3
    /// rendering that vertex-colour Gouraud cannot.
    /// </summary>
    private void DrawCoonsPatchTextured(Shading shading, CurrentGraphicsState currentState,
        ReadOnlySpan<SKPoint> pts, double[][] cornerColors, uint[]? lut, double domainLo, double domainHi)
    {
        using var bitmap = BuildPatchTexture(shading, currentState, cornerColors, PatchTextureSize, lut, domainLo, domainHi);

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
}
