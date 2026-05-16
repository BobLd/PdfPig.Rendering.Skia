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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics.Operations;
using UglyToad.PdfPig.Graphics.Operations.InlineImages;
using UglyToad.PdfPig.Graphics.Operations.PathConstruction;
using UglyToad.PdfPig.Graphics.Operations.TextState;
using UglyToad.PdfPig.PdfFonts;

namespace UglyToad.PdfPig.Rendering.Skia.Helpers
{
    internal sealed partial class SkiaFontCache
    {
        // Document-scoped: same lifetime as typeface/glyph caches. Outer key is the Type 3 font
        // instance (reference equality — Type 3 fonts are unique per document). Inner key is the
        // PDF character code (not Unicode). Lazy<T> with ExecutionAndPublication serialises the
        // build so two pages racing on the same glyph only pay the cost once.
        private readonly ConcurrentDictionary<IType3Font, ConcurrentDictionary<int, Lazy<Type3CachedGlyph>>>
            _type3Cache = new();

        /// <summary>
        /// Retrieve (or build on first encounter) the cached representation of a Type 3 glyph.
        /// </summary>
        public Type3CachedGlyph GetOrBuildType3Glyph(IType3Font font, int code, Type3BuildContext ctx)
        {
            if (IsDisposed())
            {
                throw new ObjectDisposedException(nameof(SkiaFontCache));
            }

            var perFont = _type3Cache.GetOrAdd(font, _ => new ConcurrentDictionary<int, Lazy<Type3CachedGlyph>>());
            var lazy = perFont.GetOrAdd(code,
                c => new Lazy<Type3CachedGlyph>(
                    () => BuildType3Glyph(font, c, ctx),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            return lazy.Value;
        }

        private static Type3CachedGlyph BuildType3Glyph(IType3Font font, int code, Type3BuildContext ctx)
        {
            if (!font.TryGetCharProc(code, out var charProcStream))
            {
                return Type3MissingGlyph.Instance;
            }

            var contentBytes = charProcStream.Decode(ctx.FilterProvider, ctx.PdfScanner);
            var operations = ctx.PageContentParser.Parse(ctx.PageNumber,
                new MemoryInputBytes(contentBytes), ctx.Logger!);

            if (CanCacheAsVector(operations))
            {
                var path = ExtractVectorPath(operations);
                return new Type3VectorGlyph(path);
            }

            var picture = ctx.RecordPicture(operations, font, code);
            return new Type3PictureGlyph(picture);
        }

        /// <summary>
        /// A CharProc is cacheable as a pure-vector <see cref="SKPath"/> only when:
        /// <list type="bullet">
        /// <item>The first marker is <c>d1</c> (uncoloured stencil — colour-setting ops are
        /// spec-ignored, so the current text colour drives painting at draw time).</item>
        /// <item>No <c>InvokeNamedXObject</c> (Do) or <c>BeginInlineImage</c> (BI/ID/EI) appears
        /// — those produce raster output that can't be replayed as a path.</item>
        /// </list>
        /// Anything else (d0 coloured glyphs, bitmaps, missing marker) falls into the picture path.
        /// </summary>
        private static bool CanCacheAsVector(IReadOnlyList<IGraphicsStateOperation> operations)
        {
            bool sawD1 = false;
            for (int i = 0; i < operations.Count; ++i)
            {
                switch (operations[i])
                {
                    case Type3SetGlyphWidthAndBoundingBox:
                        sawD1 = true;
                        break;
                    case Type3SetGlyphWidth:
                        return false; // d0 — colour baked into the CharProc
                    case InvokeNamedXObject:
                    case BeginInlineImage:
                        return false; // raster content
                }
            }
            return sawD1;
        }

        /// <summary>
        /// Walk the operations once and translate only path-construction operators into the
        /// returned <see cref="SKPath"/>. Painting, clipping, colour, transform, and width ops
        /// are intentionally ignored — for a d1 stencil they don't influence the cacheable
        /// geometry. The path is returned in raw glyph coordinates (pre-fontMatrix); callers
        /// apply <c>fontMatrix × renderingMatrix × textMatrix</c> via the canvas CTM at draw
        /// time, matching the matrix arithmetic of the un-cached pipeline byte-for-byte.
        /// </summary>
        private static SKPath ExtractVectorPath(IReadOnlyList<IGraphicsStateOperation> operations)
        {
            var raw = new SKPath { FillType = SKPathFillType.Winding };
            for (int i = 0; i < operations.Count; ++i)
            {
                switch (operations[i])
                {
                    case BeginNewSubpath m:
                        raw.MoveTo((float)m.X, (float)m.Y);
                        break;
                    case AppendStraightLineSegment l:
                        raw.LineTo((float)l.X, (float)l.Y);
                        break;
                    case AppendDualControlPointBezierCurve c:
                        raw.CubicTo((float)c.X1, (float)c.Y1, (float)c.X2, (float)c.Y2,
                                    (float)c.X3, (float)c.Y3);
                        break;
                    case AppendStartControlPointBezierCurve v:
                        // 'v' uses the current point as the first control point; SkiaSharp has
                        // no direct equivalent, so emit the cubic with current-point CP1.
                        var lp = raw.LastPoint;
                        raw.CubicTo(lp.X, lp.Y, (float)v.X2, (float)v.Y2, (float)v.X3, (float)v.Y3);
                        break;
                    case AppendEndControlPointBezierCurve y:
                        // 'y' uses (x3,y3) as both the second control point and the end point.
                        raw.CubicTo((float)y.X1, (float)y.Y1, (float)y.X3, (float)y.Y3,
                                    (float)y.X3, (float)y.Y3);
                        break;
                    case AppendRectangle r:
                        raw.AddRect(SKRect.Create((float)r.LowerLeftX, (float)r.LowerLeftY,
                                                  (float)r.Width, (float)r.Height));
                        break;
                    case CloseSubpath:
                        raw.Close();
                        break;
                }
            }

            return raw;
        }
    }
}
