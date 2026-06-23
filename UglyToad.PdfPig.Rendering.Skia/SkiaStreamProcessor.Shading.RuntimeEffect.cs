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
    /// Lower bound on the LUT width; below this even a tiny gradient shows banding.
    /// </summary>
    private const int MinRampStops = 16;

    /// <summary>
    /// Upper bound on the LUT width. The LUT is sized to the gradient's on-screen
    /// extent so step-function transitions stay within a pixel; this cap bounds the
    /// bake cost / texture size for very large gradients, beyond which a step is
    /// already sub-pixel-thin relative to the whole sweep.
    /// </summary>
    private const int MaxRampStops = 2048;

    private const string AxialShaderSksl =
        """
        uniform float2 p0;          // axis start  (shading space)
        uniform float2 p1;          // axis end
        uniform float  extendStart; // 1.0 if Extend[0]
        uniform float  extendEnd;   // 1.0 if Extend[1]
        uniform float  lutW;        // LUT width N (texels)
        uniform shader lut;

        half4 main(float2 p) {
            float2 axis = p1 - p0;
            float  len2 = dot(axis, axis);

            float s = (len2 > 0.0) ? dot(p - p0, axis) / len2 : 0.0;

            if (s < 0.0) {
                if (extendStart < 0.5) { return half4(0.0); }
                s = 0.0;
            } else if (s > 1.0) {
                if (extendEnd < 0.5) { return half4(0.0); }
                s = 1.0;
            }

            float texel = s * (lutW - 1.0) + 0.5;
            return lut.eval(float2(texel, 0.5));
        }
        """;

    private const string RadialShaderSksl =
        """
        uniform float2 c0;          // circle 0 centre (shading space)
        uniform float  r0;          // circle 0 radius
        uniform float2 c1;          // circle 1 centre
        uniform float  r1;          // circle 1 radius
        uniform float  extendStart; // 1.0 if Extend[0]
        uniform float  extendEnd;   // 1.0 if Extend[1]
        uniform float  lutW;
        uniform shader lut;

        half4 main(float2 p) {
            float2 dc = c1 - c0;
            float  dr = r1 - r0;
            float2 pc = p - c0;

            float a = dot(dc, dc) - dr * dr;
            float b = dot(pc, dc) + r0 * dr;
            float c = dot(pc, pc) - r0 * r0;

            float sLo = (extendStart > 0.5) ? -1e9 : 0.0;
            float sHi = (extendEnd   > 0.5) ?  1e9 : 1.0;

            float sBest = -1e30;   // sentinel: nothing found

            if (abs(a) < 1e-7) {
                // Degenerate (equal radius rate): linear equation -2b*s + c = 0.
                if (abs(b) > 1e-7) {
                    float s = c / (2.0 * b);
                    if (r0 + s * dr >= 0.0 && s >= sLo && s <= sHi) { sBest = s; }
                }
            } else {
                float det = b * b - a * c;
                if (det >= 0.0) {
                    float sq = sqrt(det);
                    float sHigh = max((b + sq) / a, (b - sq) / a);
                    float sLow  = min((b + sq) / a, (b - sq) / a);

                    // Prefer the largest s whose circle is valid (radius >= 0, in range).
                    if (r0 + sHigh * dr >= 0.0 && sHigh >= sLo && sHigh <= sHi) {
                        sBest = sHigh;
                    } else if (r0 + sLow * dr >= 0.0 && sLow >= sLo && sLow <= sHi) {
                        sBest = sLow;
                    }
                }
            }

            if (sBest < -1e29) { return half4(0.0); }   // not painted

            // Extended regions take the colour of the nearest boundary circle.
            float sColor = clamp(sBest, 0.0, 1.0);
            float texel = sColor * (lutW - 1.0) + 0.5;
            return lut.eval(float2(texel, 0.5));
        }
        """;

    private static readonly Lazy<SKRuntimeEffect> AxialEffect =
        new(() => CompileEffect(AxialShaderSksl, nameof(AxialEffect)));

    private static readonly Lazy<SKRuntimeEffect> RadialEffect =
        new(() => CompileEffect(RadialShaderSksl, nameof(RadialEffect)));

    private static SKRuntimeEffect CompileEffect(string sksl, string name)
    {
        SKRuntimeEffect effect = SKRuntimeEffect.CreateShader(sksl, out string errors);
        if (effect is null || !string.IsNullOrEmpty(errors))
        {
            throw new InvalidOperationException($"Failed to compile SKSL shading effect '{name}': {errors}");
        }

        return effect;
    }

    /// <summary>
    /// Bakes the shading's colour ramp into an <c>n</c>×1 Rgba8888 image: texel <c>k</c> holds the
    /// final colour at parametric position <c>s = k / (n-1)</c>, i.e. the PDF Function evaluated at
    /// <c>t = t0 + s·(t1−t0)</c> then converted through the shading colour space. This is the per-pixel
    /// step SKSL cannot perform; the shader then only samples this LUT. Caller owns the returned image.
    /// </summary>
    private static SKImage BuildShadingRampImage(Shading shading, double alpha, float t0, float t1, int n,
        ReadOnlySpan<double> domain)
    {
        using var bitmap = new SKBitmap(n, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        ColorSpaceDetails colorSpace = shading.ColorSpace;

        Span<double> evalIn = stackalloc double[1];
        Span<double> evalOut = stackalloc double[ShadingEvalBufferSize];
        Span<byte> px = bitmap.GetPixelSpan();

        float invDen = n > 1 ? 1f / (n - 1) : 0f;
        for (int k = 0; k < n; k++)
        {
            float s = k * invDen;
            evalIn[0] = t0 + s * (t1 - t0);
            int written = shading.Eval(evalIn, evalOut);
            Span<double> v = evalOut.Slice(0, written);

            FixIncorrectValues(v, domain);

            SKColor color = colorSpace.GetSKColor(v, alpha);
            int idx = k * 4;
            px[idx] = color.Red;
            px[idx + 1] = color.Green;
            px[idx + 2] = color.Blue;
            px[idx + 3] = color.Alpha;
        }

        return SKImage.FromBitmap(bitmap);
    }

    /// <summary>
    /// Picks a LUT width for a gradient whose on-screen extent (in device pixels) is
    /// <paramref name="deviceExtent"/>: one texel per device pixel, clamped to
    /// [<see cref="MinRampStops"/>, <see cref="MaxRampStops"/>]. Matches the resolution the old
    /// per-pixel colour-stop sampling used, so step transitions stay pixel-sharp.
    /// </summary>
    private static int RampStopsForExtent(float deviceExtent)
    {
        int n = (int)Math.Ceiling(deviceExtent);
        if (n < MinRampStops)
        {
            return MinRampStops;
        }
        
        return n > MaxRampStops ? MaxRampStops : n;
    }

    /// <summary>
    /// Builds the axial (Type 2) SKSL shader. Geometry uniforms are in shading space; the
    /// <paramref name="localMatrix"/> (the pattern transform) maps that space into the canvas-local
    /// space, so the shader receives points already in shading space — exactly as the colour-stop
    /// gradient path passed <c>patternTransformMatrix</c> as its local matrix. The returned shader
    /// holds native refs on the LUT child / image, so those wrappers are disposed here.
    /// </summary>
    private static SKShader CreateAxialShader(float x0, float y0, float x1, float y1,
        bool extendStart, bool extendEnd, int lutWidth, SKImage lut, in SKMatrix localMatrix)
    {
        using SKShader lutShader = lut.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));

        using var uniforms = new SKRuntimeEffectUniforms(AxialEffect.Value);
        uniforms["p0"] = new[] { x0, y0 };
        uniforms["p1"] = new[] { x1, y1 };
        uniforms["extendStart"] = extendStart ? 1f : 0f;
        uniforms["extendEnd"] = extendEnd ? 1f : 0f;
        uniforms["lutW"] = (float)lutWidth;

        using var children = new SKRuntimeEffectChildren(AxialEffect.Value);
        children["lut"] = lutShader;


        return AxialEffect.Value.ToShader(uniforms, children, localMatrix);
    }

    /// <summary>
    /// Builds the radial (Type 3) SKSL shader (see <see cref="CreateAxialShader"/> for the
    /// coordinate-space / disposal contract). Unlike the two-point conical gradient it replaces, this
    /// honours each Extend flag independently and solves the PDF circle-family equation exactly.
    /// </summary>
    private static SKShader CreateRadialShader(float x0, float y0, float r0, float x1, float y1, float r1,
        bool extendStart, bool extendEnd, int lutWidth, SKImage lut, in SKMatrix localMatrix)
    {
        using SKShader lutShader = lut.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));

        using var uniforms = new SKRuntimeEffectUniforms(RadialEffect.Value);
        uniforms["c0"] = new[] { x0, y0 };
        uniforms["r0"] = r0;
        uniforms["c1"] = new[] { x1, y1 };
        uniforms["r1"] = r1;
        uniforms["extendStart"] = extendStart ? 1f : 0f;
        uniforms["extendEnd"] = extendEnd ? 1f : 0f;
        uniforms["lutW"] = (float)lutWidth;

        using var children = new SKRuntimeEffectChildren(RadialEffect.Value);
        children["lut"] = lutShader;

        return RadialEffect.Value.ToShader(uniforms, children, localMatrix);
    }
}
