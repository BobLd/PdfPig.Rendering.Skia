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
        /// <summary>
        /// Stack buffer size used for shading-Eval outputs in the hot loops. 32 doubles
        /// covers DeviceRGB / DeviceGray / DeviceCMYK / DeviceN cases without touching the heap.
        /// <para>Pathological wider colour spaces would need a heap fallback.</para>
        /// </summary>
        private const int ShadingEvalBufferSize = 32;

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
        /// Resolution of the per-patch colour texture used for function-based shadings.
        /// 512² with nearest-neighbour sampling means each texel maps to ~1 output pixel at
        /// typical chart scales — step-function transitions stay pixel-sharp while smooth
        /// gradients show no visible texel blockiness.
        /// </summary>
        private const int PatchTextureSize = 512;

        /// <summary>
        /// Number of entries in the parametric colour LUT used by function-based Coons/Tensor
        /// patches. Two per texture texel so a patch whose parametric range spans the full domain
        /// across the whole texture still quantises each texel's colour to strictly sub-texel
        /// precision — preserving the pixel-sharp step-function transitions the texture path exists
        /// to capture. See <see cref="Helpers.ParametricShadingTexture"/>.
        /// </summary>
        private const int PatchTextureLutSize = 2 * PatchTextureSize;

        // Advisory cull hint for recording a mesh picture. The recorded geometry lives in pattern
        // space (arbitrary range), so a tight rect could clip it — keep it effectively unbounded.
        private static readonly SKRect MeshPictureCullRect = new SKRect(-1_000_000f, -1_000_000f, 1_000_000f, 1_000_000f);

        // A page commonly paints the same Coons/Tensor mesh many times (e.g. a chart re-invokes the
        // `sh` operator for one shading dozens of times). The tessellated triangle list only depends
        // on the shading data and the transform in force, so cache it as an SKPicture and replay it
        // instead of re-tessellating patches each time. The geometry is recorded in pattern space and
        // the canvas CTM is applied at replay, so one picture serves every invocation under any CTM
        // that shares the same pattern-space transform — keying on the CTM here would defeat the
        // reuse that makes repeated `sh` fast. Alpha and blend mode are part of the key because they
        // are baked into the recorded colours / paint and cannot be re-applied at replay time.
        private Dictionary<Shading, (SKMatrix Transform, double Alpha, SKBlendMode Blend, SKPicture Picture)>? _meshPictureCache;
        
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
        /// Returns the cached tessellated mesh picture for <paramref name="shading"/> under
        /// <paramref name="transform"/>, <paramref name="alpha"/> and <paramref name="blend"/>,
        /// recording it via <paramref name="drawMesh"/> on first use. The picture stores the
        /// geometry in pattern space; the caller's canvas transform (and any clip) is applied when
        /// it is replayed, so one picture serves every invocation that shares the same transform,
        /// alpha and blend mode. Alpha and blend are part of the key because they are baked into the
        /// recorded picture (colours, paint blend) and cannot be re-applied at replay time.
        /// </summary>
        private SKPicture GetOrBuildMeshPicture(Shading shading, in SKMatrix transform,
            double alpha, SKBlendMode blend, Action drawMesh)
        {
            if (_meshPictureCache is not null
                && _meshPictureCache.TryGetValue(shading, out var entry)
                && entry.Transform.Equals(transform)
                && entry.Alpha.Equals(alpha)
                && entry.Blend == blend)
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

            _meshPictureCache ??= new Dictionary<Shading, (SKMatrix, double, SKBlendMode, SKPicture)>();
            if (_meshPictureCache.TryGetValue(shading, out var stale))
            {
                // Same shading, different transform/alpha/blend: the old picture is now unreachable.
                stale.Picture.Dispose();
            }

            _meshPictureCache[shading] = (transform, alpha, blend, picture);
            return picture;
        }

        /// <summary>
        /// Replays a cached mesh picture, optionally clipped to <paramref name="path"/>.
        /// </summary>
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
            ReadOnlySpan<double> decode, int numStreamColorComponents,
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
                        components[k] = DecodeComponent(raw, invMaxColorRaw, decode[4 + k * 2], decode[5 + k * 2]);
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
        /// Maps a raw integer colour/parameter sample to its decoded value via the linear Decode
        /// interpolation <c>lo + (raw / max) · (hi − lo)</c>, shared by every Type 4–7 vertex reader
        /// so the formula (and the multiply-by-reciprocal form) lives in exactly one place.
        /// <paramref name="invMaxRaw"/> is <c>1 / max</c>, hoisted by the caller out of its read loop.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double DecodeComponent(long raw, double invMaxRaw, double lo, double hi)
        {
            return lo + (raw * invMaxRaw) * (hi - lo);
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
            double[][] cornerColors, float u, float v, Span<double> interpBuffer, Span<double> evalBuffer)
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

            int written = shading.Eval(interpBuffer.Slice(0, components), evalBuffer);
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
        /// Builds the shading-global parametric colour LUT for a function-based mesh shading.
        /// The Function + colour-space conversion depend only on the single parametric value, so
        /// the mapping is identical for every patch and texel — evaluate it once over the domain
        /// <c>[<paramref name="domainLo"/>, <paramref name="domainHi"/>]</c> and reuse it.
        /// </summary>
        private static uint[] BuildParametricColorLut(Shading shading, CurrentGraphicsState currentState,
            double domainLo, double domainHi)
        {
            ColorSpaceDetails colorSpace = shading.ColorSpace;
            double alpha = currentState.AlphaConstantNonStroking;

            // Heap scratch captured by the evaluator; only touched PatchTextureLutSize times total.
            double[] evalIn = new double[1];
            double[] evalOut = new double[ShadingEvalBufferSize];

            SKColor Eval(double t)
            {
                evalIn[0] = t;
                int written = shading.Eval(evalIn, evalOut);
                return colorSpace.GetSKColor(new ReadOnlySpan<double>(evalOut, 0, written), alpha);
            }

            var lut = new uint[PatchTextureLutSize];
            ParametricShadingTexture.BuildLut(Eval, domainLo, domainHi, lut);
            return lut;
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
            double[][] cornerColors, int texSize, uint[]? lut, double domainLo, double domainHi)
        {
            var bitmap = new SKBitmap(texSize, texSize, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            int components = cornerColors[0].Length;

            // Degenerate stream (Decode array of length 4 → zero colour components): nothing to
            // sample. Return the zero-initialised (fully transparent) bitmap rather than running
            // the per-texel loop with an empty interp/Eval input.
            if (components == 0)
            {
                return bitmap;
            }

            // Fast path: when a Function is present the stream carries a single parametric value,
            // so the colour is a 1-D function of the bilinear-blended corner scalars. Look it up in
            // the shading-global LUT instead of evaluating the Function + colour space per texel.
            if (lut is not null && components == 1)
            {
                ParametricShadingTexture.Fill(bitmap.GetPixelSpan(), texSize,
                    cornerColors[0][0], cornerColors[1][0], cornerColors[2][0], cornerColors[3][0],
                    lut, domainLo, domainHi);
                return bitmap;
            }

            Span<double> interp = components <= 32 ? stackalloc double[components] : new double[components];
            float invDen = 1f / (texSize - 1);
            double alpha = currentState.AlphaConstantNonStroking;
            ColorSpaceDetails colorSpace = shading.ColorSpace;

            // Hoist the 4 corner component arrays out of the inner loop — index per slot
            // once, blend per k. Reads are sequential through cc0..cc3, friendlier to the
            // prefetcher than the previous cornerColors[0..3][k] pattern.
            ReadOnlySpan<double> cc0 = cornerColors[0];
            ReadOnlySpan<double> cc1 = cornerColors[1];
            ReadOnlySpan<double> cc2 = cornerColors[2];
            ReadOnlySpan<double> cc3 = cornerColors[3];

            Span<byte> pixelBytes = bitmap.GetPixelSpan();
            int rowStride = texSize * 4;

            // Per-pixel Eval buffer — keeps the 262 K-iteration inner loop allocation-free.
            Span<double> patchEvalOut = stackalloc double[ShadingEvalBufferSize];

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

                    int written = shading.Eval(interp, patchEvalOut);
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
            // The image / shader / paint are disposed at the end of this method, while it still runs
            // inside the mesh-picture recording (DrawCoonsMeshUnclipped / DrawTensorMeshUnclipped).
            // That is safe: DrawVertices records a copy of the paint into the recorder, which takes
            // its own native ref on the shader, which refs the image (SKImage.FromBitmap copies the
            // bitmap's pixels). So the recorded mesh picture keeps the texture alive after these
            // wrappers are disposed, and the page picture in turn keeps the mesh picture alive.
            // (Verified by MeshShadingDisposalTests.)
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
