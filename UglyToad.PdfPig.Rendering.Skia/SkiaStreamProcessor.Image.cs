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

            try
            {
                using (new SKAutoCanvasRestore(_canvas, true))
                using (var skImage = image.GetSKImage())
                {
                    // Images are upside down in PDF
                    _canvas.Scale(1, -1, 0, 0.5f);

                    if (!image.IsImageMask)
                    {
                        _canvas.DrawImage(skImage, new SKRect(0, 0, 1, 1), _paintCache.GetAntialiasing());
                    }
                    else
                    {
                        // Draw image mask
                        SKColor colour = GetCurrentState().CurrentNonStrokingColor.ToSKColor(GetCurrentState().AlphaConstantNonStroking);

                        /*
                        SKMatrix finalMatrix = SKMatrix.CreateScale(destRect.Width / skImage.Width, destRect.Height / skImage.Height)
                            .PostConcat(SKMatrix.CreateTranslation(destRect.Left, destRect.Top));

                        using (var shader = SKShader.CreateImage(skImage, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, finalMatrix))
                        using (var paint = new SKPaint())
                        {
                            paint.Color = colour;
                            paint.Shader = shader;
                            paint.IsAntialias = _antiAliasing;
                            _canvas.DrawRect(destRect, paint);
                        }
                        */

                        byte r = colour.Red;
                        byte g = colour.Green;
                        byte b = colour.Blue;

                        using (var skImagePixels = skImage.PeekPixels())
                        {
                            var raster = new byte[skImage.Width * skImage.Height * 4]; // RGBA

                            Span<byte> span = skImagePixels.GetPixelSpan<byte>();
                            Span<byte> rasterSpan = raster;

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
                                       //System.Diagnostics.Debug.WriteLine("ptr.Free()");
                                   }))
                            {
                                _canvas.DrawImage(skImage2, new SKRect(0, 0, 1, 1), _paintCache.GetAntialiasing());
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
            _canvas.DrawRect(new SKRect(0, 0, 1, 1), _paintCache.GetImageDebug());
#endif
        }
    }
}
