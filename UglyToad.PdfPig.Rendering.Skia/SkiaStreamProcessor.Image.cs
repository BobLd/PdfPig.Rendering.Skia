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
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SkiaSharp;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Rendering.Skia.Helpers;
using UglyToad.PdfPig.XObjects;

namespace UglyToad.PdfPig.Rendering.Skia
{
    internal partial class SkiaStreamProcessor
    {
        protected override void RenderXObjectImage(XObjectContentRecord xObjectContentRecord)
        {
            RenderImage(XObjectFactory.ReadImage(xObjectContentRecord, PdfScanner, FilterProvider, ResourceStore));
        }

        protected override void RenderInlineImage(InlineImage inlineImage)
        {
            RenderImage(inlineImage);
        }

        private void RenderImage(IPdfImage image)
        {
            if (image.WidthInSamples == 0 || image.HeightInSamples == 0)
            {
                return;
            }

            if (image.Bounds.Width == 0 || image.Bounds.Height == 0)
            {
                return;
            }

            var destRect = image.Bounds.ToSKRect(_height);

            try
            {
                using (new SKAutoCanvasRestore(_canvas, true))
                using (var skImage = image.GetSKImage())
                {
                    if (!(CurrentTransformationMatrix.A > 0) || !(CurrentTransformationMatrix.D > 0))
                    {
                        int sx = Math.Sign(CurrentTransformationMatrix.A);
                        int sy = Math.Sign(CurrentTransformationMatrix.D);

                        // Avoid passing a scale of 0
                        var matrix = SKMatrix.CreateScale(sx == 0 ? 1 : sx, sy == 0 ? 1 : sy);

                        _canvas.SetMatrix(matrix);
                        destRect = matrix.MapRect(destRect);
                    }

                    if (!image.IsImageMask)
                    {
                        _canvas.DrawImage(skImage, destRect, _paintCache.GetAntialiasing());
                    }
                    else
                    {
                        // Draw image mask
                        SKColor colour = GetCurrentState().CurrentNonStrokingColor.ToSKColor(GetCurrentState().AlphaConstantNonStroking);

                        /*
                        var maskShader = SKShader.CreateImage(skImage, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, SKMatrix.CreateScale(destRect.Width / skImage.Width, destRect.Height / skImage.Height));

                        using var paint = new SKPaint
                        {
                            Color = colour,
                            Shader = maskShader
                        };
                        _canvas.DrawRect(destRect, paint);
                        */

                        byte r = colour.Red;
                        byte g = colour.Green;
                        byte b = colour.Blue;
                        
                        using (var skImagePixels = skImage.PeekPixels())
                        {
                            var raster = new byte[skImage.Width * skImage.Height * 4]; // RGBA

                            Span<byte> span = skImagePixels.GetPixelSpan<byte>();
                            Span<byte> rasterSpan = raster;
                            
                            int i = 0;
                            for (int row = 0; row < skImage.Height; ++row)
                            {
                                for (int col = 0; col < skImage.Width; ++col)
                                {
                                    byte pixel = span[(row * skImage.Width) + col];
                                    if (pixel == byte.MinValue)
                                    {
                                        var start = (row * (skImage.Width * 4)) + (col * 4);
                                        rasterSpan[start] = r;
                                        rasterSpan[start + 1] = g;
                                        rasterSpan[start + 2] = b;
                                        rasterSpan[start + 3] = byte.MaxValue;
                                    }
                                }
                            }

                            var info = new SKImageInfo(skImage.Width, skImage.Height, SKColorType.Rgba8888, SKAlphaType.Premul);

                            // get a pointer to the buffer, and give it to the skImage
                            var ptr = GCHandle.Alloc(raster, GCHandleType.Pinned);

                            using (SKPixmap pixmap = new SKPixmap(info, ptr.AddrOfPinnedObject(), info.RowBytes))
                            using (SKImage skImage2 = SKImage.FromPixels(pixmap, (addr, ctx) =>
                                   {
                                       ptr.Free();
                                       raster = null;
                                       System.Diagnostics.Debug.WriteLine("ptr.Free()");
                                   }))
                            {
                                _canvas.DrawImage(skImage2, destRect, _paintCache.GetAntialiasing());
                            }
                        }
                    }
                }

                return;
            }
            catch (Exception ex)
            {
                // We have no way so far to know if skia will be able to draw the picture
                System.Diagnostics.Debug.WriteLine($"Render image attempt #1: {ex.Message}");
            }

#if DEBUG
            _canvas.DrawRect(destRect, _paintCache.GetImageDebug());
#endif
        }
    }
}
