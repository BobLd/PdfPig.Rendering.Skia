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
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Graphics.Core;
using UglyToad.PdfPig.Graphics.Operations;
using UglyToad.PdfPig.PdfFonts;
using UglyToad.PdfPig.Rendering.Skia.Helpers;

namespace UglyToad.PdfPig.Rendering.Skia
{
    internal partial class SkiaStreamProcessor
    {
        public override void RenderGlyph(IFont font,
            CurrentGraphicsState currentState,
            double fontSize,
            double pointSize,
            int code,
            string unicode,
            long currentOffset,
            in TransformationMatrix renderingMatrix,
            in TransformationMatrix textMatrix,
            in TransformationMatrix transformationMatrix,
            CharacterBoundingBox characterBoundingBox)
        {
            var textRenderingMode = currentState.FontState.TextRenderingMode;

            if (textRenderingMode == TextRenderingMode.Neither)
            {
                return;
            }

            if (font is IType3Font type3Font)
            {
                ShowType3Glyph(type3Font, code, in renderingMatrix, in textMatrix);
                return;
            }

            var strokingColor = currentState.CurrentStrokingColor;
            var nonStrokingColor = currentState.CurrentNonStrokingColor;

            if (_fontCache.TryGetPath(font, code, out SKPath? path))
            {
                if (path!.IsEmpty)
                {
                    // If null or whitespace, ignore
                    if (string.IsNullOrWhiteSpace(unicode))
                    {
                        return;
                    }

                    ParsingOptions.Logger.Debug($"RenderGlyph: VectorFontGlyph path is empty for '{unicode}' ({font.Details}), falling back to NonVectorFontGlyph.");
                    ShowNonVectorFontGlyph(font, strokingColor, nonStrokingColor, textRenderingMode, fontSize, unicode,
                        in renderingMatrix, in textMatrix, characterBoundingBox);
                }
                else
                {
                    ShowVectorFontGlyph(path, strokingColor, nonStrokingColor, textRenderingMode, in renderingMatrix,
                        in textMatrix);
                }
            }
            else
            {
                ShowNonVectorFontGlyph(font, strokingColor, nonStrokingColor, textRenderingMode, fontSize, unicode,
                    in renderingMatrix, in textMatrix, characterBoundingBox);
            }
        }

        private void ShowVectorFontGlyph(SKPath path, IColor? strokingColor, IColor? nonStrokingColor,
            TextRenderingMode textRenderingMode, in TransformationMatrix renderingMatrix,
            in TransformationMatrix textMatrix)
        {
            var transformMatrix = renderingMatrix.ToSkMatrix()
                .PostConcat(textMatrix.ToSkMatrix());

            var currentState = GetCurrentState();

            using (var transformedPath = new SKPath())
            {
                path.Transform(transformMatrix, transformedPath);

                if (textRenderingMode.IsClip())
                {
                    AppendGlyphToTextClipPath(transformedPath);
                }

                if (_canvas.QuickReject(transformedPath))
                {
                    return;
                }

                if (textRenderingMode.IsFill())
                {
                    // Do fill first
                    if (nonStrokingColor is not null && nonStrokingColor.ColorSpace == ColorSpace.Pattern)
                    {
                        // TODO - Clean shading patterns painting
                        // See documents:
                        // - GHOSTSCRIPT-693120-0
                        // - GHOSTSCRIPT-698721-0.zip-6
                        // - GHOSTSCRIPT-698721-1_1

                        if (nonStrokingColor is not PatternColor pattern)
                        {
                            throw new ArgumentNullException($"Expecting a {nameof(PatternColor)} but got {nonStrokingColor.GetType()}.");
                        }

                        switch (pattern.PatternType)
                        {
                            case PatternType.Tiling:
                                RenderTilingPattern(transformedPath, pattern as TilingPatternColor, false);
                                break;

                            case PatternType.Shading:
                                RenderShadingPattern(transformedPath, pattern as ShadingPatternColor, false);
                                break;
                        }
                    }
                    else if (TryGetActiveSoftMask(out var softMask))
                    {
                        var innerPaint = _paintCache.GetPaint(nonStrokingColor, currentState.AlphaConstantNonStroking, false,
                            null, null, null, null, BlendMode.Normal);
                        var glyphPath = transformedPath;
                        DrawWithSoftMask(softMask!, currentState.BlendMode, () => _canvas.DrawPath(glyphPath, innerPaint));
                    }
                    else
                    {
                        var fillPaint = _paintCache.GetPaint(nonStrokingColor, currentState.AlphaConstantNonStroking, false,
                            null, null, null, null, currentState.BlendMode);
                        _canvas.DrawPath(transformedPath, fillPaint);
                    }
                }

                if (textRenderingMode.IsStroke())
                {
                    // Then stroke
                    if (strokingColor is not null && strokingColor.ColorSpace == ColorSpace.Pattern)
                    {
                        // Not supported for now
                        strokingColor = RGBColor.Black;
                    }

                    if (TryGetActiveSoftMask(out var softMask))
                    {
                        var innerStrokePaint = _paintCache.GetPaint(strokingColor, currentState.AlphaConstantStroking, true,
                            (float)currentState.LineWidth, currentState.JoinStyle, currentState.CapStyle,
                            currentState.LineDashPattern, BlendMode.Normal);
                        var glyphStrokePath = transformedPath;
                        DrawWithSoftMask(softMask!, currentState.BlendMode, () => _canvas.DrawPath(glyphStrokePath, innerStrokePaint));
                    }
                    else
                    {
                        var strokePaint = _paintCache.GetPaint(strokingColor, currentState.AlphaConstantStroking, true,
                            (float)currentState.LineWidth, currentState.JoinStyle, currentState.CapStyle,
                            currentState.LineDashPattern, currentState.BlendMode);
                        _canvas.DrawPath(transformedPath, strokePaint);
                    }
                }
            }
        }

        private void ShowNonVectorFontGlyph(IFont font, IColor? strokingColor, IColor? nonStrokingColor,
            TextRenderingMode textRenderingMode,
            double fontSize, string unicode, in TransformationMatrix renderingMatrix,
            in TransformationMatrix textMatrix,
            CharacterBoundingBox characterBoundingBox)
        {
            if (!CanRender(unicode))
            {
                return;
            }

            var drawTypeface = _fontCache.GetTypefaceOrFallback(font, unicode);
            if (!drawTypeface.Typeface.ContainsGlyphs(unicode))
            {
                return;
            }

            using var s = new SKAutoCanvasRestore(_canvas, true);
            _canvas.Concat(textMatrix.ToSkMatrix());
            _canvas.Concat(renderingMatrix.ToSkMatrix());
            _canvas.Scale(1, -1, 0, 0);

            // PDF 1.7 §9.3.6: in rendering modes 4–7 the glyph outline contributes to the text
            // clipping path applied on ET. Capture it from the shaped Skia glyphs at the active
            // canvas matrix so cm/q/Q changes later in the same text object don't shift it.
            if (textRenderingMode.IsClip())
            {
                using var glyphPath = GetPath(drawTypeface, unicode);
                if (!glyphPath.IsEmpty)
                {
                    AppendGlyphToTextClipPath(glyphPath);
                }
            }

            if (!textRenderingMode.IsFill() && !textRenderingMode.IsStroke())
            {
                // Mode 7 (NeitherClip) reaches here — clip-mode glyphs were recorded above.
                return;
            }

            var glyphBounds = new PdfRectangle(0, 0, characterBoundingBox.Width, UserSpaceUnit.PointMultiples);

            if (_canvas.QuickReject(glyphBounds.ToSKRect()))
            {
                return;
            }

            var currentState = GetCurrentState();

            if (textRenderingMode.IsFill())
            {
                // Do fill first
                if (nonStrokingColor is not null && nonStrokingColor.ColorSpace == ColorSpace.Pattern)
                {
                    // See documents:
                    // - 0000190.pdf

                    if (nonStrokingColor is not PatternColor)
                    {
                        throw new ArgumentNullException($"Expecting a {nameof(PatternColor)} but got '{nonStrokingColor.GetType()}'.");
                    }

                    using (var path = GetPath(drawTypeface, unicode))
                    {
                        ShowVectorFontGlyph(path, strokingColor, nonStrokingColor,
                            textRenderingMode, in TransformationMatrix.Identity, in TransformationMatrix.Identity);
                    }
                }
                else if (TryGetActiveSoftMask(out var softMask))
                {
                    var innerPaint = _paintCache.GetPaint(nonStrokingColor, currentState.AlphaConstantNonStroking, false,
                        null, null, null, null, BlendMode.Normal);
                    DrawWithSoftMask(softMask!, currentState.BlendMode, () =>
                    {
                        using var skFont = drawTypeface.Typeface.ToFont(1f);
                        _canvas.DrawShapedText(drawTypeface.Shaper, unicode, SKPoint.Empty, SKTextAlign.Left, skFont, innerPaint);
                    });
                }
                else
                {
                    var fillPaint = _paintCache.GetPaint(nonStrokingColor, currentState.AlphaConstantNonStroking, false,
                        null, null, null, null, currentState.BlendMode);

                    using (var skFont = drawTypeface.Typeface.ToFont(1f))
                    {
                        _canvas.DrawShapedText(drawTypeface.Shaper, unicode, SKPoint.Empty, SKTextAlign.Left, skFont, fillPaint);
                    }
                }
            }

            if (textRenderingMode.IsStroke())
            {
                // Then stroke
                if (strokingColor is not null && strokingColor.ColorSpace == ColorSpace.Pattern)
                {
                    // Not supported for now
                    strokingColor = RGBColor.Black;
                }

                if (TryGetActiveSoftMask(out var softMask))
                {
                    var innerStrokePaint = _paintCache.GetPaint(strokingColor, currentState.AlphaConstantStroking, true,
                        (float)currentState.LineWidth, currentState.JoinStyle, currentState.CapStyle,
                        currentState.LineDashPattern, BlendMode.Normal);
                    DrawWithSoftMask(softMask!, currentState.BlendMode, () =>
                    {
                        using var skFont = drawTypeface.Typeface.ToFont(1f);
                        _canvas.DrawShapedText(drawTypeface.Shaper, unicode, SKPoint.Empty, SKTextAlign.Left, skFont, innerStrokePaint);
                    });
                }
                else
                {
                    var strokePaint = _paintCache.GetPaint(strokingColor, currentState.AlphaConstantStroking, true,
                        (float)currentState.LineWidth, currentState.JoinStyle, currentState.CapStyle,
                        currentState.LineDashPattern, currentState.BlendMode);

                    using (var skFont = drawTypeface.Typeface.ToFont(1f))
                    {
                        _canvas.DrawShapedText(drawTypeface.Shaper, unicode, SKPoint.Empty, SKTextAlign.Left, skFont, strokePaint);
                    }
                }
            }
        }

        /// <summary>
        /// Append the given glyph outline (still in the local "pre-canvas-CTM" space — i.e. PDF
        /// user-space relative to the matrix in effect when the glyph was emitted) to the text
        /// clipping path. The accumulator stores paths in device pixel space so that subsequent
        /// <c>cm</c>/<c>q</c>/<c>Q</c> within the same text object don't move the glyph relative
        /// to where it was actually drawn.
        /// </summary>
        private void AppendGlyphToTextClipPath(SKPath glyphPathInUserSpace)
        {
            _textClipPath ??= new SKPath();

            var currentCanvasMatrix = _canvas.TotalMatrix;
            _textClipPath.AddPath(glyphPathInUserSpace, in currentCanvasMatrix, SKPathAddMode.Append);
        }

        private void ShowType3Glyph(IType3Font font, int code,
            in TransformationMatrix renderingMatrix,
            in TransformationMatrix textMatrix)
        {
            var ctx = new Type3BuildContext(
                FilterProvider,
                PdfScanner,
                PageContentParser,
                PageNumber,
                ParsingOptions.Logger,
                BuildType3Picture);

            var cached = _fontCache.GetOrBuildType3Glyph(font, code, ctx);

            switch (cached)
            {
                case Type3VectorGlyph vector:
                {
                    // The cached path is in raw glyph space (pre-fontMatrix). Match the un-cached
                    // path's matrix arithmetic exactly by applying textMatrix × renderingMatrix ×
                    // fontMatrix via CTM concatenation (PDF spec right-to-left order), and then
                    // pass Identity matrices to ShowVectorFontGlyph so the path is drawn at the
                    // current CTM without an extra path-side SKMatrix transform.
                    PushState();
                    try
                    {
                        ModifyCurrentTransformationMatrix(textMatrix);
                        ModifyCurrentTransformationMatrix(renderingMatrix);
                        ModifyCurrentTransformationMatrix(font.GetFontMatrix());
                        var currentState = GetCurrentState();
                        ShowVectorFontGlyph(vector.Path,
                            currentState.CurrentStrokingColor,
                            currentState.CurrentNonStrokingColor,
                            currentState.FontState.TextRenderingMode,
                            in TransformationMatrix.Identity, in TransformationMatrix.Identity);
                    }
                    finally
                    {
                        PopState();
                    }
                    return;
                }

                case Type3PictureGlyph picture:
                {
                    // Same matrix composition the un-cached path used: replace the CTM with
                    //   fontMatrix × renderingMatrix × textMatrix × oldCTM
                    // via right-to-left premultiplied ModifyCurrentTransformationMatrix calls.
                    PushState();
                    try
                    {
                        ModifyCurrentTransformationMatrix(textMatrix);
                        ModifyCurrentTransformationMatrix(renderingMatrix);
                        ModifyCurrentTransformationMatrix(font.GetFontMatrix());
                        _canvas.DrawPicture(picture.Picture);
                    }
                    finally
                    {
                        PopState();
                    }
                    return;
                }

                case Type3MissingGlyph:
                    return;
            }
        }

        /// <summary>
        /// Record a Type 3 CharProc's operations into a self-contained <see cref="SKPicture"/>.
        /// The recording runs the live operator pipeline (so <c>Do</c>, <c>gs</c>, inline images
        /// etc. all work) against a temporary recorder canvas. The page canvas is swapped out for
        /// the duration of the recording and restored unconditionally in the <c>finally</c>.
        /// </summary>
        /// <remarks>
        /// The recording is in raw glyph space (no fontMatrix applied). Callers apply
        /// <c>textMatrix × renderingMatrix × fontMatrix</c> at draw time, matching the matrix
        /// composition the un-cached path used.
        /// </remarks>
        private SKPicture BuildType3Picture(IReadOnlyList<IGraphicsStateOperation> operations, IType3Font font, int code)
        {
            // SKPictureRecorder culling bounds — a hint only, not a hard clip. Try the per-code
            // glyph bbox first (raw glyph space, pre-fontMatrix). Type3 fonts may not have widths
            // for every encountered code (Differences encodings), in which case GetBoundingBox
            // throws — fall back to a generous default since the bound is only an optimization.
            SKRect bounds = SKRect.Create(-1000, -1000, 2000, 2000);
            try
            {
                var fontBbox = font.GetBoundingBox(code).GlyphBounds;
                var candidate = new SKRect(
                    (float)Math.Min(fontBbox.BottomLeft.X, fontBbox.TopRight.X),
                    (float)Math.Min(fontBbox.BottomLeft.Y, fontBbox.TopRight.Y),
                    (float)Math.Max(fontBbox.BottomLeft.X, fontBbox.TopRight.X),
                    (float)Math.Max(fontBbox.BottomLeft.Y, fontBbox.TopRight.Y));
                if (!candidate.IsEmpty)
                {
                    bounds = candidate;
                }
            }
            catch
            {
                // Keep the generous fallback bounds.
            }

            var pageCanvas = _canvas;
            using var recorder = new SKPictureRecorder();
            _canvas = recorder.BeginRecording(bounds);

            bool pushedResources = false;
            var savedTextMatrix = TextMatrices.TextMatrix;
            var savedTextLineMatrix = TextMatrices.TextLineMatrix;

            try
            {
                if (font.Type3Resources is { } type3Resources)
                {
                    ResourceStore.LoadResourceDictionary(type3Resources);
                    pushedResources = true;
                }

                PushState();
                try
                {
                    TextMatrices.TextMatrix = TransformationMatrix.Identity;
                    TextMatrices.TextLineMatrix = TransformationMatrix.Identity;
                    ProcessOperations(operations);
                }
                finally
                {
                    PopState();
                }
            }
            finally
            {
                TextMatrices.TextMatrix = savedTextMatrix;
                TextMatrices.TextLineMatrix = savedTextLineMatrix;
                if (pushedResources)
                {
                    ResourceStore.UnloadResourceDictionary();
                }
                _canvas = pageCanvas;
            }

            return recorder.EndRecording();
        }

        private static SKPath GetPath(SkiaFontCacheItem fontItem, string unicode)
        {
            using (var skFont = fontItem.Typeface.ToFont(1f))
            {
                var shaped = fontItem.Shaper.Shape(unicode, skFont);

                var combinedPath = new SKPath();
                for (int i = 0; i < shaped.Codepoints.Length; ++i)
                {
                    uint glyphId = shaped.Codepoints[i];
                    SKPoint pos = shaped.Points[i];

                    SKPath glyphPath = skFont.GetGlyphPath((ushort)glyphId); // Check for overflow?
                    if (glyphPath is not null)
                    {
                        var matrix = SKMatrix.CreateTranslation(pos.X, pos.Y);
                        combinedPath.AddPath(glyphPath, in matrix);
                    }
                }

                return combinedPath;
            }
        }

        private static float ComputeSkewX(PdfRectangle rectangle)
        {
            var rotationRadians = rectangle.Rotation * Math.PI / 180.0;
            var diffY = rectangle.TopLeft.Y - rectangle.BottomLeft.Y;
            var diffX = rectangle.TopLeft.X - rectangle.BottomLeft.X;

            var rotationBottomTopRadians = (float)Math.Atan2(diffY, diffX);

            // Test documents:
            // - SPARC - v9 Architecture Manual.pdf, page 1
            // - 68-1990-01_A.pdf, page 15
            // - GHOSTSCRIPT-686821-0.pdf

            double radians = (Math.PI / 2.0 + rotationRadians - rotationBottomTopRadians) % Math.PI;

            return (float)Math.Round(radians, 5);
        }

        private static bool CanRender(string unicode)
        {
            ReadOnlySpan<char> chars = unicode.AsSpan();
            for (int i = 0; i < chars.Length; ++i)
            {
                if (char.IsControl(chars[i]))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
