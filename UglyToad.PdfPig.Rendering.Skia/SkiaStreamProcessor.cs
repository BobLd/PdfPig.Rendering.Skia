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
using System.Linq;
using SkiaSharp;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Geometry;
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Graphics.Operations;
using UglyToad.PdfPig.Parser;
using UglyToad.PdfPig.Rendering.Skia.Helpers;
using UglyToad.PdfPig.Tokenization.Scanner;
using UglyToad.PdfPig.Tokens;

namespace UglyToad.PdfPig.Rendering.Skia
{
    internal partial class SkiaStreamProcessor : BaseStreamProcessor<SKPicture>
    {
        private const float _minimumLineWidth = 0.25f;
        private const bool _antiAliasing = true;

        private readonly bool _renderAnnotations;
        private readonly float _height;
        private readonly float _width;
        private readonly SKMatrix _yAxisInvertMatrix;

        private readonly SkiaFontCache _fontCache;
        private readonly SKPaintCache _paintCache = new SKPaintCache(_antiAliasing, _minimumLineWidth);

        // Stack to keep track of original transforms for nested form XObjects
        private readonly Stack<SKMatrix> _currentStreamOriginalTransforms = new();

        // Stack to track layer paints for transparency groups (null means regular Save was used)
        private readonly Stack<SKPaint?> _transparencyLayerPaints = new();

        // Pending layer paint to be used in the next PushState call
        private SKPaint? _pendingTransparencyLayerPaint;

        // Stack to track soft-mask images applied per transparency-group SaveLayer.
        // Each entry corresponds to one PushState/PopState pair: non-null means a soft mask
        // has been pre-rendered and must be DstIn-blended into the layer just before the
        // matching PopState restores it. Null entries (most pushes) carry no mask.
        private readonly Stack<SKImage?> _pendingMasks = new();

        // Soft mask captured by the current ProcessFormXObject override; consumed and rendered
        // inside the next PushState (ahead of SaveLayer) so the resulting SKImage is queued at
        // the same stack level as the layer it will mask.
        private SoftMask? _pendingSoftMask;

        // Set true while RenderSoftMaskToImage is recursively driving the form processor through
        // the soft-mask transparency group. Suppresses re-entry into the SMask path so we don't
        // try to mask the mask we are rendering.
        private bool _isRenderingSoftMask;

        /// <summary>
        /// The current transformation matrix at the time the soft mask was activated by the
        /// <c>gs</c> operator. The soft mask's transparency group must be rendered using this
        /// matrix, not the CTM in effect at the time the mask is consumed.
        /// </summary>
        private SKMatrix _softMaskMatrix = SKMatrix.Identity;
        
        private SKCanvas _canvas;

        public SkiaStreamProcessor(
            int pageNumber,
            IResourceStore resourceStore,
            IPdfTokenScanner pdfScanner,
            IPageContentParser pageContentParser,
            ILookupFilterProvider filterProvider,
            CropBox cropBox,
            UserSpaceUnit userSpaceUnit,
            PageRotationDegrees rotation,
            TransformationMatrix initialMatrix,
            ParsingOptions parsingOptions,
            AnnotationProvider? annotationProvider,
            SkiaFontCache fontCache)
            : base(pageNumber,
                resourceStore,
                pdfScanner,
                pageContentParser,
                filterProvider,
                cropBox,
                userSpaceUnit,
                rotation,
                initialMatrix,
                parsingOptions)
        {
            _renderAnnotations = annotationProvider is not null;
            _annotations = new Lazy<Annotation[]>(() => annotationProvider?.GetAnnotations().ToArray() ?? []);

            _fontCache = fontCache;

            _width = (float)cropBox.Bounds.Width;
            _height = (float)cropBox.Bounds.Height;
            _yAxisInvertMatrix = SKMatrix.CreateScale(1, -1, 0, _height / 2f);

            _currentStreamOriginalTransforms.Push(initialMatrix.ToSkMatrix());

            // Wrap the default IColorSpaceContext so we can capture operand colour components
            // supplied alongside pattern names (PdfPig drops them otherwise). Required to render
            // uncoloured tiling patterns.
            var initialState = GetCurrentState();
            initialState.ColorSpaceContext = new PatternAwareColorSpaceContext(initialState.ColorSpaceContext);
        }

        public override SKPicture Process(int pageNumberCurrent, IReadOnlyList<IGraphicsStateOperation> operations)
        {
            try
            {
                // https://github.com/apache/pdfbox/blob/94b3d15fd24b9840abccece261173593625ff85c/pdfbox/src/main/java/org/apache/pdfbox/rendering/PDFRenderer.java#L274

                CloneAllStates();

                using var recorder = new SKPictureRecorder();
                _canvas = recorder.BeginRecording(SKRect.Create(_width, _height), true);

                // Inverse direction of y-axis
                _canvas.SetMatrix(in _yAxisInvertMatrix);
                _canvas.Concat(CurrentTransformationMatrix.ToSkMatrix());

                PushState();

                ProcessOperations(operations);

                PopState();

                if (_renderAnnotations)
                {
                    DrawAnnotations();
                }

                _canvas.Flush();

                return recorder.EndRecording();
            }
            finally
            {
                Cleanup();
            }
        }

        public override void PopState()
        {
            // Apply the pending soft mask (if any) BEFORE restoring the layer so the mask is
            // composited into the still-open offscreen buffer. DstIn keeps only the parts of
            // the layer where the mask has alpha; the LumaColor filter converts the source
            // colour to luminosity-as-alpha for /Luminosity-subtype masks.
            var maskImage = _pendingMasks.Pop();
            if (maskImage is not null)
            {
                _canvas.Save();
                // Cancel out the recorder's current CTM so DrawImage targets device pixels of
                // the layer 1:1 against the mask SKImage (which was rendered into pixel space
                // covering the same logical bounds as the layer).
                _canvas.SetMatrix(SKMatrix.Identity);

                using var maskPaint = new SKPaint
                {
                    BlendMode = SKBlendMode.DstIn,
                    ColorFilter = SKColorFilter.CreateLumaColor()
                };

                _canvas.DrawImage(maskImage, SKRect.Create(0, 0, _width, _height), maskPaint);
                _canvas.Restore();
                maskImage.Dispose();
            }

            base.PopState();
            _canvas.Restore();

            // Dispose layer paint if one was used for this state
            var layerPaint = _transparencyLayerPaints.Pop();
            layerPaint?.Dispose();

            EndPath();
        }

        public override void PushState()
        {
            base.PushState();

            if (_pendingTransparencyLayerPaint is not null)
            {
                // Render the queued soft mask to an offscreen image BEFORE SaveLayer so the
                // mask uses the parent state's CTM (which is what SoftMask.InitialTransformationMatrix
                // already encodes). Once SaveLayer fires, _canvas's clip and matrix shift to the
                // new layer. Consume the field before recursing — RenderSoftMaskToImage drives
                // ProcessFormXObject → PushState again to render the mask's own transparency
                // group, and that re-entrant PushState must not see the same pending mask.
                SKImage? maskImage = null;
                SoftMask? pendingMask = _pendingSoftMask;
                _pendingSoftMask = null;
                if (pendingMask is not null)
                {
                    // RenderSoftMaskToImage drives a recursive ProcessFormXObject for the mask
                    // form's own transparency group, whose own PushState consumes
                    // _pendingTransparencyLayerPaint (SaveLayer + null-out). Snapshot and restore
                    // it so the outer SaveLayer below still gets the parent's blend-mode/alpha
                    // paint instead of null.
                    SKPaint? savedPendingPaint = _pendingTransparencyLayerPaint;

                    maskImage = RenderSoftMaskToImage(pendingMask);

                    _pendingTransparencyLayerPaint = savedPendingPaint;
                }

                // Use SaveLayer for transparency group - creates offscreen buffer
                _canvas.SaveLayer(_pendingTransparencyLayerPaint);
                _transparencyLayerPaints.Push(_pendingTransparencyLayerPaint);
                _pendingMasks.Push(maskImage);
                _pendingTransparencyLayerPaint = null;
            }
            else
            {
                _canvas.Save();
                _transparencyLayerPaints.Push(null);
                _pendingMasks.Push(null);
            }

            EndPath();
        }

        public override void ModifyClippingIntersect(FillingRule clippingRule)
        {
            if (_currentPath is null)
            {
                return;
            }

            _currentPath.FillType = clippingRule.ToSKPathFillType();
            _canvas.ClipPath(_currentPath, SKClipOperation.Intersect);
        }

        protected override void ClipToRectangle(PdfRectangle rectangle, FillingRule clippingRule)
        {
            //_canvas.DrawRect(rectangle.ToSKRect(_height), _paintCache.GetImageDebug());
            _canvas.ClipRect(rectangle.ToSKRect(), SKClipOperation.Intersect);
        }

        /// <inheritdoc/>
        public override void BeginMarkedContent(NameToken name, NameToken? propertyDictionaryName, DictionaryToken? properties)
        {
            // No op
        }

        /// <inheritdoc/>
        public override void EndMarkedContent()
        {
            // No op
        }

        public override void ModifyCurrentTransformationMatrix(TransformationMatrix value)
        {
            base.ModifyCurrentTransformationMatrix(value);
            _canvas.Concat(value.ToSkMatrix());
        }

        // Note that recursive calls are possible here for nested form XObjects
        protected override void ProcessFormXObject(StreamToken formStream, NameToken xObjectName)
        {
            // Check if this is a Transparency Group XObject
            bool isTransparencyGroup = formStream.StreamDictionary.TryGet(NameToken.Group, PdfScanner, out DictionaryToken? formGroupToken)
                && formGroupToken.TryGet<NameToken>(NameToken.S, PdfScanner, out var sToken)
                && sToken == NameToken.Transparency;

            if (isTransparencyGroup)
            {
                // Capture parent state's blend mode and alpha before PushState
                var parentState = GetCurrentState();

                // Set pending layer paint - will be used in PushState called by base.ProcessFormXObject
                // The transparency group will be composited with parent's blend mode and alpha when the layer is restored
                _pendingTransparencyLayerPaint = new SKPaint
                {
                    IsAntialias = _antiAliasing,
                    BlendMode = parentState.BlendMode.ToSKBlendMode(),
                    Color = SKColors.White.WithAlpha((parentState.AlphaConstantNonStroking * 255).ToByte())
                };

                // PDF 1.7 §11.6.5.2: a soft mask in the parent graphics state masks the
                // transparency group's compositing. Capture it now so the upcoming PushState
                // can render the mask into an offscreen image alongside SaveLayer. Suppress
                // while we're already rendering one mask, otherwise the mask form's own
                // transparency group would re-enter and mask itself.
                if (!_isRenderingSoftMask && parentState.SoftMask is not null)
                {
                    _pendingSoftMask = parentState.SoftMask;
                }
            }

            // Capture the CTM that base.ProcessFormXObject will leave in effect after applying
            // the form's Matrix entry (CTM' = formMatrix × CTM, see PDF 1.7 §8.7.2). Pattern
            // rendering inside the form body recovers this via _currentStreamOriginalTransforms.Peek().
            // Push/pop unconditionally and adjacently here — recursive ProcessFormXObject calls
            // (e.g. RenderSoftMaskToImage's mask form) likewise push/pop their own entry, so
            // the stack stays balanced regardless of execution path.
            var formInitialCtm = PdfPigExtensions.ReadFormMatrix(formStream, PdfScanner)
                .Multiply(CurrentTransformationMatrix);
            _currentStreamOriginalTransforms.Push(formInitialCtm.ToSkMatrix());
            try
            {
                base.ProcessFormXObject(formStream, xObjectName);
            }
            finally
            {
                _currentStreamOriginalTransforms.Pop();
            }
        }
        
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
            SKColor backdrop = ResolveSoftMaskBackdrop(softMask);
            maskCanvas.Clear(backdrop);

            // Match the main canvas: 2× supersample, then PDF Y-flip, then the CTM captured at
            // the moment /gs activated this soft mask. The mask form's content stream then
            // runs as if it were drawing on the main canvas at the time of activation.
            maskCanvas.Scale(superSample, superSample);
            maskCanvas.Concat(in _yAxisInvertMatrix);
            maskCanvas.Concat(in _softMaskMatrix);
            _softMaskMatrix = SKMatrix.Identity; // Reset matrix

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
        /// Resolves the back-drop colour used to seed the soft mask offscreen surface for
        /// /Luminosity masks. The PDF spec specifies the BC array in the group's colour space;
        /// we approximate by treating 1-component as gray and 3+ components as RGB. /Alpha
        /// masks ignore colour so we keep transparent (alpha 0) as the seed.
        /// </summary>
        private static SKColor ResolveSoftMaskBackdrop(SoftMask softMask)
        {
            if (softMask.Subtype != SoftMaskType.Luminosity)
            {
                return SKColors.Transparent;
            }

            double[]? bc = softMask.BC;
            if (bc is null || bc.Length == 0)
            {
                // Spec default: the colour space's initial value, representing black.
                return SKColors.Black;
            }

            if (bc.Length == 1)
            {
                byte g = (bc[0] * 255).ToByte();
                return new SKColor(g, g, g, 255);
            }

            return new SKColor((bc[0] * 255).ToByte(),
                (bc[1] * 255).ToByte(),
                (bc[2] * 255).ToByte(), 255);
        }

        public override void SetNamedGraphicsState(NameToken stateName)
        {
            base.SetNamedGraphicsState(stateName);

            var currentGraphicsState = GetCurrentState();
            if (currentGraphicsState.SoftMask is not null)
            {
                var state = ResourceStore.GetExtendedGraphicsStateDictionary(stateName);
                if (state?.ContainsKey(NameToken.Smask) == true)
                {
                    // The soft mask's transparency group must be rendered with the CTM that is in
                    // effect at the time the gs operator activates the mask, not whatever CTM is
                    // active when the mask is later consumed during a paint operation.
                    _softMaskMatrix = currentGraphicsState.CurrentTransformationMatrix.ToSkMatrix();

                    // NB: We could store the matrix in the SoftMask object, and set the value direct in SetNamedGraphicsState (needs a change in PdfPig)
                    //public TransformationMatrix InitialTransformationMatrix { get; private set; } = TransformationMatrix.Identity;
                    //internal static SoftMask Parse(DictionaryToken dictionaryToken, IPdfTokenScanner pdfTokenScanner, ILookupFilterProvider filterProvider,
                    //    TransformationMatrix initialTransformationMatrix)
                }
            }
        }

        private void Cleanup()
        {
            _paintCache.Dispose();
            _pendingTransparencyLayerPaint?.Dispose();
            _pendingTransparencyLayerPaint = null;
            _pendingSoftMask = null;
            foreach (var paint in _transparencyLayerPaints)
            {
                paint?.Dispose();
            }
            _transparencyLayerPaints.Clear();
            while (_pendingMasks.Count > 0)
            {
                _pendingMasks.Pop()?.Dispose();
            }
        }
    }
}
