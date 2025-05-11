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
                        var colour = GetCurrentState().CurrentNonStrokingColor.ToSKColor(1);
                        
                        byte refByte = image.NeedsReverseDecode() ? byte.MaxValue : byte.MinValue;

                        using (var skImagePixels = skImage.PeekPixels())
                        using (var alphaMask = new SKBitmap(skImage.Width, skImage.Height, SKColorType.Bgra8888, SKAlphaType.Premul))
                        {
                            var span = skImagePixels.GetPixelSpan();

                            for (int y = 0; y < skImage.Height; y++)
                            {
                                for (int x = 0; x < skImage.Width; x++)
                                {
                                    byte pixel = span[(y * skImage.Width) + x];
                                    if (pixel == refByte)
                                    {
                                        alphaMask.SetPixel(x, y, colour);
                                    }
                                }
                            }

                            _canvas.DrawBitmap(alphaMask, destRect, _paintCache.GetAntialiasing());
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
