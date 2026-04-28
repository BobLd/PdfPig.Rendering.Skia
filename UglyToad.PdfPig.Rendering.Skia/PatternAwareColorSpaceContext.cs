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

using System.Collections.Generic;
using System.Linq;
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Tokens;

namespace UglyToad.PdfPig.Rendering.Skia;

/// <summary>
/// Wraps an <see cref="IColorSpaceContext"/> to capture the operand colour components
/// supplied alongside a pattern name in <c>SCN</c>/<c>scn</c> operations. PdfPig's default
/// context drops these operands; uncoloured tiling patterns need them to compute the
/// underlying-space colour to paint with.
/// </summary>
internal sealed class PatternAwareColorSpaceContext : IColorSpaceContext
{
    private readonly IColorSpaceContext _inner;

    public IReadOnlyList<double>? LastNonStrokingPatternOperands { get; private set; }

    public IReadOnlyList<double>? LastStrokingPatternOperands { get; private set; }

    public PatternAwareColorSpaceContext(IColorSpaceContext inner)
    {
        _inner = inner;
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
    }

    public void SetStrokingColorGray(double gray)
    {
        LastStrokingPatternOperands = null;
        _inner.SetStrokingColorGray(gray);
    }

    public void SetStrokingColorRgb(double r, double g, double b)
    {
        LastStrokingPatternOperands = null;
        _inner.SetStrokingColorRgb(r, g, b);
    }

    public void SetStrokingColorCmyk(double c, double m, double y, double k)
    {
        LastStrokingPatternOperands = null;
        _inner.SetStrokingColorCmyk(c, m, y, k);
    }

    public void SetNonStrokingColor(IReadOnlyList<double> operands, NameToken? patternName = null)
    {
        LastNonStrokingPatternOperands = patternName is not null && operands?.Count > 0
            ? operands.ToArray()
            : null;
        _inner.SetNonStrokingColor(operands, patternName);
    }

    public void SetNonStrokingColorGray(double gray)
    {
        LastNonStrokingPatternOperands = null;
        _inner.SetNonStrokingColorGray(gray);
    }

    public void SetNonStrokingColorRgb(double r, double g, double b)
    {
        LastNonStrokingPatternOperands = null;
        _inner.SetNonStrokingColorRgb(r, g, b);
    }

    public void SetNonStrokingColorCmyk(double c, double m, double y, double k)
    {
        LastNonStrokingPatternOperands = null;
        _inner.SetNonStrokingColorCmyk(c, m, y, k);
    }

    public IColorSpaceContext DeepClone()
    {
        return new PatternAwareColorSpaceContext(_inner.DeepClone())
        {
            LastNonStrokingPatternOperands = LastNonStrokingPatternOperands,
            LastStrokingPatternOperands = LastStrokingPatternOperands,
        };
    }
}
