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
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Rendering.Skia.Helpers;
using UglyToad.PdfPig.Tokens;

namespace UglyToad.PdfPig.Rendering.Skia;

/// <summary>
/// Renderer-side <see cref="IColorSpaceContext"/> decorator over PdfPig's default context. It adds two
/// rendering concerns that the core context deliberately does not carry:
/// <list type="bullet">
/// <item>captures the operand colour components supplied alongside a pattern name in <c>SCN</c>/<c>scn</c>
/// (the core context drops these; uncoloured tiling patterns need them); and</item>
/// <item>colour-manages device colours through the page's output intent (PDF 2.0 14.11.5 / 8.6.5.7) at
/// colour-set time, so the managed colour is stored on the graphics state and every paint operation uses
/// it without further work. The effective output intent (and its suppression inside soft masks) is read
/// from <see cref="CurrentGraphicsState.OutputIntent"/>.</item>
/// </list>
/// </summary>
internal sealed class PatternAwareColorSpaceContext : IColorSpaceContext
{
    private readonly IColorSpaceContext _inner;
    private readonly Func<CurrentGraphicsState> _currentState;

    public IReadOnlyList<double>? LastNonStrokingPatternOperands { get; private set; }

    public IReadOnlyList<double>? LastStrokingPatternOperands { get; private set; }

    public PatternAwareColorSpaceContext(IColorSpaceContext inner, Func<CurrentGraphicsState> currentState)
    {
        _inner = inner;
        _currentState = currentState;
    }

    public ColorSpaceDetails CurrentStrokingColorSpace => _inner.CurrentStrokingColorSpace;

    public ColorSpaceDetails CurrentNonStrokingColorSpace => _inner.CurrentNonStrokingColorSpace;

    public void SetStrokingColorspace(NameToken colorspace, DictionaryToken? dictionary = null)
    {
        LastStrokingPatternOperands = null;
        _inner.SetStrokingColorspace(colorspace, dictionary);
    }

    public void SetNonStrokingColorspace(NameToken colorspace, DictionaryToken? dictionary = null)
    {
        LastNonStrokingPatternOperands = null;
        _inner.SetNonStrokingColorspace(colorspace, dictionary);
    }

    public void SetStrokingColor(IReadOnlyList<double> operands, NameToken? patternName = null)
    {
        LastStrokingPatternOperands = patternName is not null && operands?.Count > 0
            ? operands.ToArray()
            : null;
        _inner.SetStrokingColor(operands, patternName);
        ApplyOutputIntent(stroking: true);
    }

    public void SetStrokingColorGray(double gray)
    {
        LastStrokingPatternOperands = null;
        _inner.SetStrokingColorGray(gray);
        ApplyOutputIntent(stroking: true);
    }

    public void SetStrokingColorRgb(double r, double g, double b)
    {
        LastStrokingPatternOperands = null;
        _inner.SetStrokingColorRgb(r, g, b);
        ApplyOutputIntent(stroking: true);
    }

    public void SetStrokingColorCmyk(double c, double m, double y, double k)
    {
        LastStrokingPatternOperands = null;
        _inner.SetStrokingColorCmyk(c, m, y, k);
        ApplyOutputIntent(stroking: true);
    }

    public void SetNonStrokingColor(IReadOnlyList<double> operands, NameToken? patternName = null)
    {
        LastNonStrokingPatternOperands = patternName is not null && operands?.Count > 0
            ? operands.ToArray()
            : null;
        _inner.SetNonStrokingColor(operands, patternName);
        ApplyOutputIntent(stroking: false);
    }

    public void SetNonStrokingColorGray(double gray)
    {
        LastNonStrokingPatternOperands = null;
        _inner.SetNonStrokingColorGray(gray);
        ApplyOutputIntent(stroking: false);
    }

    public void SetNonStrokingColorRgb(double r, double g, double b)
    {
        LastNonStrokingPatternOperands = null;
        _inner.SetNonStrokingColorRgb(r, g, b);
        ApplyOutputIntent(stroking: false);
    }

    public void SetNonStrokingColorCmyk(double c, double m, double y, double k)
    {
        LastNonStrokingPatternOperands = null;
        _inner.SetNonStrokingColorCmyk(c, m, y, k);
        ApplyOutputIntent(stroking: false);
    }

    /// <summary>
    /// After the inner context has set the natural (device) colour, replace it with the output-intent
    /// managed colour when the current colour space is a device space and the graphics state carries an
    /// effective output intent. Runs at colour-set time, so the rendering intent in effect when the colour
    /// is set is used (consistent with how every other colour is converted), and the managed colour is then
    /// reused by all paint operations.
    /// </summary>
    private void ApplyOutputIntent(bool stroking)
    {
        var state = _currentState();
        var profile = state.OutputIntent?.DestOutputProfile;
        if (profile is null)
        {
            return;
        }

        var colorSpace = stroking ? _inner.CurrentStrokingColorSpace : _inner.CurrentNonStrokingColorSpace;
        var color = stroking ? state.CurrentStrokingColor : state.CurrentNonStrokingColor;

        if (!OutputIntentColorManagement.TryConvert(color, colorSpace.Type, profile, state.RenderingIntent, out var managed))
        {
            return;
        }

        if (stroking)
        {
            state.CurrentStrokingColor = managed;
        }
        else
        {
            state.CurrentNonStrokingColor = managed;
        }
    }

    public IColorSpaceContext DeepClone()
    {
        return new PatternAwareColorSpaceContext(_inner.DeepClone(), _currentState)
        {
            LastNonStrokingPatternOperands = LastNonStrokingPatternOperands,
            LastStrokingPatternOperands = LastStrokingPatternOperands,
        };
    }
}
