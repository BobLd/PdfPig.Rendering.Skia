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
        // Alpha and blend mode are baked into the recorded picture, so they form part of the cache
        // key and are captured here, before GetOrBuildMeshPicture swaps the canvas for a recorder.
        // See RenderCoonsPatchShading / GetOrBuildMeshPicture.
        var currentState = GetCurrentState();
        double alpha = currentState.AlphaConstantNonStroking;
        SKBlendMode blend = currentState.BlendMode.ToSKBlendMode();
        SKMatrix transform = patternTransformMatrix;
        SKPicture mesh = GetOrBuildMeshPicture(shading, in transform, alpha, blend, currentState.RenderingIntent,
            () => DrawTensorMeshUnclipped(shading, transform));
        DrawCachedMesh(mesh, path);
    }

    private void DrawTensorMeshUnclipped(TensorProductPatchMeshesShading shading,
      SKMatrix patternTransformMatrix)
    {
        // The stream layout and edge-continuation rules are shared with Type 6 — see
        // DrawPatchMeshUnclipped for the loop; only the per-patch tessellation is Tensor-specific.
        DrawPatchMeshUnclipped(shading, in patternTransformMatrix, shading.Data.Span,
            shading.BitsPerCoordinate, shading.BitsPerComponent, shading.BitsPerFlag,
            shading.Decode, isTensor: true);
    }

    /// <summary>
    /// Samples a Tensor-product patch surface on an adaptive (n+1)² UV grid and submits
    /// it as a single indexed DrawVertices call. The 4×4 control grid follows the PDFBox
    /// layout: rows indexed by v, columns by u. See <see cref="TessellateAndDrawCoonsPatch"/>
    /// for the exact-size vertex-array rationale.
    /// </summary>
    private void TessellateAndDrawTensorPatch(Shading shading, CurrentGraphicsState currentState,
        SKPoint[] tcp, double[][] cornerColors, double[] interpBuffer,
        SKPaint paint)
    {
        // Map the 16 stream points into a 4×4 grid stored row-major in a stackalloc
        // span. Indexing is row * 4 + col (col is u, row is v). See the comment on
        // BuildTensorControlGrid for the layout.
        Span<SKPoint> p = stackalloc SKPoint[16];
        BuildTensorControlGrid(tcp, p);

        // Subdivide proportionally to the patch size: a fine mesh of tiny patches needs only a
        // cell or two each, not the full 32×32. See ComputePatchSubdivisions.
        int n = ComputePatchSubdivisions(p);
        System.Diagnostics.Debug.Assert(n <= PatchSubdivisions);

        int axisLen = n + 1;
        var grid = new SKPoint[axisLen * axisLen];
        var gridCol = new SKColor[axisLen * axisLen];
        SampleTensorPatchGrid(p, n, grid);

        float invN = 1f / n;
        double alpha = currentState.AlphaConstantNonStroking;
        Span<double> tensorEvalBuffer = stackalloc double[ShadingEvalBufferSize];
        for (int j = 0; j < axisLen; j++)
        {
            float v = j * invN;
            int rowOffset = j * axisLen;
            for (int i = 0; i < axisLen; i++)
            {
                float u = i * invN;
                gridCol[rowOffset + i] = EvaluatePatchColor(shading, alpha, currentState.RenderingIntent, cornerColors, u, v, interpBuffer,
                        tensorEvalBuffer);
            }
        }

        DrawGridTriangles(grid, gridCol, n, paint);
    }

    /// <summary>
    /// Samples the Tensor-product Bézier surface on an (n+1)² grid (row-major, index
    /// <c>j·(n+1) + i</c>), writing positions into <paramref name="grid"/>. <paramref name="p"/>
    /// is the row-major 4×4 control grid (see <see cref="BuildTensorControlGrid"/>). Bernstein
    /// bases are computed once per axis and reused; <see cref="EvaluateTensorSurface"/> does the
    /// per-vertex blend. Shared by the Gouraud (<see cref="TessellateAndDrawTensorPatch"/>) and
    /// textured (<see cref="DrawTensorPatchTextured"/>) paths so the surface evaluation lives in
    /// exactly one place.
    /// </summary>
    private static void SampleTensorPatchGrid(ReadOnlySpan<SKPoint> p, int n, Span<SKPoint> grid)
    {
        int axisLen = n + 1;
        Span<float> bU = stackalloc float[4 * axisLen];
        Span<float> bV = stackalloc float[4 * axisLen];

        for (int i = 0; i < axisLen; i++)
        {
            BernsteinCubic((float)i / n, bU.Slice(i * 4, 4));
        }
        for (int j = 0; j < axisLen; j++)
        {
            BernsteinCubic((float)j / n, bV.Slice(j * 4, 4));
        }

        for (int j = 0; j < axisLen; j++)
        {
            ReadOnlySpan<float> bv = bV.Slice(j * 4, 4);
            int rowOffset = j * axisLen;
            for (int i = 0; i < axisLen; i++)
            {
                grid[rowOffset + i] = EvaluateTensorSurface(p, bU.Slice(i * 4, 4), bv);
            }
        }
    }

    /// <summary>
    /// Draws a Tensor-product patch via texture mapping. See <see cref="DrawCoonsPatchTextured"/>.
    /// </summary>
    private void DrawTensorPatchTextured(Shading shading, CurrentGraphicsState currentState,
        ReadOnlySpan<SKPoint> tcp, double[][] cornerColors, uint[]? lut, double domainLo, double domainHi)
    {
        using SKBitmap bitmap = BuildPatchTexture(shading, currentState, cornerColors, PatchTextureSize, lut, domainLo, domainHi);

        // Row-major 4×4 control grid lives on the stack — saves the heap allocation the
        // SKPoint[,] form paid per patch.
        Span<SKPoint> p = stackalloc SKPoint[16];
        BuildTensorControlGrid(tcp, p);

        // Subdivide proportionally to the patch size, matching the Gouraud path.
        // See ComputePatchSubdivisions.
        int n = ComputePatchSubdivisions(p);
        System.Diagnostics.Debug.Assert(n <= PatchSubdivisions);

        // Exact-size positions (DrawVertices takes the vertex count from Array.Length);
        // texture coordinates and the triangulation depend only on n and come from the
        // shared caches.
        int axisLen = n + 1;
        var positions = new SKPoint[axisLen * axisLen];
        SampleTensorPatchGrid(p, n, positions);
        DrawTexturedPatchVertices(shading, currentState, bitmap,
            positions, GetPatchTexCoords(n), GetGridTriangleIndices(n));
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
        grid[0] = tcp[0]; grid[1] = tcp[1]; grid[2] = tcp[2]; grid[3] = tcp[3];
        grid[4] = tcp[11]; grid[5] = tcp[12]; grid[6] = tcp[13]; grid[7] = tcp[4];
        grid[8] = tcp[10]; grid[9] = tcp[15]; grid[10] = tcp[14]; grid[11] = tcp[5];
        grid[12] = tcp[9]; grid[13] = tcp[8]; grid[14] = tcp[7]; grid[15] = tcp[6];
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
}