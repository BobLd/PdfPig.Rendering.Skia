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
            /*
             * See https://groups.google.com/g/skia-discuss/c/Y5OUBx0_eHY
             *
             * Brian Osman:
             * '''
             * Sorry, forgot one thing: If you have an SkImage and want to make sure that it's fully decoded (so it will be fast to draw), you can do:
             *      image = image->makeRasterImage();   // Produces an image that will be fully decoded and fast to draw on the CPU
             * -or-
             *      image = image->makeTextureImage(context);  // Produces an image that will be fully decoded and uploaded to a texture, so it's fast to draw on the GPU
             * '''
             */

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
                if (CurrentTransformationMatrix.A > 0 && CurrentTransformationMatrix.D > 0)
                {
                    // No transformation to do
                    using (var skImage = SKImage.FromEncodedData(image.GetImageBytes()))
                    {
                        _canvas.DrawImage(skImage, destRect, _paintCache.GetAntialiasing());
                    }
                }
                else
                {
                    SKMatrix matrix = SKMatrix.CreateScale(
                        Math.Sign(CurrentTransformationMatrix.A),
                        Math.Sign(CurrentTransformationMatrix.D));

                    using (var skImage = SKImage.FromEncodedData(image.GetImageBytes()))
                    using (new SKAutoCanvasRestore(_canvas, true))
                    {
                        _canvas.SetMatrix(matrix);
                        _canvas.DrawImage(skImage, matrix.MapRect(destRect), _paintCache.GetAntialiasing());
                    }
                }
            }
            catch (Exception ex)
            {
#if DEBUG

                // We have no way so far to know if skia will be able to draw the picture
                System.Diagnostics.Debug.WriteLine($"Render image attempt #1: {ex.Message}");
                _canvas.DrawRect(destRect, _paintCache.GetImageDebug());
#endif
            }
        }
    }
}
