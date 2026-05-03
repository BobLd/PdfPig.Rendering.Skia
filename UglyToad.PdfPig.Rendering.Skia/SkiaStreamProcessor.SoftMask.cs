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
using System.Text;
using SkiaSharp;
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Graphics.Core;
using UglyToad.PdfPig.Rendering.Skia.Helpers;

namespace UglyToad.PdfPig.Rendering.Skia
{
    internal partial class SkiaStreamProcessor
    {
        /// <summary>
        /// Renders a soft mask's transparency group into an offscreen <see cref="SKImage"/>
        /// covering the full page bounds, ready to be applied via DstIn at PopState time.
        /// <para>
        /// The mask is rasterised at 2× page resolution to keep luminosity edges sharp once
        /// the host SKPicture is later played back to a higher-DPI surface. The rendering CTM
        /// matches the main canvas (Y-flip + soft mask's captured initial CTM) so that the
        /// mask's shape lands at the same device pixels as the layer it will mask.
        /// </para>
        /// <para>
        /// For /Luminosity masks the canvas is pre-cleared with the mask's BC (back-drop)
        /// colour, typically black, so the soft mask's transparency group is composited
        /// against the spec-mandated opaque backdrop. The DstIn paint then uses
        /// <see cref="SKColorFilter.CreateLumaColor"/> to convert the resulting RGB to alpha.
        /// </para>
        /// </summary>
        private SKImage? RenderSoftMaskToImage(SoftMask softMask)
        {
            const int superSample = 2;
            int pixelWidth = Math.Max(1, (int)Math.Ceiling(_width * superSample));
            int pixelHeight = Math.Max(1, (int)Math.Ceiling(_height * superSample));

            var info = new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface is null)
            {
                return null;
            }

            var maskCanvas = surface.Canvas;

            // Backdrop — for /Luminosity masks the source group is composited over an opaque
            // backdrop (BC entry, defaulting to black in the group's colour space). For /Alpha
            // masks the colour is irrelevant since only the alpha channel is consumed.
            SKColor backdrop = softMask.GetSoftMaskBackdrop();
            maskCanvas.Clear(backdrop);

            // Match the main canvas: 2× supersample, then PDF Y-flip, then the CTM captured at
            // the moment /gs activated this soft mask. The mask form's content stream then
            // runs as if it were drawing on the main canvas at the time of activation.
            maskCanvas.Scale(superSample, superSample);
            maskCanvas.Concat(in _yAxisInvertMatrix);
            maskCanvas.Concat(in _softMaskMatrix);

            var savedCanvas = _canvas;
            _canvas = maskCanvas;
            bool savedFlag = _isRenderingSoftMask;
            _isRenderingSoftMask = true;

            try
            {
                // Drive the form processor through the mask's transparency group exactly as
                // any other form XObject. The flag above prevents re-entry into the SMask path
                // for the (always transparency-group) mask form itself.
                ProcessFormXObject(softMask.TransparencyGroup, null!);
            }
            finally
            {
                _canvas = savedCanvas;
                _isRenderingSoftMask = savedFlag;
            }

            return surface.Snapshot();
        }

        /// <summary>
        /// Returns true (and the captured <see cref="SoftMask"/>) when the current graphics state
        /// has an active soft mask that must be applied to a per-paint operation.
        /// <para>
        /// Per PDF 1.7 §11.6.5.2, a soft mask in the graphics state shall be applied to every
        /// painting operation while it is in scope - not just to whole transparency groups.
        /// Form-level soft mask handling (see <see cref="ProcessFormXObject"/>) covers the case
        /// where a soft mask wraps a /Form Do invocation; this helper covers the per-paint case
        /// where a <c>gs</c> activates an SMask inside a form (or at top level) and subsequent
        /// fills/strokes/glyphs/images must each be composited through the mask.
        /// </para>
        /// </summary>
        private bool TryGetActiveSoftMask(out SoftMask? softMask)
        {
            var currentState = GetCurrentState();
            if (_isRenderingSoftMask)
            {
                softMask = null;
                return false;
            }

            softMask = currentState.SoftMask;
            return softMask is not null;
        }

        /// <summary>
        /// Wraps a single paint operation so that the active soft mask multiplies its source
        /// alpha and the gs blend mode applies between the masked source and the backdrop.
        /// <para>
        /// Mechanism: open an offscreen <c>SaveLayer</c> tagged with the gs <paramref name="blendMode"/>
        /// (and full alpha tint), let the caller draw into that layer with a Normal-blend paint,
        /// composite the rendered mask into the layer with <see cref="SKBlendMode.DstIn"/> (using
        /// <see cref="SKColorFilter.CreateLumaColor"/> for /Luminosity masks), then restore - the
        /// restore is what performs the blend onto the backdrop.
        /// </para>
        /// <para>
        /// The constant alpha (ca/CA) must already be baked into the inner paint's color so we
        /// avoid double-multiplying it via the layer paint. Callers therefore use the regular
        /// paint-cache call but pass <see cref="BlendMode.Normal"/> so the gs blend mode is
        /// applied here at composite-back time rather than during the inner draw.
        /// </para>
        /// </summary>
        private void DrawWithSoftMask(SoftMask softMask, BlendMode blendMode, Action draw)
        {
            // Render the mask offscreen. RenderSoftMaskToImage drives a recursive
            // ProcessFormXObject through the mask's transparency group, which goes through
            // PushState/PopState. Our PushState calls EndPath() (PDF spec: q ends the
            // current path) — that will dispose any path the caller built up to this point
            // (e.g. the rect for the very fill we are about to draw). Snapshot the field
            // and restore it so the caller's draw still sees a live path. Cloning would be
            // safer but allocates per paint; the snapshot/restore is enough because EndPath
            // only nulls the field, it does not also dispose the SKPath inside the closure.
            SKPath? savedPath = _currentPath;
            _currentPath = null;
            SKImage? maskImage;
            try
            {
                maskImage = RenderSoftMaskToImage(softMask);
            }
            finally
            {
                _currentPath = savedPath;
            }

            if (maskImage is null)
            {
                draw();
                return;
            }

            try
            {
                using var compositePaint = new SKPaint();
                compositePaint.BlendMode = blendMode.ToSKBlendMode();
                compositePaint.Color = SKColors.White;

                _canvas.SaveLayer(compositePaint);
                try
                {
                    draw();

                    int saveCount = _canvas.Save();
                    try
                    {
                        // Mask image was rendered into device-pixel space covering the full
                        // page; clear the CTM so the subsequent DrawImage lands 1:1 against
                        // those pixels regardless of any transforms applied during the draw.
                        _canvas.SetMatrix(SKMatrix.Identity);

                        using var maskPaint = new SKPaint();
                        maskPaint.BlendMode = SKBlendMode.DstIn;
                        maskPaint.ColorFilter = softMask.Subtype == SoftMaskType.Luminosity
                            ? SKColorFilter.CreateLumaColor()
                            : null;
                        _canvas.DrawImage(maskImage, SKRect.Create(0, 0, _width, _height), maskPaint);
                    }
                    finally
                    {
                        _canvas.RestoreToCount(saveCount);
                    }
                }
                finally
                {
                    _canvas.Restore();
                }
            }
            finally
            {
                maskImage.Dispose();
            }
        }
    }
}
