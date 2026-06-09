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
        Span<double> colorBuffer = new double[numStreamColorComponents];
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

        using var paint = new SKPaint();
        paint.IsAntialias = shading.AntiAlias;
        paint.BlendMode = currentState.BlendMode.ToSKBlendMode();
        paint.Color = SKColors.White;

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
    /// Reads <paramref name="row"/>.Length consecutive vertex records (coordinates +
    /// decoded colour, byte-aligned per record) from a Type 5 lattice-form stream into
    /// <paramref name="row"/>. Returns false if the stream ends mid-record so the
    /// caller can stop without producing a partial row of triangles.
    /// </summary>
    private static bool TryReadLatticeRow(ref GouraudBitReader bitReader,
        (SKPoint pt, SKColor col)[] row,
        int bitsPerCoordinate, int bitsPerComponent,
        double maxCoordRaw, double maxColorRaw,
        ReadOnlySpan<double> decode, int numStreamColorComponents,
        double xMin, double xMax, double yMin, double yMax,
        in SKMatrix patternTransformMatrix,
        Shading shading, CurrentGraphicsState currentState,
        Span<double> colorBuffer)
    {
        double alpha = currentState.AlphaConstantNonStroking;
        ColorSpaceDetails colorSpace = shading.ColorSpace;
        double invMaxColorRaw = 1.0 / maxColorRaw;
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
                    colorBuffer[i] = DecodeComponent(raw, invMaxColorRaw, decode[4 + i * 2], decode[5 + i * 2]);
                }
                bitReader.AlignToByte();
            }
            catch
            {
                return false;
            }

            double x = xMin + (rawX / maxCoordRaw) * (xMax - xMin);
            double y = yMin + (rawY / maxCoordRaw) * (yMax - yMin);

            int written = shading.Eval(colorBuffer.Slice(0, numStreamColorComponents), evalOut);
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
}
