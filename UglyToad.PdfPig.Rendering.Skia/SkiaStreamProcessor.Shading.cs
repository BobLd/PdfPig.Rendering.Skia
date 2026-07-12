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

namespace UglyToad.PdfPig.Rendering.Skia;

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
        RenderShading(ResourceStore.GetShading(shadingNameToken), in SKMatrix.Identity, false, null);
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

        RenderShading(pattern.Shading, in patternTransform, isStroke, path);
    }

    /// <summary>
    /// Shared dispatch for the direct `sh` operator (identity transform, non-stroking) and
    /// shading patterns (pattern transform, optional stroke, clipped to the painted path).
    /// </summary>
    private void RenderShading(Shading shading, in SKMatrix patternTransformMatrix, bool isStroke, SKPath? path)
    {
        switch (shading)
        {
            case AxialShading axial:
                RenderAxialShading(axial, in patternTransformMatrix, isStroke, path);
                break;

            case RadialShading radial:
                RenderRadialShading(radial, in patternTransformMatrix, isStroke, path);
                break;

            case FunctionBasedShading functionBased:
                RenderFunctionBasedShading(functionBased, in patternTransformMatrix, isStroke, path);
                break;

            case FreeFormGouraudShading freeForm:
                RenderFreeFormGouraudShading(freeForm, in patternTransformMatrix, isStroke, path);
                break;

            case LatticeFormGouraudShading lattice:
                RenderLatticeFormGouraudShading(lattice, in patternTransformMatrix, path);
                break;

            case CoonsPatchMeshesShading coons:
                RenderCoonsPatchShading(coons, in patternTransformMatrix, path);
                break;

            case TensorProductPatchMeshesShading tensor:
                RenderTensorProductPatchShading(tensor, in patternTransformMatrix, path);
                break;
        }
    }

    /// <summary>
    /// PDF 1.7 §8.7.4.3: the shading's BBox is a temporary clipping boundary in the shading's
    /// target coordinate space, applied on top of the current clipping path. For a Type 2
    /// (shading) pattern that space is pattern space, so the rect is pushed through
    /// <paramref name="patternTransformMatrix"/> to bring it into canvas input coordinates
    /// (identity for the direct `sh` operator, where the BBox is already in user space).
    /// Returns <see langword="true"/> when a canvas save/clip was pushed — the caller must
    /// then <see cref="SKCanvas.Restore"/> once its drawing is done.
    /// </summary>
    private bool TryClipToShadingBBox(Shading shading, in SKMatrix patternTransformMatrix)
    {
        if (!shading.BBox.HasValue)
        {
            return false;
        }

        using var bboxPath = new SKPath();
        bboxPath.AddRect(shading.BBox.Value.ToSKRect());
        bboxPath.Transform(patternTransformMatrix);
        _canvas.Save();
        _canvas.ClipPath(bboxPath, SKClipOperation.Intersect, true);
        return true;
    }

    /// <summary>
    /// PDF 1.7 §8.7.4.5.4: when Background is present, paint that colour over the shading
    /// object's painted area before drawing the gradient, so that areas the gradient leaves
    /// unpainted (Extend=false, or outside a Type 1 Domain rectangle) show the declared
    /// Background instead of the page beneath. Callers skip the call when the gradient is
    /// guaranteed to cover everything (both Extend flags true).
    /// </summary>
    private void PaintShadingBackground(Shading shading, double alpha, SKBlendMode blendMode, SKPath? path)
    {
        if (shading.Background is null || shading.ColorSpace is null)
        {
            return;
        }

        using var bgPaint = new SKPaint();
        bgPaint.IsAntialias = shading.AntiAlias;
        bgPaint.Color = shading.ColorSpace.GetColor(shading.Background).ToSKColor(alpha);
        bgPaint.BlendMode = blendMode;

        if (path is null)
        {
            _canvas.DrawPaint(bgPaint);
        }
        else
        {
            _canvas.DrawPath(path, bgPaint);
        }
    }

    /// <summary>
    /// Shared epilogue of the axial / radial / function-based shading renderers: fills — or,
    /// when <paramref name="isStroke"/> is set, strokes with the current pen state
    /// (width / join / cap / dash) — the target with <paramref name="shader"/>.
    /// Draws <paramref name="path"/> when given, else paints the whole clip region (`sh`).
    /// <paramref name="paintAlpha"/> modulates the shader output (Skia multiplies the shader
    /// by the paint's alpha); pass 255 when alpha is already baked into the shader's colours.
    /// </summary>
    private void DrawShadingShader(SKShader shader, Shading shading, CurrentGraphicsState currentState,
        bool isStroke, SKPath? path, byte paintAlpha = byte.MaxValue)
    {
        using var paint = new SKPaint();
        paint.IsAntialias = shading.AntiAlias;
        paint.Shader = shader;
        paint.BlendMode = currentState.BlendMode.ToSKBlendMode();
        paint.Color = SKColors.White.WithAlpha(paintAlpha);

        SKPathEffect? dash = null;
        try
        {
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
        }
        finally
        {
            dash?.Dispose();
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
            dest[destOffset + i] = MapPointAffine(in patternTransformMatrix, (float)x, (float)y);
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
    /// Shared stream-reading loop for Type 6 (Coons, 12 boundary points per full patch) and
    /// Type 7 (Tensor, 16 control points) meshes. The record layout, the edge-continuation
    /// flag rules and the corner-colour bookkeeping are identical for the two types: a
    /// continuation patch (flag 1–3) reuses four boundary points and two corner colours of
    /// the previous patch, then reads the remaining <c>pointsPerPatch − 4</c> points and
    /// 2 colours (for Type 7 the four interior control points, indices 12–15, are always
    /// new). Only the per-patch tessellation differs, so the loop lives here once and
    /// dispatches on <paramref name="isTensor"/>.
    /// <para>
    /// When a Function is present, the colour is non-linear in the bilinear-interpolated
    /// parameter (most visibly: stitched Type-3 functions and Type-2 N=0 step functions).
    /// Per-vertex Gouraud interpolation can't represent these correctly inside a cell — a
    /// cell straddling a step boundary smears the two output colours together — so the
    /// Function path draws each patch with a pre-evaluated colour texture and
    /// texture-coordinate mapping, getting per-pixel function output.
    /// </para>
    /// </summary>
    private void DrawPatchMeshUnclipped(Shading shading, in SKMatrix patternTransformMatrix,
        ReadOnlySpan<byte> data, int bitsPerCoordinate, int bitsPerComponent, int bitsPerFlag,
        double[] decode, bool isTensor)
    {
        var currentState = GetCurrentState();

        int numStreamColorComponents = (decode.Length - 4) / 2;
        double maxCoordRaw = (1L << bitsPerCoordinate) - 1.0;
        double maxColorRaw = (1L << bitsPerComponent) - 1.0;
        double xMin = decode[0], xMax = decode[1];
        double yMin = decode[2], yMax = decode[3];

        bool hasFunction = shading.Functions is { Length: > 0 };

        // Per-shading scratch for the no-function (vertex-colour Gouraud) path. Each patch
        // is submitted via its own indexed DrawVertices call with exact-size vertex arrays,
        // so memory stays bounded regardless of mesh size.
        double[]? interpBuffer = null;
        SKPaint? gouraudPaint = null;

        if (!hasFunction)
        {
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

        int pointsPerPatch = isTensor ? 16 : 12;

        try
        {
            // Patch buffers are alternated between the current and previous patch via a
            // two-slot pool: the implicit-edge flags (1/2/3) require keeping the previous
            // patch alive, but at most one previous and one current patch are live at a
            // time. Pre-allocating both pairs lifts the point slots + 4 component arrays
            // out of the per-patch hot loop.
            var ptsBufA = new SKPoint[pointsPerPatch];
            var ptsBufB = new SKPoint[pointsPerPatch];
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
            var bitReader = new GouraudBitReader(data);

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

                int newPointCount = flag == 0 ? pointsPerPatch : pointsPerPatch - 4;
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
                        case 1: p11Idx = 3; p12Idx = 4; p13Idx = 5; p14Idx = 6; newC1ColorIdx = 1; newC2ColorIdx = 2;
                            break;
                        case 2: p11Idx = 6; p12Idx = 7; p13Idx = 8; p14Idx = 9; newC1ColorIdx = 2; newC2ColorIdx = 3;
                            break;
                        default: // flag is masked to two bits and 0 is handled above, so this is exactly flag == 3.
                            p11Idx = 9; p12Idx = 10; p13Idx = 11; p14Idx = 0; newC1ColorIdx = 3; newC2ColorIdx = 0;
                            break;
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
                    if (isTensor)
                    {
                        DrawTensorPatchTextured(shading, currentState, points, cornerColors, patchLut, domainLo, domainHi);
                    }
                    else
                    {
                        DrawCoonsPatchTextured(shading, currentState, points, cornerColors, patchLut, domainLo, domainHi);
                    }
                }
                else if (isTensor)
                {
                    TessellateAndDrawTensorPatch(shading, currentState, points, cornerColors,
                        interpBuffer!, gouraudPaint!);
                }
                else
                {
                    TessellateAndDrawCoonsPatch(shading, currentState, points, cornerColors,
                        interpBuffer!, gouraudPaint!);
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
    /// Submits the (n+1)² colour grid as an indexed triangle list (two triangles per cell)
    /// in a single DrawVertices call. The index buffer depends only on <paramref name="n"/>
    /// and comes from the shared cache. The vertex arrays must be exactly (n+1)² long:
    /// DrawVertices takes the vertex count from Array.Length, so an oversized scratch
    /// buffer would copy stale vertices into the recorded picture.
    /// </summary>
    private void DrawGridTriangles(SKPoint[] grid, SKColor[] gridCol, int n, SKPaint paint)
    {
        _canvas.DrawVertices(SKVertexMode.Triangles, grid, null, gridCol,
            SKBlendMode.Modulate, GetGridTriangleIndices(n), paint);
    }

    // Index buffers for the (n+1)² grid triangulation and the matching texture-coordinate
    // grids depend only on the subdivision count — never on the patch geometry — so they are
    // built once per n and shared process-wide. The unsynchronised publish is a benign race:
    // the content is deterministic and never mutated after creation, and .NET reference
    // stores have release semantics, so a racing reader either sees null (and rebuilds the
    // identical array) or a fully initialised one.
    private static readonly ushort[]?[] GridTriangleIndexCache = new ushort[PatchSubdivisions + 1][];
    private static readonly SKPoint[]?[] PatchTexCoordCache = new SKPoint[PatchSubdivisions + 1][];

    /// <summary>
    /// Returns the index buffer triangulating an (n+1)×(n+1) vertex grid (row-major, index
    /// <c>j·(n+1) + i</c>) into two triangles per cell: cell (i,j) connects vertices at
    /// (i, j), (i+1, j), (i, j+1), (i+1, j+1). Winding matches the expanded triangle list
    /// this replaces, so rendering is triangle-for-triangle identical.
    /// </summary>
    private static ushort[] GetGridTriangleIndices(int n)
    {
        ushort[]? indices = GridTriangleIndexCache[n];
        if (indices is not null)
        {
            return indices;
        }

        indices = new ushort[n * n * 6];
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

                indices[w++] = (ushort)i00; indices[w++] = (ushort)i10; indices[w++] = (ushort)i01;
                indices[w++] = (ushort)i10; indices[w++] = (ushort)i11; indices[w++] = (ushort)i01;
            }
        }

        GridTriangleIndexCache[n] = indices;
        return indices;
    }

    /// <summary>
    /// Returns the (n+1)² texture-coordinate grid mapping (u,v) ∈ [0,1]² onto the
    /// [0, <see cref="PatchTextureSize"/>−1]² texel space used by the textured patch path.
    /// Identical for every patch at a given subdivision count, hence cached.
    /// </summary>
    private static SKPoint[] GetPatchTexCoords(int n)
    {
        SKPoint[]? texCoords = PatchTexCoordCache[n];
        if (texCoords is not null)
        {
            return texCoords;
        }

        int axisLen = n + 1;
        texCoords = new SKPoint[axisLen * axisLen];
        float invN = 1f / n;
        const float texScale = PatchTextureSize - 1;
        for (int j = 0; j < axisLen; j++)
        {
            float v = j * invN * texScale;
            int rowOffset = j * axisLen;
            for (int i = 0; i < axisLen; i++)
            {
                texCoords[rowOffset + i] = new SKPoint(i * invN * texScale, v);
            }
        }

        PatchTexCoordCache[n] = texCoords;
        return texCoords;
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
    /// Submits the texture-mapped patch grid as an indexed triangle list with a
    /// nearest-neighbour bitmap shader. Nearest sampling preserves sharp step-function
    /// transitions stored in the colour texture (linear filtering would smear them into a
    /// multi-pixel band). <paramref name="positions"/> / <paramref name="texCoords"/> must
    /// be exactly (n+1)² long (DrawVertices takes the vertex count from Array.Length);
    /// <paramref name="indices"/> comes from <see cref="GetGridTriangleIndices"/>.
    /// </summary>
    private void DrawTexturedPatchVertices(Shading shading, CurrentGraphicsState currentState,
        SKBitmap bitmap, SKPoint[] positions, SKPoint[] texCoords, ushort[] indices)
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

        _canvas.DrawVertices(SKVertexMode.Triangles, positions, texCoords, null,
            SKBlendMode.SrcOver, indices, paint);
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
                FilterProvider, new CropBox(pattern.BBox), UserSpaceUnit, default,
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

    private static SKShaderTileMode GetSKShaderTileMode(bool[] extend)
    {
        // PDF Extend controls whether the gradient continues past the start/end circles.
        // Skia's tile mode on a two-point conical gradient is the closest equivalent:
        //   Both true   → Clamp  (t=0/t=1 colours bleed to infinity)
        //   Both false  → Decal  (areas outside the gradient are transparent)
        // Mixed extends have no exact tile-mode counterpart; Decal keeps at least the
        // non-extending side correct, and the extending side is rare enough in practice
        // that we accept the imperfection rather than rasterising by hand.

        return extend[0] && extend[1]
            ? SKShaderTileMode.Clamp
            : SKShaderTileMode.Decal;
    }
}
