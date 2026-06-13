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
using System.IO;
using System.Linq;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics.Operations;
using UglyToad.PdfPig.Graphics.Operations.General;
using UglyToad.PdfPig.Graphics.Operations.PathConstruction;
using UglyToad.PdfPig.Graphics.Operations.PathPainting;
using UglyToad.PdfPig.Graphics.Operations.SpecialGraphicsState;
using UglyToad.PdfPig.Graphics.Operations.TextPositioning;
using UglyToad.PdfPig.Graphics.Operations.TextShowing;
using UglyToad.PdfPig.Graphics.Operations.TextState;
using UglyToad.PdfPig.Tokens;
using CloseSubpathOp = UglyToad.PdfPig.Graphics.Operations.PathConstruction.CloseSubpath;
using StrokePathOp = UglyToad.PdfPig.Graphics.Operations.PathPainting.StrokePath;
using BeginTextOp = UglyToad.PdfPig.Graphics.Operations.TextObjects.BeginText;
using EndTextOp = UglyToad.PdfPig.Graphics.Operations.TextObjects.EndText;

namespace UglyToad.PdfPig.Rendering.Skia;

// Based on PdfBox PDTextAppearanceHandler.

internal partial class SkiaStreamProcessor
{
    private StreamToken? GenerateTextNormalAppearanceAsStream(Annotation annotation)
    {
        // https://github.com/apache/pdfbox/blob/trunk/pdfbox/src/main/java/org/apache/pdfbox/pdmodel/interactive/annotation/handlers/PDTextAppearanceHandler.java
        string iconName = "Note";
        if (annotation.AnnotationDictionary.TryGet<NameToken>(NameToken.Name, PdfScanner, out var iconNameToken))
        {
            iconName = iconNameToken.Data;
        }

        // Background (fill) colour comes from /C. Adobe uses white when /C is missing.
        double[] color;
        if (annotation.AnnotationDictionary.TryGet<ArrayToken>(NameToken.C, PdfScanner, out var colorToken) &&
            colorToken.Data.Count > 0)
        {
            color = colorToken.Data.OfType<NumericToken>().Select(x => x.Data).ToArray();
        }
        else
        {
            color = [1.0]; // DeviceGray white
        }

        try
        {
            return iconName switch
            {
                "Comment" => DrawCommentIcon(annotation, color),
                "Key" => DrawKeyIcon(annotation, color),
                "Insert" => DrawInsertIcon(annotation, color),
                "Circle" => DrawCircleIcon(annotation, color),
                "RightArrow" => DrawRightArrowIcon(annotation, color),
                "UpArrow" => DrawUpArrowIcon(annotation, color),
                "UpLeftArrow" => DrawUpLeftArrowIcon(annotation, color),
                "Cross" => DrawCrossIcon(annotation, color),
                "Star" => DrawStarIcon(annotation, color),
                "Check" => DrawCheckIcon(annotation, color),
                "RightPointer" => DrawRightPointerIcon(annotation, color),
                "CrossHairs" => DrawCrossHairsIcon(annotation, color),
                "Help" => DrawHelpIcon(annotation, color),
                "Paragraph" => DrawParagraphIcon(annotation, color),
                "NewParagraph" => DrawNewParagraphIcon(annotation, color),
                _ => DrawNoteIcon(annotation, color) // "Note" and any unknown name default to the note icon
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            // TODO - Log
            return null;
        }
    }

    private static StreamToken BuildTextIconAppearance(Annotation annotation,
        float width, float height, byte[] content, DictionaryToken? resources)
    {
        PdfRectangle original = annotation.Rectangle;
        PdfRectangle rect = new PdfRectangle(
            original.BottomLeft.X,
            original.TopLeft.Y - height,
            original.BottomLeft.X + width,
            original.TopLeft.Y);
        PdfRectangle bbox = new PdfRectangle(0, 0, width, height);

        var dict = annotation.AnnotationDictionary
            .With(NameToken.Rect, rect.ToArrayToken());
        dict = setTransformationMatrix(dict, bbox);
        if (resources is not null)
        {
            dict = dict.With(NameToken.Resources, resources);
        }

        return new StreamToken(dict, content);
    }

    private static void WriteLineStyle(MemoryStream ms, double width, int join, int cap)
    {
        new SetMiterLimit(4).Write(ms);
        new SetLineJoin(join).Write(ms);
        new SetLineCap(cap).Write(ms);
        new SetLineWidth(width).Write(ms);
    }

    private static void WriteCircle(MemoryStream ms, double x, double y, double r, bool clockwise)
    {
        double magic = r * 0.551784;
        new BeginNewSubpath(x, y + r).Write(ms);
        if (clockwise)
        {
            new AppendDualControlPointBezierCurve(x + magic, y + r, x + r, y + magic, x + r, y).Write(ms);
            new AppendDualControlPointBezierCurve(x + r, y - magic, x + magic, y - r, x, y - r).Write(ms);
            new AppendDualControlPointBezierCurve(x - magic, y - r, x - r, y - magic, x - r, y).Write(ms);
            new AppendDualControlPointBezierCurve(x - r, y + magic, x - magic, y + r, x, y + r).Write(ms);
        }
        else
        {
            new AppendDualControlPointBezierCurve(x - magic, y + r, x - r, y + magic, x - r, y).Write(ms);
            new AppendDualControlPointBezierCurve(x - r, y - magic, x - magic, y - r, x, y - r).Write(ms);
            new AppendDualControlPointBezierCurve(x + magic, y - r, x + r, y - magic, x + r, y).Write(ms);
            new AppendDualControlPointBezierCurve(x + r, y + magic, x + magic, y + r, x, y + r).Write(ms);
        }

        CloseSubpathOp.Value.Write(ms);
    }

    /// <summary>
    /// Resources dictionary exposing the Standard 14 Helvetica font as /F0, used by the text-based icons.
    /// </summary>
    private static DictionaryToken CreateHelveticaResources()
    {
        var font = new DictionaryToken(new Dictionary<NameToken, IToken>
            {
                { NameToken.Type, NameToken.Font },
                { NameToken.Subtype, NameToken.Type1 },
                { NameToken.BaseFont, NameToken.Create("Helvetica") }
            });
        var fonts = new DictionaryToken(new Dictionary<NameToken, IToken> { { NameToken.Create("F0"), font } });
        return new DictionaryToken(new Dictionary<NameToken, IToken> { { NameToken.Font, fonts } });
    }

    private static void WriteGlyphText(MemoryStream ms, double[] fillColor, ReadOnlyMemory<byte> codes,
        double fontSize, double x, double y)
    {
        GetAnnotationNonStrokeColorOperation(fillColor)?.Write(ms);
        BeginTextOp.Value.Write(ms);
        new SetFontAndSize(NameToken.Create("F0"), fontSize).Write(ms);
        new SetTextMatrix([1, 0, 0, 1, x, y]).Write(ms);
        new ShowText(codes).Write(ms);
        EndTextOp.Value.Write(ms);
    }

    private static StreamToken DrawNoteIcon(Annotation annotation, double[] color)
    {
        const float width = 18f;
        const float height = 20f;
        using var ms = new MemoryStream();

        GetAnnotationNonStrokeColorOperation(color)?.Write(ms);
        WriteLineStyle(ms, 0.61, 1, 0);

        new AppendRectangle(1, 1, width - 2, height - 2).Write(ms);

        // Four horizontal "text" lines.
        for (int line = 2; line <= 5; line++)
        {
            new BeginNewSubpath(width / 4f, height / 7f * line).Write(ms);
            new AppendStraightLineSegment(width * 3f / 4f - 1f, height / 7f * line).Write(ms);
        }

        FillPathNonZeroWindingAndStroke.Value.Write(ms);
        return BuildTextIconAppearance(annotation, width, height, ms.ToArray(), null);
    }

    private static StreamToken DrawInsertIcon(Annotation annotation, double[] color)
    {
        const float width = 17f;
        const float height = 20f;
        using var ms = new MemoryStream();

        GetAnnotationNonStrokeColorOperation(color)?.Write(ms);
        WriteLineStyle(ms, 0.59, 0, 0);

        new BeginNewSubpath(width / 2f - 1f, height - 2f).Write(ms);
        new AppendStraightLineSegment(1, 1).Write(ms);
        new AppendStraightLineSegment(width - 2f, 1).Write(ms);
        CloseFillPathNonZeroWindingAndStroke.Value.Write(ms);

        return BuildTextIconAppearance(annotation, width, height, ms.ToArray(), null);
    }

    private static StreamToken DrawCircleIcon(Annotation annotation, double[] color)
    {
        const float width = 20f;
        const float height = 20f;
        using var ms = new MemoryStream();

        GetAnnotationNonStrokeColorOperation(color)?.Write(ms);
        WriteLineStyle(ms, 0.59, 1, 0);

        WriteCircle(ms, width / 2f, height / 2f, 6.36, true);
        WriteCircle(ms, width / 2f, height / 2f, 9.756, false);
        FillPathNonZeroWindingAndStroke.Value.Write(ms);

        return BuildTextIconAppearance(annotation, width, height, ms.ToArray(), null);
    }

    private static StreamToken DrawRightArrowIcon(Annotation annotation, double[] color)
    {
        const float width = 20f;
        const float height = 20f;
        using var ms = new MemoryStream();

        GetAnnotationNonStrokeColorOperation(color)?.Write(ms);
        WriteLineStyle(ms, 0.59, 1, 0);

        WriteCircle(ms, width / 2f, height / 2f, width / 2f - 1f, false);
        FillPathNonZeroWindingAndStroke.Value.Write(ms);

        new SetStrokeColorDeviceGray(1).Write(ms);
        WriteLineStyle(ms, 1.6, 1, 1);
        new BeginNewSubpath(5, 10).Write(ms);
        new AppendStraightLineSegment(15, 10).Write(ms);
        new BeginNewSubpath(10.5, 5.5).Write(ms);
        new AppendStraightLineSegment(15, 10).Write(ms);
        new AppendStraightLineSegment(10.5, 14.5).Write(ms);
        StrokePathOp.Value.Write(ms);

        return BuildTextIconAppearance(annotation, width, height, ms.ToArray(), null);
    }

    private static StreamToken DrawUpArrowIcon(Annotation annotation, double[] color)
    {
        const float width = 17f;
        const float height = 20f;
        using var ms = new MemoryStream();

        GetAnnotationNonStrokeColorOperation(color)?.Write(ms);
        WriteLineStyle(ms, 0.59, 1, 0);
        WriteUpArrowPath(ms);
        CloseFillPathNonZeroWindingAndStroke.Value.Write(ms);

        return BuildTextIconAppearance(annotation, width, height, ms.ToArray(), null);
    }

    private static StreamToken DrawUpLeftArrowIcon(Annotation annotation, double[] color)
    {
        const float width = 17f;
        const float height = 17f;
        using var ms = new MemoryStream();

        GetAnnotationNonStrokeColorOperation(color)?.Write(ms);
        WriteLineStyle(ms, 0.59, 1, 0);

        const double cos = 0.70710678, sin = 0.70710678; // Rotate 45° (PdfBox Matrix.getRotateInstance(45°, 8, -4)).
        new ModifyCurrentTransformationMatrix([cos, sin, -sin, cos, 8, -4]).Write(ms);
        WriteUpArrowPath(ms);
        CloseFillPathNonZeroWindingAndStroke.Value.Write(ms);

        return BuildTextIconAppearance(annotation, width, height, ms.ToArray(), null);
    }

    private static void WriteUpArrowPath(MemoryStream ms)
    {
        new BeginNewSubpath(1, 7).Write(ms);
        new AppendStraightLineSegment(5, 7).Write(ms);
        new AppendStraightLineSegment(5, 1).Write(ms);
        new AppendStraightLineSegment(12, 1).Write(ms);
        new AppendStraightLineSegment(12, 7).Write(ms);
        new AppendStraightLineSegment(16, 7).Write(ms);
        new AppendStraightLineSegment(8.5, 19).Write(ms);
    }

    private static StreamToken DrawCrossIcon(Annotation annotation, double[] color)
    {
        const float width = 20f;
        const float height = 19f;
        using var ms = new MemoryStream();

        GetAnnotationStrokeColorOperation(color)?.Write(ms);
        WriteLineStyle(ms, 3.0, 1, 1);
        new BeginNewSubpath(4, 4).Write(ms);
        new AppendStraightLineSegment(width - 4, height - 4).Write(ms);
        new BeginNewSubpath(width - 4, 4).Write(ms);
        new AppendStraightLineSegment(4, height - 4).Write(ms);
        StrokePathOp.Value.Write(ms);

        return BuildTextIconAppearance(annotation, width, height, ms.ToArray(), null);
    }

    private static StreamToken DrawStarIcon(Annotation annotation, double[] color)
    {
        const float width = 20f;
        const float height = 19f;
        using var ms = new MemoryStream();

        GetAnnotationNonStrokeColorOperation(color)?.Write(ms);
        WriteLineStyle(ms, 0.5, 1, 0);

        double cx = width / 2f, cy = height / 2f, rOuter = 9.0, rInner = 3.7;
        for (int i = 0; i < 10; i++)
        {
            double angle = Math.PI / 2.0 + i * Math.PI / 5.0;
            double r = (i % 2 == 0) ? rOuter : rInner;
            double px = cx + r * Math.Cos(angle);
            double py = cy + r * Math.Sin(angle);
            if (i == 0)
            {
                new BeginNewSubpath(px, py).Write(ms);
            }
            else
            {
                new AppendStraightLineSegment(px, py).Write(ms);
            }
        }

        CloseSubpathOp.Value.Write(ms);
        FillPathNonZeroWinding.Value.Write(ms);

        return BuildTextIconAppearance(annotation, width, height, ms.ToArray(), null);
    }

    private static StreamToken DrawCheckIcon(Annotation annotation, double[] color)
    {
        const float width = 20f;
        const float height = 19f;
        using var ms = new MemoryStream();

        GetAnnotationStrokeColorOperation(color)?.Write(ms);
        WriteLineStyle(ms, 3.0, 1, 1);
        new BeginNewSubpath(3.5, 10).Write(ms);
        new AppendStraightLineSegment(7.5, 4).Write(ms);
        new AppendStraightLineSegment(16.5, 16).Write(ms);
        StrokePathOp.Value.Write(ms);

        return BuildTextIconAppearance(annotation, width, height, ms.ToArray(), null);
    }

    private static StreamToken DrawRightPointerIcon(Annotation annotation, double[] color)
    {
        const float width = 20f;
        const float height = 17f;
        using var ms = new MemoryStream();

        GetAnnotationNonStrokeColorOperation(color)?.Write(ms);
        WriteLineStyle(ms, 0.59, 1, 0);
        new BeginNewSubpath(2, 11).Write(ms);
        new AppendStraightLineSegment(11, 11).Write(ms);
        new AppendStraightLineSegment(11, 15).Write(ms);
        new AppendStraightLineSegment(18, height / 2f).Write(ms);
        new AppendStraightLineSegment(11, 2).Write(ms);
        new AppendStraightLineSegment(11, 6).Write(ms);
        new AppendStraightLineSegment(2, 6).Write(ms);
        CloseSubpathOp.Value.Write(ms);
        FillPathNonZeroWinding.Value.Write(ms);

        return BuildTextIconAppearance(annotation, width, height, ms.ToArray(), null);
    }

    private static StreamToken DrawCrossHairsIcon(Annotation annotation, double[] color)
    {
        const float width = 20f;
        const float height = 20f;
        using var ms = new MemoryStream();

        GetAnnotationStrokeColorOperation(color)?.Write(ms);
        WriteLineStyle(ms, 1.2, 1, 0);
        WriteCircle(ms, width / 2f, height / 2f, 8, true);
        StrokePathOp.Value.Write(ms);

        new BeginNewSubpath(width / 2f, 1).Write(ms);
        new AppendStraightLineSegment(width / 2f, height - 1).Write(ms);
        new BeginNewSubpath(1, height / 2f).Write(ms);
        new AppendStraightLineSegment(width - 1, height / 2f).Write(ms);
        StrokePathOp.Value.Write(ms);

        return BuildTextIconAppearance(annotation, width, height, ms.ToArray(), null);
    }

    private static StreamToken DrawHelpIcon(Annotation annotation, double[] color)
    {
        const float width = 20f;
        const float height = 20f;
        using var ms = new MemoryStream();

        GetAnnotationNonStrokeColorOperation(color)?.Write(ms);
        WriteLineStyle(ms, 0.59, 1, 0);
        WriteCircle(ms, width / 2f, height / 2f, width / 2f - 1f, false);
        FillPathNonZeroWindingAndStroke.Value.Write(ms);

        WriteGlyphText(ms, [1.0], new byte[] { 0x3F }, 14, 5.8, 4.5); // "?"

        return BuildTextIconAppearance(annotation, width, height, ms.ToArray(), CreateHelveticaResources());
    }

    private static StreamToken DrawParagraphIcon(Annotation annotation, double[] color)
    {
        const float width = 20f;
        const float height = 20f;
        using var ms = new MemoryStream();

        new SetNonStrokeColorDeviceGray(1).Write(ms);
        WriteLineStyle(ms, 0.59, 1, 0);
        WriteCircle(ms, width / 2f, height / 2f, width / 2f - 1f, false);
        FillPathNonZeroWinding.Value.Write(ms);

        WriteGlyphText(ms, color, new byte[] { 0xB6 }, 14, 6.0, 4.5); // "paragraph"

        new SetStrokeColorDeviceGray(0).Write(ms);
        WriteCircle(ms, width / 2f, height / 2f, width / 2f - 1f, true);
        StrokePathOp.Value.Write(ms);

        return BuildTextIconAppearance(annotation, width, height, ms.ToArray(), CreateHelveticaResources());
    }

    private static StreamToken DrawNewParagraphIcon(Annotation annotation, double[] color)
    {
        const float width = 13f;
        const float height = 20f;
        using var ms = new MemoryStream();

        GetAnnotationNonStrokeColorOperation(color)?.Write(ms);
        WriteLineStyle(ms, 0.59, 0, 0);
        new BeginNewSubpath(6.4995, 20).Write(ms);
        new AppendStraightLineSegment(0.295, 7.287).Write(ms);
        new AppendStraightLineSegment(12.705, 7.287).Write(ms);
        CloseFillPathNonZeroWindingAndStroke.Value.Write(ms);

        WriteGlyphText(ms, color, new byte[] { 0x4E, 0x50 }, 7, 1.65, 1.4); // "NP"

        return BuildTextIconAppearance(annotation, width, height, ms.ToArray(), CreateHelveticaResources());
    }

    private static StreamToken DrawCommentIcon(Annotation annotation, double[] color)
    {
        const float width = 18f;
        const float height = 18f;
        using var ms = new MemoryStream();

        GetAnnotationNonStrokeColorOperation(color)?.Write(ms);
        WriteLineStyle(ms, 200, 1, 0);

        new ModifyCurrentTransformationMatrix([0.003, 0, 0, 0.003, 0, 0]).Write(ms);
        new ModifyCurrentTransformationMatrix([1, 0, 0, 1, 500, -300]).Write(ms);

        new BeginNewSubpath(2549, 5269).Write(ms);
        new AppendDualControlPointBezierCurve(1307, 5269, 300, 4451, 300, 3441).Write(ms);
        new AppendDualControlPointBezierCurve(300, 3023, 474, 2640, 764, 2331).Write(ms);
        new AppendDualControlPointBezierCurve(633, 1985, 361, 1691, 357, 1688).Write(ms);
        new AppendDualControlPointBezierCurve(299, 1626, 283, 1537, 316, 1459).Write(ms);
        new AppendDualControlPointBezierCurve(350, 1382, 426, 1332, 510, 1332).Write(ms);
        new AppendDualControlPointBezierCurve(1051, 1332, 1477, 1558, 1733, 1739).Write(ms);
        new AppendDualControlPointBezierCurve(1987, 1659, 2261, 1613, 2549, 1613).Write(ms);
        new AppendDualControlPointBezierCurve(3792, 1613, 4799, 2431, 4799, 3441).Write(ms);
        new AppendDualControlPointBezierCurve(4799, 4451, 3792, 5269, 2549, 5269).Write(ms);
        CloseSubpathOp.Value.Write(ms);

        const double inset = 0.3 / 0.003 - 500;          // -400
        const double bottom = 0.3 / 0.003 + 300;         // 400
        const double side = 17.4 / 0.003;                // 5800
        new BeginNewSubpath(inset, bottom).Write(ms);
        new AppendStraightLineSegment(inset, bottom + side).Write(ms);
        new AppendStraightLineSegment(inset + side, bottom + side).Write(ms);
        new AppendStraightLineSegment(inset + side, bottom).Write(ms);
        CloseFillPathNonZeroWindingAndStroke.Value.Write(ms);

        return BuildTextIconAppearance(annotation, width, height, ms.ToArray(), null);
    }

    private static StreamToken DrawKeyIcon(Annotation annotation, double[] color)
    {
        const float width = 13f;
        const float height = 18f;
        using var ms = new MemoryStream();

        GetAnnotationNonStrokeColorOperation(color)?.Write(ms);
        WriteLineStyle(ms, 200, 1, 0);

        new ModifyCurrentTransformationMatrix([0.003, 0, 0, 0.003, 0, 0]).Write(ms);
        const double cos = 0.70710678, sin = 0.70710678;
        new ModifyCurrentTransformationMatrix([cos, sin, -sin, cos, 2500, -800]).Write(ms);

        new BeginNewSubpath(4799, 4004).Write(ms);
        new AppendDualControlPointBezierCurve(4799, 3149, 4107, 2457, 3253, 2457).Write(ms);
        new AppendDualControlPointBezierCurve(3154, 2457, 3058, 2466, 2964, 2484).Write(ms);
        new AppendStraightLineSegment(2753, 2246).Write(ms);
        new AppendDualControlPointBezierCurve(2713, 2201, 2656, 2175, 2595, 2175).Write(ms);
        new AppendStraightLineSegment(2268, 2175).Write(ms);
        new AppendStraightLineSegment(2268, 1824).Write(ms);
        new AppendDualControlPointBezierCurve(2268, 1707, 2174, 1613, 2057, 1613).Write(ms);
        new AppendStraightLineSegment(1706, 1613).Write(ms);
        new AppendStraightLineSegment(1706, 1261).Write(ms);
        new AppendDualControlPointBezierCurve(1706, 1145, 1611, 1050, 1495, 1050).Write(ms);
        new AppendStraightLineSegment(510, 1050).Write(ms);
        new AppendDualControlPointBezierCurve(394, 1050, 300, 1145, 300, 1261).Write(ms);
        new AppendStraightLineSegment(300, 1947).Write(ms);
        new AppendDualControlPointBezierCurve(300, 2003, 322, 2057, 361, 2097).Write(ms);
        new AppendStraightLineSegment(1783, 3519).Write(ms);
        new AppendDualControlPointBezierCurve(1733, 3671, 1706, 3834, 1706, 4004).Write(ms);
        new AppendDualControlPointBezierCurve(1706, 4858, 2398, 5550, 3253, 5550).Write(ms);
        new AppendDualControlPointBezierCurve(4109, 5550, 4799, 4860, 4799, 4004).Write(ms);
        CloseSubpathOp.Value.Write(ms);

        new BeginNewSubpath(3253, 4425).Write(ms);
        new AppendDualControlPointBezierCurve(3253, 4192, 3441, 4004, 3674, 4004).Write(ms);
        new AppendDualControlPointBezierCurve(3907, 4004, 4096, 4192, 4096, 4425).Write(ms);
        new AppendDualControlPointBezierCurve(4096, 4658, 3907, 4847, 3674, 4847).Write(ms);
        new AppendDualControlPointBezierCurve(3441, 4847, 3253, 4658, 3253, 4425).Write(ms);
        FillPathNonZeroWindingAndStroke.Value.Write(ms);

        return BuildTextIconAppearance(annotation, width, height, ms.ToArray(), null);
    }
}
