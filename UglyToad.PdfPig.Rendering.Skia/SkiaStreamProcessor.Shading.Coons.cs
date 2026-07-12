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
        // RenderTensorProductPatchShading / GetOrBuildMeshPicture. Alpha and blend mode are baked
        // into the recorded picture, so they form part of the cache key and are captured here,
        // before GetOrBuildMeshPicture swaps the canvas for a recorder.
        var currentState = GetCurrentState();
        double alpha = currentState.AlphaConstantNonStroking;
        SKBlendMode blend = currentState.BlendMode.ToSKBlendMode();
        SKMatrix transform = patternTransformMatrix;
        SKPicture mesh = GetOrBuildMeshPicture(shading, in transform, alpha, blend,
            () => DrawCoonsMeshUnclipped(shading, transform));
        DrawCachedMesh(mesh, path);
    }

    private void DrawCoonsMeshUnclipped(CoonsPatchMeshesShading shading, SKMatrix patternTransformMatrix)
    {
        // The stream layout and edge-continuation rules are shared with Type 7 — see
        // DrawPatchMeshUnclipped for the loop; only the per-patch tessellation is Coons-specific.
        DrawPatchMeshUnclipped(shading, in patternTransformMatrix, shading.Data.Span,
            shading.BitsPerCoordinate, shading.BitsPerComponent, shading.BitsPerFlag,
            shading.Decode, isTensor: false);
    }

    /// <summary>
    /// Samples a Coons patch surface on an adaptive (n+1)² UV grid and submits it as a
    /// single indexed DrawVertices call. Corner-colour bilinear interpolation matches
    /// PDFBox: cornerColors[0..3] correspond to (u,v) = (0,0), (1,0), (1,1), (0,1).
    /// <para>
    /// The vertex arrays are allocated at exactly (n+1)² per patch — DrawVertices takes
    /// the vertex count from Array.Length, so an oversized shared scratch would record
    /// stale vertices into the picture. The triangulation itself is the shared cached
    /// index buffer (see <see cref="GetGridTriangleIndices"/>).
    /// </para>
    /// </summary>
    private void TessellateAndDrawCoonsPatch(Shading shading, CurrentGraphicsState currentState,
        ReadOnlySpan<SKPoint> pts, double[][] cornerColors, double[] interpBuffer,
        SKPaint paint)
    {
        // Subdivide proportionally to the patch size — a fine mesh of tiny patches needs only
        // a cell or two each rather than the full 32×32. See ComputePatchSubdivisions.
        int n = ComputePatchSubdivisions(pts);
        System.Diagnostics.Debug.Assert(n <= PatchSubdivisions);

        int axisLen = n + 1;
        var grid = new SKPoint[axisLen * axisLen];
        var gridCol = new SKColor[axisLen * axisLen];
        SampleCoonsPatchGrid(pts, n, grid);

        float invN = 1f / n;
        double alpha = currentState.AlphaConstantNonStroking;
        Span<double> coonsEvalBuffer = stackalloc double[ShadingEvalBufferSize];
        for (int j = 0; j < axisLen; j++)
        {
            float v = j * invN;
            int rowOffset = j * axisLen;
            for (int i = 0; i < axisLen; i++)
            {
                float u = i * invN;
                gridCol[rowOffset + i] = EvaluatePatchColor(shading, alpha, cornerColors, u, v, interpBuffer, coonsEvalBuffer);
            }
        }

        DrawGridTriangles(grid, gridCol, n, paint);
    }

    /// <summary>
    /// Samples the Coons patch surface S(u,v) on an (n+1)² grid (row-major, index
    /// <c>j·(n+1) + i</c>), writing positions into <paramref name="grid"/>. The four boundary
    /// curves each depend on only one of u/v, so each is evaluated once per axis
    /// (4·(n+1) cubic-Bézier evaluations — a ~17× drop versus evaluating per cell at n = 32) and
    /// the per-vertex blend reuses those samples. Shared by the Gouraud
    /// (<see cref="TessellateAndDrawCoonsPatch"/>) and textured (<see cref="DrawCoonsPatchTextured"/>)
    /// paths so the surface formula lives in exactly one place.
    /// </summary>
    private static void SampleCoonsPatchGrid(ReadOnlySpan<SKPoint> pts, int n, Span<SKPoint> grid)
    {
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
            }
        }
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

        // Subdivide proportionally to the patch size, matching the Gouraud path.
        // See ComputePatchSubdivisions.
        int n = ComputePatchSubdivisions(pts);
        System.Diagnostics.Debug.Assert(n <= PatchSubdivisions);

        // Exact-size positions (DrawVertices takes the vertex count from Array.Length);
        // texture coordinates and the triangulation depend only on n and come from the
        // shared caches.
        int axisLen = n + 1;
        var positions = new SKPoint[axisLen * axisLen];
        SampleCoonsPatchGrid(pts, n, positions);
        DrawTexturedPatchVertices(shading, currentState, bitmap,
            positions, GetPatchTexCoords(n), GetGridTriangleIndices(n));
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
