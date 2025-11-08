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
                //paint.BlendMode = GetCurrentState().BlendMode.ToSKBlendMode(); // TODO - check if correct

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
                double tx = t0 + (t / (double)factor * t1);
                double[] v = shading.Eval(tx);

                FixIncorrectValues(v, domain); // This is a hack, this should never happen

                colors[t] = shading.ColorSpace.GetColor(v).ToSKColor(currentState.AlphaConstantNonStroking);
                // TODO - is it non stroking??
                colorPos[t] = (float)tx;
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

                // check if bbox not null

                if (isStroke)
                {
                    // TODO - To finish
                    paint.Style = SKPaintStyle.Stroke;
                    paint.StrokeWidth = (float)currentState.LineWidth;
                    paint.StrokeJoin = currentState.JoinStyle.ToSKStrokeJoin();
                    paint.StrokeCap = currentState.CapStyle.ToSKStrokeCap();
                    paint.PathEffect = currentState.LineDashPattern.ToSKPathEffect();
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
                double tx = t0 + (t / (double)factor * t1);
                double[] v = shading.Eval(tx);

                FixIncorrectValues(v, domain); // This is a hack, this should never happen, see GHOSTSCRIPT-693154-0

                colors[t] = shading.ColorSpace.GetColor(v).ToSKColor(currentState.AlphaConstantNonStroking); // TODO - is it non stroking??
                colorPos[t] = (float)tx;
            }

            using (var shader = SKShader.CreateLinearGradient(new SKPoint(x0, y0), new SKPoint(x1, y1), colors, colorPos, SKShaderTileMode.Clamp, patternTransformMatrix))
            using (var paint = new SKPaint())
            {
                paint.IsAntialias = shading.AntiAlias;
                paint.Shader = shader;

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

            byte[] raster = new byte[w * h * 4];
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

            var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);

            // get a pointer to the buffer, and give it to the skImage
            var ptr = GCHandle.Alloc(raster, GCHandleType.Pinned);

            var finalShadingMatrix = patternTransformMatrix.PreConcat(shading.Matrix.ToSkMatrix());

            using (SKPixmap pixmap = new SKPixmap(info, ptr.AddrOfPinnedObject(), info.RowBytes))
            using (SKImage skImage2 = SKImage.FromPixels(pixmap, (addr, ctx) =>
                {
                    ptr.Free();
                    raster = null!;
                }))
            using (var shader = SKShader.CreateImage(skImage2, SKShaderTileMode.Decal, SKShaderTileMode.Decal, finalShadingMatrix))
            using (var paint = new SKPaint())
            {
                paint.IsAntialias = shading.AntiAlias;
                paint.Shader = shader;

                SKPathEffect? dash = null;
                if (isStroke)
                {
                    var currentState = GetCurrentState();

                    // TODO - To Check
                    paint.Style = SKPaintStyle.Stroke;
                    paint.StrokeWidth = (float)currentState.LineWidth;
                    paint.StrokeJoin = currentState.JoinStyle.ToSKStrokeJoin();
                    paint.StrokeCap = currentState.CapStyle.ToSKStrokeCap();
                    paint.PathEffect = currentState.LineDashPattern.ToSKPathEffect();
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
