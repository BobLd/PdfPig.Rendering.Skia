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

        private void RenderImage(IPdfImage pdfImage)
        {
            if (pdfImage.WidthInSamples == 0 || pdfImage.HeightInSamples == 0)
            {
                return;
            }

            if (pdfImage.Bounds.Width == 0 || pdfImage.Bounds.Height == 0)
            {
                return;
            }

            SKBitmap? bitmap = null;
            SKBitmap? alphaMask = null;

            try
            {
                using SKAutoCanvasRestore skAutoCanvasRestore = new SKAutoCanvasRestore(_canvas, true);

                if (!pdfImage.TryGenerate(out bitmap, out alphaMask, ParsingOptions.Logger))
                {
                    // Fall back to encoded / raw byte decoding (no separate mask in that path).
                    bitmap = pdfImage.GetSKBitmap(ParsingOptions.Logger);
                    alphaMask = null;
                }

                if (bitmap is null)
                {
                    throw new NullReferenceException("Got a null image.");
                }

                // Images are upside down in PDF
                _canvas.Scale(1, -1, 0, 0.5f);

                var currentState = GetCurrentState();

                if (!pdfImage.IsImageMask)
                {
                    bitmap.SetImmutable();
                    var imagePaint = _paintCache.GetPaint(pdfImage, currentState.BlendMode);
                    using SKImage image = SKImage.FromBitmap(bitmap);

                    if (alphaMask is not null)
                    {
                        // Gray + SMask: composite via DstIn inside an isolated layer so the mask
                        // affects only this image, not whatever is already on the canvas. The
                        // outer layer paint carries the parent state's blend mode and antialias;
                        // the inner DrawImage uses default SrcOver into the empty layer.
                        using SKImage maskImage = SKImage.FromBitmap(alphaMask);
                        int saved = _canvas.SaveLayer(new SKRect(0, 0, 1, 1), imagePaint);
                        _canvas.DrawImage(image, new SKRect(0, 0, 1, 1), SKSamplingOptions.Default, null);
                        using SKPaint dstInPaint = new SKPaint { BlendMode = SKBlendMode.DstIn };
                        _canvas.DrawImage(maskImage, new SKRect(0, 0, 1, 1), SKSamplingOptions.Default, dstInPaint);
                        _canvas.RestoreToCount(saved);
                    }
                    else
                    {
                        _canvas.DrawImage(image, new SKRect(0, 0, 1, 1), SKSamplingOptions.Default, imagePaint);
                    }
                }
                else
                {
                    // Image mask: 1-bit stencil. The source bitmap is Gray8 in canonical PDF
                    // convention (0 = paint, 255 = transparent), so invert into an Alpha8 image
                    // (Alpha8 is set in GetSKBitmap) and let Skia composite the current
                    // non-stroking colour through it
                    System.Diagnostics.Debug.Assert(bitmap.ColorType == SKColorType.Alpha8);
                    System.Diagnostics.Debug.Assert(bitmap.AlphaType == SKAlphaType.Premul);

                    Span<byte> src = bitmap.GetPixelSpan();
                    for (int i = 0; i < src.Length; i++)
                    {
                        src[i] = (byte)~src[i];
                    }
                    bitmap.SetImmutable();

                    var maskPaint = _paintCache.GetPaint(currentState.CurrentNonStrokingColor,
                        currentState.AlphaConstantNonStroking, false, null, null, null, null,
                        currentState.BlendMode);
                    using SKImage image = SKImage.FromBitmap(bitmap);
                    _canvas.DrawImage(image, new SKRect(0, 0, 1, 1), SKSamplingOptions.Default, maskPaint);
                }
            }
            catch (Exception ex)
            {
                // We have no way so far to know if skia will be able to draw the picture
                ParsingOptions.Logger.Error($"Failed to render image: {ex}");
            }
            finally
            {
                bitmap?.Dispose();
                alphaMask?.Dispose();
            }

#if DEBUG
            _canvas.DrawRect(new SKRect(0, 0, 1, 1), _paintCache.GetImageDebug());
#endif
        }
    }
}
