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
using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;
using UglyToad.PdfPig.Core;
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
                case ShadingType.CoonsPatch:
                case ShadingType.TensorProductPatch:
                default:
                    RenderUnsupportedShading(shading, in SKMatrix.Identity);
                    break;
            }
        }

        private void RenderUnsupportedShading(Shading shading, in SKMatrix patternTransformMatrix)
        {
#if DEBUG
            using (var shader = SKShader.CreateLinearGradient(SKPoint.Empty, new SKPoint(0, 1), new[] { SKColors.Red, SKColors.Green }, SKShaderTileMode.Clamp))
            using (var paint = new SKPaint())
            {
                paint.IsAntialias = shading.AntiAlias;
                paint.Shader = shader;
                paint.BlendMode = GetCurrentState().BlendMode.ToSKBlendMode();

                // check if bbox not null

                _canvas.DrawPaint(paint);
            }
#endif
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
                double normalized = t / (double)factor;
                double tx = t0 + normalized * (t1 - t0);
                double[] v = shading.Eval(tx);

                FixIncorrectValues(v, domain); // This is a hack, this should never happen

                colors[t] = shading.ColorSpace.GetColor(v).ToSKColor(currentState.AlphaConstantNonStroking);
                // TODO - is it non stroking??
                colorPos[t] = (float)normalized;
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
                double normalized = t / (double)factor;
                double tx = t0 + normalized * (t1 - t0);
                double[] v = shading.Eval(tx);

                FixIncorrectValues(v, domain); // This is a hack, this should never happen, see GHOSTSCRIPT-693154-0

                colors[t] = shading.ColorSpace.GetColor(v).ToSKColor(currentState.AlphaConstantNonStroking); // TODO - is it non stroking??
                colorPos[t] = (float)normalized;
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

            // Storage for the triangles that will be passed to DrawVertices.
            // Each triangle contributes exactly 3 entries (one per corner).
            var positions = new List<SKPoint>();
            var colors = new List<SKColor>();

            // The three corners of the most-recently completed triangle.
            // Required for edge-sharing flags 1 and 2.
            var prevTri = new (SKPoint pt, SKColor col)[3];
            bool hasPrevTri = false;

            // Accumulator for consecutive flag-0 vertices (need 3 to form a free triangle).
            var flag0Buf = new (SKPoint pt, SKColor col)[3];
            int flag0Count = 0;

            var bitReader = new GouraudBitReader(shading.Data.Span);

            while (bitReader.HasData)
            {
                int flag;
                long rawX, rawY;
                double[] rawC = new double[numStreamColorComponents];

                try
                {
                    flag = (int)(bitReader.ReadBits(bitsPerFlag) & 3);
                    rawX = bitReader.ReadBits(bitsPerCoordinate);
                    rawY = bitReader.ReadBits(bitsPerCoordinate);
                    for (int i = 0; i < numStreamColorComponents; i++)
                    {
                        rawC[i] = bitReader.ReadBits(bitsPerComponent);
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

                // Decode colour components.
                double[] colorComponents = new double[numStreamColorComponents];
                for (int i = 0; i < numStreamColorComponents; i++)
                {
                    double cMin = decode[4 + i * 2];
                    double cMax = decode[5 + i * 2];
                    colorComponents[i] = cMin + (rawC[i] / maxColorRaw) * (cMax - cMin);
                }

                // Evaluate optional function and convert to an SKColor through the colour space.
                double[] evalResult = shading.Eval(colorComponents);
                SKColor skColor = shading.ColorSpace.GetColor(evalResult).ToSKColor(currentState.AlphaConstantNonStroking);

                // Transform the vertex from shading/pattern space to canvas space.
                SKPoint pt = patternTransformMatrix.MapPoint(new SKPoint((float)x, (float)y));

                // Build triangles according to the edge-flag value (PDF spec Table 92).
                switch (flag)
                {
                    case 0:
                        // Accumulate free vertices; emit a triangle once three are collected.
                        flag0Buf[flag0Count] = (pt, skColor);
                        flag0Count++;

                        if (flag0Count == 3)
                        {
                            for (int i = 0; i < 3; i++)
                            {
                                positions.Add(flag0Buf[i].pt);
                                colors.Add(flag0Buf[i].col);
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
                            positions.Add(prevTri[1].pt);
                            positions.Add(prevTri[2].pt);
                            positions.Add(pt);
                            colors.Add(prevTri[1].col);
                            colors.Add(prevTri[2].col);
                            colors.Add(skColor);

                            // Slide the window: new prevTri = [prevTri[1], prevTri[2], newVertex]
                            prevTri[0] = prevTri[1];
                            prevTri[1] = prevTri[2];
                            prevTri[2] = (pt, skColor);
                            flag0Count = 0;
                        }
                        break;

                    case 2:
                        // New triangle shares edge prevTri[0]–prevTri[2] with the previous triangle.
                        if (hasPrevTri)
                        {
                            positions.Add(prevTri[0].pt);
                            positions.Add(prevTri[2].pt);
                            positions.Add(pt);
                            colors.Add(prevTri[0].col);
                            colors.Add(prevTri[2].col);
                            colors.Add(skColor);

                            // Slide the window: new prevTri = [prevTri[0], prevTri[2], newVertex]
                            prevTri[1] = prevTri[2];
                            prevTri[2] = (pt, skColor);
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

            // Use canvas bounds to determine bitmap resolution, capped to avoid excessive memory use
            int w = Math.Max(2, Math.Min(512, (int)Math.Ceiling(Math.Abs(maxX - minX))));
            int h = Math.Max(2, Math.Min(512, (int)Math.Ceiling(Math.Abs(maxY - minY))));

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
                        // Map pixel (i, j) to domain coordinates via linear interpolation
                        values[0] = x0 + (double)i / (w - 1) * (x1 - x0);
                        values[1] = y0 + (double)j / (h - 1) * (y1 - y0);
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

                // Build local matrix mapping bitmap pixel space → canvas space.
                // SKShader.CreateBitmap local matrix follows the same convention as gradient shaders:
                // it maps FROM shader/bitmap local space TO canvas space (Skia inverts it when sampling).
                // Pipeline: pixel(i,j) → domain(x,y) → shading target space → canvas
                //   pixel → domain:  Translate(x0,y0) × Scale(dx, dy)
                //   domain → shading: shading.Matrix
                //   shading → canvas: patternTransformMatrix
                float dx = (w > 1) ? (float)((x1 - x0) / (w - 1)) : 1f;
                float dy = (h > 1) ? (float)((y1 - y0) / (h - 1)) : 1f;
                var finalShadingMatrix = SKMatrix.CreateScale(dx, dy)
                    .PostConcat(SKMatrix.CreateTranslation((float)x0, (float)y0))
                    .PostConcat(shading.Matrix.ToSkMatrix())
                    .PostConcat(patternTransformMatrix);

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
                case ShadingType.CoonsPatch:
                case ShadingType.TensorProductPatch:
                default:
                    RenderUnsupportedShading(pattern.Shading, in patternTransform);
                    break;
            }
        }

        private void RenderTilingPatternCurrentPath(TilingPatternColor pattern, bool isStroke)
        {
            RenderTilingPattern(_currentPath, pattern, isStroke);
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
            // TODO - to finish
            // See 22060_A1_01_Plans-1.pdf
            // And Apitron.PDF.Kit.Samples_patternFill.pdf

            // For Uncoloured;
            // - gs-bugzilla694385 

            if (pattern.PaintType == PatternPaintType.Uncoloured)
            {
                // TODO - not supported for the moment
                return;
            }

            var operations = PageContentParser.Parse(PageNumber, new MemoryInputBytes(pattern.Data), ParsingOptions.Logger);

            bool hasResources = pattern.PatternStream.StreamDictionary.TryGet(NameToken.Resources, PdfScanner, out DictionaryToken? resourcesDictionary);

            if (hasResources)
            {
                ResourceStore.LoadResourceDictionary(resourcesDictionary!);
            }

            // https://github.com/apache/pdfbox/blob/trunk/pdfbox/src/main/java/org/apache/pdfbox/contentstream/PDFStreamEngine.java#L370

            var processor = new SkiaStreamProcessor(PageNumber, ResourceStore, PdfScanner, PageContentParser,
                FilterProvider, new Content.CropBox(pattern.BBox), UserSpaceUnit, Rotation,
                pattern.Matrix, ParsingOptions, null, _fontCache);

            /*
            if (pattern.PaintType == PatternPaintType.Uncoloured)
            {
                var cs = (this.GetCurrentState().ColorSpaceContext.CurrentNonStrokingColorSpace as PatternColorSpaceDetails).UnderlyingColourSpace;
                processor.GetCurrentState().ColorSpaceContext.SetStrokingColorspace(cs);
                processor.GetCurrentState().CurrentStrokingColor = pattern.CurrentColor;
                processor.GetCurrentState().ColorSpaceContext.SetNonStrokingColorspace(cs);
                processor.GetCurrentState().CurrentNonStrokingColor = pattern.CurrentColor;
            }
            */

            // Installs the graphics state that was in effect at the beginning of the pattern’s parent content stream,
            // with the current transformation matrix altered by the pattern matrix as described in 8.7.2, "General properties of patterns"

            SKRect rect = SKRect.Create(Math.Abs((float)pattern.XStep), Math.Abs((float)pattern.YStep));

            // We cancel CTM, but not canvas' Y flip, as we still need it.
            // We are drawing a SKPicture, we need to flip the Y axis of this picture.
            var transformMatrix = CurrentTransformationMatrix.ToSkMatrix().Invert()
                .PreConcat(_currentStreamOriginalTransforms.Peek())
                .PreConcat(SKMatrix.CreateScale(1, -1, 0, (float)pattern.BBox.Height / 2f));

            using (var picture = processor.Process(PageNumber, operations))
            using (var shader = SKShader.CreatePicture(picture, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, SKFilterMode.Linear, transformMatrix, rect))
            using (var paint = new SKPaint())
            {
                paint.IsAntialias = _antiAliasing;
                paint.Shader = shader;
                paint.BlendMode = GetCurrentState().BlendMode.ToSKBlendMode();

//#if DEBUG
//                _canvas.DrawPath(path, new SKPaint() { Color = SKColors.Blue.WithAlpha(150) });
//                _canvas.DrawPath(path, new SKPaint() { Color = SKColors.Red.WithAlpha(150), IsStroke = true, StrokeWidth = 5 });
//#endif
                
                _canvas.DrawPath(path, paint);
            }

            if (hasResources)
            {
                ResourceStore.UnloadResourceDictionary();
            }
        }
    }
}
