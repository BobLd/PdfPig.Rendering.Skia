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
using UglyToad.PdfPig.Graphics.Operations.PathPainting;
using UglyToad.PdfPig.Graphics.Operations.SpecialGraphicsState;
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
        /// <item>No stroking paint operator (<c>S s B B* b b*</c>) appears. The vector path is
        /// replayed as a single fill governed by the text rendering mode, so it cannot reproduce
        /// a stroke's line width, dash pattern, cap/join or stroking colour. A d1 stencil is
        /// allowed to stroke (only colour is inherited, not the other stroke parameters), so such
        /// glyphs must run their real operators via the picture path to look correct.</item>
        /// </list>
        /// Anything else (d0 coloured glyphs, bitmaps, stroked stencils, missing marker) falls into
        /// the picture path.
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
                    case StrokePath:                            // S
                    case CloseAndStrokePath:                    // s
                    case FillPathNonZeroWindingAndStroke:       // B
                    case FillPathEvenOddRuleAndStroke:          // B*
                    case CloseFillPathNonZeroWindingAndStroke:  // b
                    case CloseFillPathEvenOddRuleAndStroke:     // b*
                        return false; // stroke appearance can't be replayed by a fill-only path
                }
            }
            return sawD1;
        }

        /// <summary>
        /// Walk the operations once and translate path-construction operators into the returned
        /// <see cref="SKPath"/>. Painting, clipping, colour, and width ops are ignored — for a d1
        /// stencil they don't influence the cacheable geometry. Transform ops (<c>cm</c>) and the
        /// save/restore stack (<c>q</c>/<c>Q</c>) ARE honoured: a CharProc routinely establishes a
        /// glyph-internal scale (e.g. <c>0.01 0 0 0.01 0 0 cm</c>) before drawing, so every point
        /// must be mapped through the CharProc's own CTM. Ignoring it would render the glyph at the
        /// raw drawing coordinates — orders of magnitude too large. The path is returned in glyph
        /// space (the CharProc's internal CTM baked in, but pre-fontMatrix); callers apply
        /// <c>fontMatrix × renderingMatrix × textMatrix</c> via the canvas CTM at draw time.
        /// </summary>
        private static SKPath ExtractVectorPath(IReadOnlyList<IGraphicsStateOperation> operations)
        {
            var raw = new SKPath { FillType = SKPathFillType.Winding };

            // CharProc-local CTM, seeded at identity. Mirrors BaseStreamProcessor's concatenation
            // (CTM' = cm × CTM) so the baked geometry matches the un-cached picture pipeline.
            var ctm = TransformationMatrix.Identity;
            Stack<TransformationMatrix>? ctmStack = null;

            for (int i = 0; i < operations.Count; ++i)
            {
                switch (operations[i])
                {
                    case ModifyCurrentTransformationMatrix cm:
                        ctm = TransformationMatrix.FromArray(cm.Value).Multiply(ctm);
                        break;
                    case Push:
                        (ctmStack ??= new Stack<TransformationMatrix>()).Push(ctm);
                        break;
                    case Pop:
                        if (ctmStack is { Count: > 0 })
                        {
                            ctm = ctmStack.Pop();
                        }
                        break;
                    case BeginNewSubpath m:
                    {
                        var (x, y) = ctm.Transform(m.X, m.Y);
                        raw.MoveTo((float)x, (float)y);
                        break;
                    }
                    case AppendStraightLineSegment l:
                    {
                        var (x, y) = ctm.Transform(l.X, l.Y);
                        raw.LineTo((float)x, (float)y);
                        break;
                    }
                    case AppendDualControlPointBezierCurve c:
                    {
                        var (x1, y1) = ctm.Transform(c.X1, c.Y1);
                        var (x2, y2) = ctm.Transform(c.X2, c.Y2);
                        var (x3, y3) = ctm.Transform(c.X3, c.Y3);
                        raw.CubicTo((float)x1, (float)y1, (float)x2, (float)y2, (float)x3, (float)y3);
                        break;
                    }
                    case AppendStartControlPointBezierCurve v:
                    {
                        // 'v' uses the current point as the first control point; SkiaSharp has
                        // no direct equivalent, so emit the cubic with current-point CP1.
                        var lp = raw.LastPoint;
                        var (x2, y2) = ctm.Transform(v.X2, v.Y2);
                        var (x3, y3) = ctm.Transform(v.X3, v.Y3);
                        raw.CubicTo(lp.X, lp.Y, (float)x2, (float)y2, (float)x3, (float)y3);
                        break;
                    }
                    case AppendEndControlPointBezierCurve y:
                    {
                        // 'y' uses (x3,y3) as both the second control point and the end point.
                        var (x1, y1) = ctm.Transform(y.X1, y.Y1);
                        var (x3, y3) = ctm.Transform(y.X3, y.Y3);
                        raw.CubicTo((float)x1, (float)y1, (float)x3, (float)y3, (float)x3, (float)y3);
                        break;
                    }
                    case AppendRectangle r:
                    {
                        // Emit as an explicit subpath so a rotated/skewed CTM is honoured (AddRect
                        // would force an axis-aligned box).
                        var (ax, ay) = ctm.Transform(r.LowerLeftX, r.LowerLeftY);
                        var (bx, by) = ctm.Transform(r.LowerLeftX + r.Width, r.LowerLeftY);
                        var (cx, cy) = ctm.Transform(r.LowerLeftX + r.Width, r.LowerLeftY + r.Height);
                        var (dx, dy) = ctm.Transform(r.LowerLeftX, r.LowerLeftY + r.Height);
                        raw.MoveTo((float)ax, (float)ay);
                        raw.LineTo((float)bx, (float)by);
                        raw.LineTo((float)cx, (float)cy);
                        raw.LineTo((float)dx, (float)dy);
                        raw.Close();
                        break;
                    }
                    case CloseSubpath:
                        raw.Close();
                        break;
                }
            }

            return raw;
        }
    }
}
