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
using System.Runtime.ExceptionServices;
using SkiaSharp;
using UglyToad.PdfPig.Content;

namespace UglyToad.PdfPig.Rendering.Skia;

/// <summary>
/// A page recorded once with its optional content (layers) kept apart, composed into an <see cref="SKPicture"/>
/// for any visibility state without processing the page again.
/// <para>
/// Content produced ahead of time (Type 3 glyph pictures, soft masks, mesh shadings, tiling-pattern cells) is
/// recorded with all its optional content drawn, and does not follow the state: as a whole it is shown or hidden
/// with the paint that uses it.
/// </para>
/// </summary>
public sealed class SkiaLayeredPage : IDisposable
{
    private readonly SKDrawable _root;
    private readonly LayeredComposition _composition;
    private readonly IReadOnlyList<IDisposable> _owned;
    private bool _disposed;

    internal SkiaLayeredPage(SKDrawable root, LayeredComposition composition, IReadOnlyList<IDisposable> owned,
        SKRect bounds)
    {
        _root = root;
        _composition = composition;
        _owned = owned;
        Bounds = bounds;
        HasOptionalContent = owned.Count > 0;
    }

    /// <summary>Whether anything on the page is drawn under optional content.</summary>
    public bool HasOptionalContent { get; }

    /// <summary>The page bounds, as for the directly rendered <see cref="SKPicture"/>.</summary>
    public SKRect Bounds { get; }

    /// <summary>
    /// The page as drawn in <paramref name="state"/>; everything when <paramref name="state"/> is null.
    /// Content produced ahead of time (Type 3 glyph pictures, soft masks, mesh shadings, tiling-pattern cells) is
    /// drawn in full whatever the state, as part of the paint that uses it.
    /// Safe to call from several threads (calls are serialised).
    /// </summary>
    /// <exception cref="ArgumentException">The state was built for another document.</exception>
    public SKPicture Compose(OptionalContentState? state)
    {
        lock (_composition)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SkiaLayeredPage));
            }

            // Visibility is evaluated inside native Skia callbacks during Snapshot(), where a managed exception
            // must not escape: check up front that the state belongs to this page's document. Every condition
            // of a page comes from the same document, so one check covers them all.
            if (state is not null && _owned.Count > 0)
            {
                ((ConditionalPictureDrawable)_owned[0]).Condition.IsVisible(state);
            }

            _composition.Reset(state);
            _root.NotifyDrawingChanged();
            var picture = _root.Snapshot();

            if (_composition.TakeFailure() is { } failure)
            {
                picture.Dispose();
                failure.Throw();
            }

            return picture;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_composition)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _root.Dispose();
            foreach (var item in _owned)
            {
                item.Dispose();
            }
        }
    }
}

/// <summary>
/// The state a composition draws with, and its per-condition visibility, shared by a page's conditional drawables.
/// </summary>
internal sealed class LayeredComposition
{
    private readonly Dictionary<OptionalContentCondition, bool> _visible = new();
    private OptionalContentState? _state;
    private ExceptionDispatchInfo? _failure;

    public void Reset(OptionalContentState? state)
    {
        _state = state;
        _visible.Clear();
        _failure = null;
    }

    /// <summary>
    /// Records an exception thrown while drawing inside a native callback, to be rethrown once back in managed code.
    /// Only the first one is kept.
    /// </summary>
    public void SetFailure(Exception exception)
    {
        _failure ??= ExceptionDispatchInfo.Capture(exception);
    }

    public ExceptionDispatchInfo? TakeFailure()
    {
        var failure = _failure;
        _failure = null;
        return failure;
    }

    public bool IsVisible(OptionalContentCondition condition)
    {
        if (_state is null)
        {
            return true;
        }

        if (!_visible.TryGetValue(condition, out bool visible))
        {
            visible = condition.IsVisible(_state);
            _visible[condition] = visible;
        }

        return visible;
    }
}

/// <summary>
/// Paint drawn under a non-trivial optional content condition: drawn only when the condition is visible in the
/// composition's state.
/// </summary>
internal sealed class ConditionalPictureDrawable : SKDrawable
{
    private readonly SKPicture _picture;
    private readonly OptionalContentCondition _condition;
    private readonly LayeredComposition _composition;

    public ConditionalPictureDrawable(SKPicture picture, OptionalContentCondition condition, LayeredComposition composition)
    {
        _picture = picture;
        _condition = condition;
        _composition = composition;
    }

    public OptionalContentCondition Condition => _condition;

    protected override void OnDraw(SKCanvas canvas)
    {
        // Called from native code: an exception must not cross back into Skia. Draw nothing and let
        // SkiaLayeredPage.Compose rethrow it once Snapshot() has returned.
        try
        {
            if (_composition.IsVisible(_condition))
            {
                canvas.DrawPicture(_picture);
            }
        }
        catch (Exception ex)
        {
            _composition.SetFailure(ex);
        }
    }

    protected override SKRect OnGetBounds() => _picture.CullRect;

    protected override void DisposeManaged()
    {
        _picture.Dispose();
        base.DisposeManaged();
    }
}
