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
using UglyToad.PdfPig.Content;

namespace UglyToad.PdfPig.Rendering.Skia;

internal partial class SkiaStreamProcessor
{
    /// <summary>Non-null when recording a <see cref="SkiaLayeredPage"/>.</summary>
    private readonly LayeredComposition? _layered;

    private readonly List<IDisposable> _layeredOwned = new();

    // Paint recorded ahead of time (Type 3 glyph pictures, soft-mask images, mesh pictures) is not split by condition.
    private int _aheadOfTimeDepth;

    private bool _inConditionalPaint;

    /// <summary>
    /// Starts a paint operation. When recording a layered page under a non-trivial condition, the operation's drawing
    /// goes to its own picture, emitted on dispose as a <see cref="ConditionalPictureDrawable"/> at the current
    /// position of the main recording; state calls made outside paint operations stay on the main recording.
    /// </summary>
    private ConditionalPaintScope BeginPaint()
    {
        if (_layered is null || _aheadOfTimeDepth > 0 || _inConditionalPaint || CurrentOptionalContent.IsAlways)
        {
            return default;
        }

        var main = _canvas;
        var deviceMatrix = main.TotalMatrix;
        var recorder = new SKPictureRecorder();
        _canvas = recorder.BeginRecording(SKRect.Create(_width, _height), true);
        _canvas.SetMatrix(in deviceMatrix);
        _inConditionalPaint = true;
        return new ConditionalPaintScope(this, recorder, main, CurrentOptionalContent);
    }

    private void EndPaint(SKPictureRecorder recorder, SKCanvas main, OptionalContentCondition condition)
    {
        SKPicture picture;
        try
        {
            picture = recorder.EndRecording();
        }
        finally
        {
            recorder.Dispose();
            _canvas = main;
            _inConditionalPaint = false;
        }

        var drawable = new ConditionalPictureDrawable(picture, condition, _layered!);
        _layeredOwned.Add(drawable);

        // The sub-picture holds absolute (device) matrices: draw it at identity, under the main recording's clip.
        _canvas.Save();
        _canvas.ResetMatrix();
        _canvas.DrawDrawable(drawable, SKMatrix.Identity);
        _canvas.Restore();
    }

    private readonly struct ConditionalPaintScope : IDisposable
    {
        private readonly SkiaStreamProcessor? _processor;
        private readonly SKPictureRecorder? _recorder;
        private readonly SKCanvas? _main;
        private readonly OptionalContentCondition? _condition;

        public ConditionalPaintScope(SkiaStreamProcessor processor, SKPictureRecorder recorder, SKCanvas main,
            OptionalContentCondition condition)
        {
            _processor = processor;
            _recorder = recorder;
            _main = main;
            _condition = condition;
        }

        public void Dispose()
        {
            _processor?.EndPaint(_recorder!, _main!, _condition!);
        }
    }
}
