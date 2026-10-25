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
using System.Reflection;
using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.PdfFonts;
using UglyToad.PdfPig.Tokens;
using Xunit;

namespace UglyToad.PdfPig.Rendering.Skia.Tests;

/// <summary>
/// The document-scoped font cache must free everything it holds when the document is disposed, even
/// when some of its glyph outlines failed to build.
/// </summary>
public class FontCacheDisposalTests
{
    private static readonly Type FontCacheType = typeof(SkiaPageFactory).Assembly
        .GetType("UglyToad.PdfPig.Rendering.Skia.Helpers.SkiaFontCache", true)!;

    [Fact]
    public void DisposeSkipsGlyphOutlinesThatFailedToBuild()
    {
        // No document is known to make an outline fail to build (thousands of stress-corpus files
        // render without), so the internal cache is driven directly. Lazy<T> rethrows a failed
        // build's exception on Value: Dispose used to stop there, leaking all it had yet to free.
        var cache = (IDisposable)Activator.CreateInstance(FontCacheType, nonPublic: true)!;
        MethodInfo tryGetPath = FontCacheType.GetMethod("TryGetPath", BindingFlags.Public | BindingFlags.Instance)!;
        var font = new StubFont();

        object?[] square = [font, StubFont.Square, null];
        Assert.True((bool)tryGetPath.Invoke(cache, square)!);
        var path = (SKPath)square[2]!;

        var failure = Assert.Throws<TargetInvocationException>(() =>
        {
            tryGetPath.Invoke(cache, [font, StubFont.Broken, null]);
        });
        Assert.IsType<InvalidOperationException>(failure.InnerException);

        cache.Dispose();

        Assert.Equal(IntPtr.Zero, path.Handle);
    }

    /// <summary>
    /// A font whose glyph <see cref="Square"/> is a unit square and whose glyph <see cref="Broken"/>
    /// cannot be read. Nothing else is used by the cache.
    /// </summary>
    private sealed class StubFont : IFont
    {
        public const int Square = 1;
        public const int Broken = 2;

        public NameToken? Name { get; } = NameToken.Create("Stub");

        public bool IsVertical => false;

        public FontDetails Details => throw new NotSupportedException();

        public int ReadCharacterCode(IInputBytes bytes, out int codeLength) => throw new NotSupportedException();

        public bool TryGetUnicode(int characterCode, out string? value) => throw new NotSupportedException();

        public CharacterBoundingBox GetBoundingBox(int characterCode) => throw new NotSupportedException();

        public TransformationMatrix GetFontMatrix() => throw new NotSupportedException();

        public double GetDescent() => throw new NotSupportedException();

        public double GetAscent() => throw new NotSupportedException();

        public bool TryGetPath(int characterCode, out IReadOnlyList<PdfSubpath>? path) => throw new NotSupportedException();

        public bool TryGetGlyphIndex(int characterCode, out int glyphIndex) => throw new NotSupportedException();

        public bool TryGetNormalisedPath(int characterCode, out IReadOnlyList<PdfSubpath>? path)
        {
            if (characterCode == Broken)
            {
                throw new InvalidOperationException("Malformed glyph.");
            }

            var subpath = new PdfSubpath();
            subpath.MoveTo(0, 0);
            subpath.LineTo(1, 0);
            subpath.LineTo(1, 1);
            subpath.LineTo(0, 1);
            subpath.CloseSubpath();
            path = [subpath];
            return true;
        }
    }
}