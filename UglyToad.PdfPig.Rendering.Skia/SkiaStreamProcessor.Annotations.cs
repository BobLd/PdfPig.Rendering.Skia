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
using System.IO;
using System.Linq;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Geometry;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Graphics.Operations;
using UglyToad.PdfPig.Graphics.Operations.MarkedContent;
using UglyToad.PdfPig.Graphics.Operations.PathConstruction;
using UglyToad.PdfPig.Tokens;

namespace UglyToad.PdfPig.Rendering.Skia
{
    // Based on PdfBox

    internal partial class SkiaStreamProcessor
    {
        /// <summary>
        /// Default FieldsHighlightColor.
        /// TODO - make an option of that
        /// </summary>
        public static readonly RGBColor DefaultFieldsHighlightColor = new RGBColor(204 / 255.0, 215 / 255.0, 1);

        /// <summary>
        /// Default Required FieldsHighlightColor.
        /// TODO - make an option of that
        /// </summary>
        public static readonly RGBColor DefaultRequiredFieldsHighlightColor = new RGBColor(1, 0, 0);

        private static bool IsAnnotationBelowText(Annotation annotation)
        {
            // TODO - Very hackish
            switch (annotation.Type)
            {
                case AnnotationType.Highlight:
                    return true;

                default:
                    return false;
            }
        }

        private static bool ShouldRender(Annotation annotation)
        {
            // cf. ISO 32000-2:2020(E) - Table 167 — Annotation flags
            if (annotation.Flags.HasFlag(AnnotationFlags.Invisible) ||
                annotation.Flags.HasFlag(AnnotationFlags.Hidden) ||
                annotation.Flags.HasFlag(AnnotationFlags.NoView))
            {
                return false;
            }

            return true;
        }

        private readonly Lazy<Annotation[]> _annotations;

        private void DrawAnnotations(bool isBelowText)
        {
            // https://github.com/apache/pdfbox/blob/trunk/pdfbox/src/main/java/org/apache/pdfbox/rendering/PageDrawer.java
            // https://github.com/apache/pdfbox/blob/c4b212ecf42a1c0a55529873b132ea338a8ba901/pdfbox/src/main/java/org/apache/pdfbox/contentstream/PDFStreamEngine.java#L312
            foreach (Annotation annotation in _annotations.Value.Where(a => IsAnnotationBelowText(a) == isBelowText))
            {
                // Check if visible
                if (!ShouldRender(annotation))
                {
                    continue;
                }

                // Get appearance
                StreamToken? appearance = GetNormalAppearanceAsStream(annotation);

                if (appearance is null)
                {
                    // TODO - log
                    continue;
                }

                if (!annotation.AnnotationDictionary.TryGet<ArrayToken>(NameToken.Rect, PdfScanner, out var rectToken))
                {
                    // TODO - log
                    continue; // Should never happen
                }

                // Don't use annotation.Rectangle, we might have updated it in GetNormalAppearanceAsStream()
                var rectPoints = rectToken.Data.OfType<NumericToken>().Select(x => x.Double).ToArray();
                PdfRectangle rect = new PdfRectangle(rectPoints[0], rectPoints[1], rectPoints[2], rectPoints[3]);

                PdfRectangle? bbox = null;
                if (appearance.StreamDictionary.TryGet<ArrayToken>(NameToken.Bbox, PdfScanner, out var bboxToken))
                {
                    var points = bboxToken.Data.OfType<NumericToken>().Select(x => x.Double).ToArray();
                    bbox = new PdfRectangle(points[0], points[1], points[2], points[3]);
                }

                // zero-sized rectangles are not valid
                if (rect.Width > 0 && rect.Height > 0 &&
                    bbox.HasValue && bbox.Value.Width > 0 && bbox.Value.Height > 0)
                {
                    var matrix = TransformationMatrix.Identity;
                    if (appearance.StreamDictionary.TryGet<ArrayToken>(NameToken.Matrix, PdfScanner, out var matrixToken))
                    {
                        matrix = TransformationMatrix.FromArray(matrixToken.Data.OfType<NumericToken>()
                            .Select(x => x.Double).ToArray());
                    }

                    PushState();
                    int gsCount = GraphicsStack.Count;

                    // https://github.com/apache/pdfbox/blob/47867f7eee275e9e54a87222b66ab14a8a3a062a/pdfbox/src/main/java/org/apache/pdfbox/contentstream/PDFStreamEngine.java#L310
                    // transformed appearance box  fixme: may be an arbitrary shape
                    PdfRectangle transformedBox = matrix.Transform(bbox.Value).Normalise();

                    var a = TransformationMatrix.GetTranslationMatrix(rect.TopLeft.X, rect.TopLeft.Y);
                    a = Scale(a, (float)(rect.Width / transformedBox.Width),
                        (float)(rect.Height / transformedBox.Height));
                    a = a.Translate(-transformedBox.TopLeft.X, -transformedBox.TopLeft.Y);

                    var currentState = GetCurrentState();
                    currentState.CurrentTransformationMatrix = a.Multiply(currentState.CurrentTransformationMatrix);

                    try
                    {
                        ProcessFormXObject(appearance, null!);
                    }
                    catch (Exception ex)
                    {
                        // An exception was thrown in ProcessFormXObject, we want to make sure the stack
                        // is the same as before we entered the method
                        while (GraphicsStack.Count > gsCount)
                        {
                            PopState();
                        }
                        System.Diagnostics.Debug.WriteLine($"DrawAnnotations: {ex}");
                    }
                    finally
                    {
                        PopState();
                    }
                }
            }
        }

        private static TransformationMatrix Scale(TransformationMatrix matrix, float sx, float sy)
        {
            var x0 = matrix[0, 0] * sx;
            var x1 = matrix[0, 1] * sx;
            var x2 = matrix[0, 2] * sx;
            var y0 = matrix[1, 0] * sy;
            var y1 = matrix[1, 1] * sy;
            var y2 = matrix[1, 2] * sy;
            return new TransformationMatrix(
                x0, x1, x2,
                y0, y1, y2,
                matrix[2, 0], matrix[2, 1], matrix[2, 2]);
        }

        private StreamToken? GetNormalAppearanceAsStream(Annotation annotation)
        {
            var appearanceDict = GetAppearance(annotation);

            // https://github.com/apache/pdfbox/blob/trunk/pdfbox/src/main/java/org/apache/pdfbox/pdmodel/interactive/form/AppearanceGeneratorHelper.java

            if (appearanceDict is null)
            {
                return GenerateNormalAppearanceAsStream(annotation);
            }

            // get Normal Appearance
            if (!appearanceDict.Data.TryGetValue(NameToken.N, out var data))
            {
                return null;
            }

            if (data is IndirectReferenceToken irt)
            {
                data = PdfScanner.Get(irt.Data);
                if (data is null)
                {
                    return null;
                }
            }

            StreamToken? normalAppearance = null;

            if (data is StreamToken streamToken)
            {
                normalAppearance = streamToken;
            }
            else if (data is DictionaryToken dictionaryToken)
            {
                if (annotation.AnnotationDictionary.TryGet<NameToken>(NameToken.As, PdfScanner, out var appearanceState))
                {
                    if (!dictionaryToken.TryGet(appearanceState, PdfScanner, out normalAppearance))
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"GetNormalAppearanceAsStream: Error could not find token '{appearanceState.Data}' in annotation dictionary or in D dictionary.");
                    }
                }
            }
            else if (data is ObjectToken objectToken)
            {
                if (objectToken.Data is StreamToken streamToken2)
                {
                    normalAppearance = streamToken2;
                }
                else if (objectToken.Data is DictionaryToken dictionaryToken2)
                {
                    if (annotation.AnnotationDictionary.TryGet<NameToken>(NameToken.As, PdfScanner, out var appearanceState))
                    {
                        if (!dictionaryToken2.TryGet(appearanceState, PdfScanner, out normalAppearance))
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"GetNormalAppearanceAsStream: Error could not find token '{appearanceState.Data}' in annotation dictionary or in D dictionary.");
                        }
                    }
                }
            }
            else
            {
                throw new ArgumentException("TODO GetNormalAppearanceAsStream");
            }

            if (normalAppearance is null)
            {
                return null;
            }

            if (annotation.Type == AnnotationType.Widget)
            {
                normalAppearance = SetAppearanceContent(annotation, normalAppearance);
            }

            var dict = normalAppearance.StreamDictionary
                .With(NameToken.Rect, annotation.Rectangle.ToArrayToken());

            return new StreamToken(dict, normalAppearance.Data);
        }

        private bool ShouldPaintWidgetBackground(Annotation widget)
        {
            // This is a guess, some widget annotations do not need background colors to be added

            if (!widget.AnnotationDictionary.TryGet(NameToken.Ft, out NameToken fieldTypeToken) ||
                !fieldTypeToken.Equals(NameToken.Sig))
            {
                return true; // Default
            }

            // We have a Signature dictionary
            // Do not paint background for signature annotation with signature dictionary (V)
            return !widget.AnnotationDictionary.TryGet<DictionaryToken>(NameToken.V, PdfScanner, out _);
        }

        /// <summary>
        /// Constructs and sets new contents for given appearance stream.
        /// </summary>
        private StreamToken SetAppearanceContent(Annotation widget, StreamToken appearanceStream)
        {
            // first copy any needed resources from the document’s DR dictionary into
            // the stream’s Resources dictionary
            //getWidgetDefaultAppearanceString(widget);
            //defaultAppearance.copyNeededResourcesTo(appearanceStream);

            using (var ms = new MemoryStream())
            {
                var contentStream = appearanceStream.Decode(FilterProvider, PdfScanner);
                var tokens = PageContentParser
                    .Parse(PageNumber, new MemoryInputBytes(contentStream), ParsingOptions.Logger).ToList();

                int bmcIndex = tokens.FindIndex(x => x is BeginMarkedContent);

                if (bmcIndex == -1)
                {
                    // append to existing stream
                    foreach (var operation in tokens)
                    {
                        operation.Write(ms);
                    }
                    new BeginMarkedContent(NameToken.Tx).Write(ms);
                }
                else
                {
                    // prepend content before BMC
                    foreach (var operation in tokens.Take(bmcIndex + 1))
                    {
                        operation.Write(ms);
                    }
                }

                // insert field contents
                // TODO - To finish, does not follow PdfBox
                if (ShouldPaintWidgetBackground(widget))
                {
                    var (r, g, b) = DefaultFieldsHighlightColor.ToRGBValues();
                    GetAnnotationNonStrokeColorOperation([r, g, b])?.Write(ms);

                    PdfRectangle bbox = widget.Rectangle;
                    if (widget.AnnotationDictionary.TryGet(NameToken.Rect, PdfScanner, out ArrayToken? rect))
                    {
                        var points = rect.Data.OfType<NumericToken>().Select(x => x.Double).ToArray();
                        bbox = new PdfRectangle(points[0], points[1], points[2], points[3]);
                    }

                    new AppendRectangle(0, 0, bbox.Width, bbox.Height).Write(ms);
                    PdfPig.Graphics.Operations.PathPainting.FillPathEvenOddRule.Value.Write(ms);
                }

                int emcIndex = tokens.FindIndex(x => x is EndMarkedContent);

                if (emcIndex != -1)
                {
                    int count = emcIndex - (bmcIndex + 1);
                    foreach (var operation in tokens.Skip(bmcIndex + 1).Take(count))
                    {
                        operation.Write(ms);
                    }
                }
                else
                {
                    foreach (var operation in tokens.Skip(bmcIndex + 1))
                    {
                        operation.Write(ms);
                    }
                }

                //insertGeneratedAppearance(widget, appearanceStream, ms);
                // TODO - END To finish, does not follow PdfBox

                if (emcIndex == -1)
                {
                    // append EMC
                    Graphics.Operations.MarkedContent.EndMarkedContent.Value.Write(ms);
                }
                else
                {
                    // append contents after EMC
                    foreach (var operation in tokens.Skip(emcIndex))
                    {
                        operation.Write(ms);
                    }
                }

                var newAppearance = new StreamToken(appearanceStream.StreamDictionary, ms.ToArray());

                //var contentStreamTest = newAppearance.Decode(FilterProvider, PdfScanner);
                //var operationsTest = PageContentParser
                //    .Parse(PageNumber, new ByteArrayInputBytes(contentStreamTest), ParsingOptions.Logger).ToList();

                return newAppearance;
            }
        }

        private DictionaryToken? GetAppearance(Annotation annotation)
        {
            return annotation.AnnotationDictionary.TryGet<DictionaryToken>(NameToken.Ap, PdfScanner, out var appearance)
                ? appearance
                : null;
        }

        private StreamToken? GenerateNormalAppearanceAsStream(Annotation annotation)
        {
            // https://github.com/apache/pdfbox/blob/c4b212ecf42a1c0a55529873b132ea338a8ba901/pdfbox/src/main/java/org/apache/pdfbox/pdmodel/interactive/annotation/handlers/PDAbstractAppearanceHandler.java#L479

            return annotation.Type switch
            {
                AnnotationType.StrikeOut => GenerateStrikeOutNormalAppearanceAsStream(annotation),
                AnnotationType.Highlight => GenerateHighlightNormalAppearanceAsStream(annotation),
                AnnotationType.Underline => GenerateUnderlineNormalAppearanceAsStream(annotation),
                AnnotationType.Link => GenerateLinkNormalAppearanceAsStream(annotation),
                AnnotationType.Widget => GenerateWidgetNormalAppearanceAsStream(annotation),
                _ => null
            };
        }

        private StreamToken? GenerateStrikeOutNormalAppearanceAsStream(Annotation annotation)
        {
            // https://github.com/apache/pdfbox/blob/trunk/pdfbox/src/main/java/org/apache/pdfbox/pdmodel/interactive/annotation/handlers/PDStrikeoutAppearanceHandler.java

            PdfRectangle rect = annotation.Rectangle;

            if (!annotation.AnnotationDictionary.TryGet<ArrayToken>(NameToken.Quadpoints, PdfScanner, out var quadPoints))
            {
                return null;
            }

            var pathsArray = quadPoints.Data.OfType<NumericToken>().Select(x => (float)x.Double).ToArray();

            AnnotationBorder ab = annotation.Border;

            if (!annotation.AnnotationDictionary.TryGet(NameToken.C, PdfScanner, out ArrayToken? colorToken) ||
                colorToken.Data.Count == 0)
            {
                return null;
            }

            var color = colorToken.Data.OfType<NumericToken>().Select(x => x.Data).ToArray();

            double width = ab.BorderWidth;
            if (width == 0)
            {
                // value found in adobe reader
                width = 1.5;
            }

            // Adjust rectangle even if not empty, see PLPDF.com-MarkupAnnotations.pdf
            //TODO in a class structure this should be overridable
            // this is similar to polyline but different data type
            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;
            for (int i = 0; i < pathsArray.Length / 2; ++i)
            {
                float x = pathsArray[i * 2];
                float y = pathsArray[i * 2 + 1];
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }

            var setLowerLeftX = Math.Min(minX - (float)width / 2.0, rect.BottomLeft.X);
            var setLowerLeftY = Math.Min(minY - (float)width / 2.0, rect.BottomLeft.Y);
            var setUpperRightX = Math.Max(maxX + (float)width / 2.0, rect.TopRight.X);
            var setUpperRightY = Math.Max(maxY + (float)width / 2.0, rect.TopRight.Y);
            rect = new PdfRectangle(setLowerLeftX, setLowerLeftY, setUpperRightX, setUpperRightY);

            try
            {
                using (var ms = new MemoryStream())
                {
                    //setOpacity(cs, annotation.getConstantOpacity()); // TODO

                    GetAnnotationStrokeColorOperation(color)?.Write(ms);

                    //if (ab.dashArray is not null)
                    //{
                    //    cs.setLineDashPattern(ab.dashArray, 0);
                    //}                   

                    new Graphics.Operations.General.SetLineWidth(width).Write(ms);

                    // spec is incorrect
                    // https://stackoverflow.com/questions/9855814/pdf-spec-vs-acrobat-creation-quadpoints
                    for (int i = 0; i < pathsArray.Length / 8; ++i)
                    {
                        // get mid point between bounds, subtract the line width to approximate what Adobe is doing
                        // See e.g. CTAN-example-Annotations.pdf and PLPDF.com-MarkupAnnotations.pdf
                        // and https://bugs.ghostscript.com/show_bug.cgi?id=693664
                        // do the math for diagonal annotations with this weird old trick:
                        // https://stackoverflow.com/questions/7740507/extend-a-line-segment-a-specific-distance
                        float len0 = (float)Math.Sqrt(Math.Pow(pathsArray[i * 8] - pathsArray[i * 8 + 4], 2) +
                                                      Math.Pow(pathsArray[i * 8 + 1] - pathsArray[i * 8 + 5], 2));
                        float x0 = pathsArray[i * 8 + 4];
                        float y0 = pathsArray[i * 8 + 5];
                        if (len0 != 0)
                        {
                            // only if both coordinates are not identical to avoid divide by zero
                            x0 += (pathsArray[i * 8] - pathsArray[i * 8 + 4]) / len0 *
                                  (len0 / 2 - (float)ab.BorderWidth);
                            y0 += (pathsArray[i * 8 + 1] - pathsArray[i * 8 + 5]) / len0 *
                                  (len0 / 2 - (float)ab.BorderWidth);
                        }

                        float len1 = (float)Math.Sqrt(Math.Pow(pathsArray[i * 8 + 2] - pathsArray[i * 8 + 6], 2) +
                                                      Math.Pow(pathsArray[i * 8 + 3] - pathsArray[i * 8 + 7], 2));
                        float x1 = pathsArray[i * 8 + 6];
                        float y1 = pathsArray[i * 8 + 7];
                        if (len1 != 0)
                        {
                            // only if both coordinates are not identical to avoid divide by zero
                            x1 += (pathsArray[i * 8 + 2] - pathsArray[i * 8 + 6]) / len1 *
                                  (len1 / 2 - (float)ab.BorderWidth);
                            y1 += (pathsArray[i * 8 + 3] - pathsArray[i * 8 + 7]) / len1 *
                                  (len1 / 2 - (float)ab.BorderWidth);
                        }

                        new BeginNewSubpath(x0, y0).Write(ms);
                        new AppendStraightLineSegment(x1, y1).Write(ms);
                    }

                    Graphics.Operations.PathPainting.StrokePath.Value.Write(ms);

                    var dict = annotation.AnnotationDictionary
                        .With(NameToken.Rect, rect.ToArrayToken());
                    dict = setTransformationMatrix(dict, annotation.Rectangle);

                    return new StreamToken(dict, ms.ToArray());
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                // TODO - Log
                return null;
            }
        }

        private StreamToken? GenerateHighlightNormalAppearanceAsStream(Annotation annotation)
        {
            // https://github.com/apache/pdfbox/blob/trunk/pdfbox/src/main/java/org/apache/pdfbox/pdmodel/interactive/annotation/handlers/PDHighlightAppearanceHandler.java
            PdfRectangle rect = annotation.Rectangle;

            if (!annotation.AnnotationDictionary.TryGet<ArrayToken>(NameToken.Quadpoints, PdfScanner, out var quadPoints))
            {
                return null;
            }

            var pathsArray = quadPoints.Data.OfType<NumericToken>().Select(x => (float)x.Double).ToArray();

            var ab = annotation.Border;

            if (!annotation.AnnotationDictionary.TryGet<ArrayToken>(NameToken.C, PdfScanner, out var colorToken) ||
                colorToken.Data.Count == 0)
            {
                return null;
            }

            var color = colorToken.Data.OfType<NumericToken>().Select(x => x.Data).ToArray();

            double width = ab.BorderWidth;

            // Adjust rectangle even if not empty, see PLPDF.com-MarkupAnnotations.pdf
            //TODO in a class structure this should be overridable
            // this is similar to polyline but different data type
            //TODO padding should consider the curves too; needs to know in advance where the curve is
            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;
            for (int i = 0; i < pathsArray.Length / 2; ++i)
            {
                float x = pathsArray[i * 2];
                float y = pathsArray[i * 2 + 1];
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }

            // get the delta used for curves and use it for padding
            float maxDelta = 0;
            for (int i = 0; i < pathsArray.Length / 8; ++i)
            {
                // one of the two is 0, depending whether the rectangle is 
                // horizontal or vertical
                // if it is diagonal then... uh...
                float delta = Math.Max((pathsArray[i + 0] - pathsArray[i + 4]) / 4,
                    (pathsArray[i + 1] - pathsArray[i + 5]) / 4);
                maxDelta = Math.Max(delta, maxDelta);
            }

            var setLowerLeftX = Math.Min(minX - (float)width / 2.0, rect.BottomLeft.X);
            var setLowerLeftY = Math.Min(minY - (float)width / 2.0, rect.BottomLeft.Y);
            var setUpperRightX = Math.Max(maxX + (float)width / 2.0, rect.TopRight.X);
            var setUpperRightY = Math.Max(maxY + (float)width / 2.0, rect.TopRight.Y);
            rect = new PdfRectangle(setLowerLeftX, setLowerLeftY, setUpperRightX, setUpperRightY);

            try
            {
                using (var ms = new MemoryStream())
                {
                    /*
                    PDExtendedGraphicsState r0 = new PDExtendedGraphicsState();
                    PDExtendedGraphicsState r1 = new PDExtendedGraphicsState();
                    r0.setAlphaSourceFlag(false);
                    r0.setStrokingAlphaConstant(annotation.getConstantOpacity());
                    r0.setNonStrokingAlphaConstant(annotation.getConstantOpacity());
                    r1.setAlphaSourceFlag(false);
                    r1.setBlendMode(BlendMode.MULTIPLY);
                    cs.setGraphicsStateParameters(r0);
                    cs.setGraphicsStateParameters(r1);
                    PDFormXObject frm1 = new PDFormXObject(createCOSStream());
                    PDFormXObject frm2 = new PDFormXObject(createCOSStream());
                    frm1.setResources(new PDResources());
                    try (PDFormContentStream mwfofrmCS = new PDFormContentStream(frm1))
                    {
                        mwfofrmCS.drawForm(frm2);
                    }
                    frm1.setBBox(annotation.getRectangle());
                    COSDictionary groupDict = new COSDictionary();
                    groupDict.setItem(COSName.S, COSName.TRANSPARENCY);
                    //TODO PDFormXObject.setGroup() is missing
                    frm1.getCOSObject().setItem(COSName.GROUP, groupDict);
                    cs.drawForm(frm1);
                    frm2.setBBox(annotation.getRectangle());
                    */

                    GetAnnotationNonStrokeColorOperation(color)?.Write(ms);

                    int of = 0;
                    while (of + 7 < pathsArray.Length)
                    {
                        // quadpoints spec sequence is incorrect, correct one is (4,5 0,1 2,3 6,7)
                        // https://stackoverflow.com/questions/9855814/pdf-spec-vs-acrobat-creation-quadpoints

                        // for "curvy" highlighting, two Bézier control points are used that seem to have a
                        // distance of about 1/4 of the height.
                        // note that curves won't appear if outside of the rectangle
                        float delta = 0;
                        if (pathsArray[of + 0] == pathsArray[of + 4] &&
                            pathsArray[of + 1] == pathsArray[of + 3] &&
                            pathsArray[of + 2] == pathsArray[of + 6] &&
                            pathsArray[of + 5] == pathsArray[of + 7])
                        {
                            // horizontal highlight
                            delta = (pathsArray[of + 1] - pathsArray[of + 5]) / 4;
                        }
                        else if (pathsArray[of + 1] == pathsArray[of + 5] &&
                                 pathsArray[of + 0] == pathsArray[of + 2] &&
                                 pathsArray[of + 3] == pathsArray[of + 7] &&
                                 pathsArray[of + 4] == pathsArray[of + 6])
                        {
                            // vertical highlight
                            delta = (pathsArray[of + 0] - pathsArray[of + 4]) / 4;
                        }

                        new BeginNewSubpath(pathsArray[of + 4], pathsArray[of + 5]).Write(ms);

                        if (pathsArray[of + 0] == pathsArray[of + 4])
                        {
                            // horizontal highlight
                            new AppendDualControlPointBezierCurve(
                                    (pathsArray[of + 4] - delta), (pathsArray[of + 5] + delta),
                                    (pathsArray[of + 0] - delta), (pathsArray[of + 1] - delta),
                                    pathsArray[of + 0], pathsArray[of + 1])
                                .Write(ms);
                        }
                        else if (pathsArray[of + 5] == pathsArray[of + 1])
                        {
                            // vertical highlight
                            new AppendDualControlPointBezierCurve(
                                    (pathsArray[of + 4] + delta), (pathsArray[of + 5] + delta),
                                    (pathsArray[of + 0] - delta), (pathsArray[of + 1] + delta),
                                    pathsArray[of + 0], pathsArray[of + 1])
                                .Write(ms);
                        }
                        else
                        {
                            new AppendStraightLineSegment(pathsArray[of + 0], pathsArray[of + 1])
                                .Write(ms);
                        }

                        new AppendStraightLineSegment(pathsArray[of + 2], pathsArray[of + 3]).Write(ms);

                        if (pathsArray[of + 2] == pathsArray[of + 6])
                        {
                            // horizontal highlight
                            new AppendDualControlPointBezierCurve(
                                    (pathsArray[of + 2] + delta), (pathsArray[of + 3] - delta),
                                    (pathsArray[of + 6] + delta), (pathsArray[of + 7] + delta),
                                    pathsArray[of + 6], pathsArray[of + 7])
                                .Write(ms);
                        }
                        else if (pathsArray[of + 3] == pathsArray[of + 7])
                        {
                            // vertical highlight
                            new AppendDualControlPointBezierCurve(
                                    (pathsArray[of + 2] - delta), (pathsArray[of + 3] - delta),
                                    (pathsArray[of + 6] + delta), (pathsArray[of + 7] - delta),
                                    pathsArray[of + 6], pathsArray[of + 7])
                                .Write(ms);
                        }
                        else
                        {
                            new AppendStraightLineSegment(pathsArray[of + 6], pathsArray[of + 7])
                                .Write(ms);
                        }

                        Graphics.Operations.PathPainting.FillPathEvenOddRule.Value.Write(ms);
                        of += 8;
                    }

                    var dict = annotation.AnnotationDictionary
                        .With(NameToken.Rect, rect.ToArrayToken());
                    dict = setTransformationMatrix(dict, annotation.Rectangle);

                    return new StreamToken(dict, ms.ToArray());
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                // TODO - Log
            }

            return null;
        }

        private StreamToken? GenerateUnderlineNormalAppearanceAsStream(Annotation annotation)
        {
            // https://github.com/apache/pdfbox/blob/trunk/pdfbox/src/main/java/org/apache/pdfbox/pdmodel/interactive/annotation/handlers/PDStrikeoutAppearanceHandler.java

            PdfRectangle rect = annotation.Rectangle;

            if (!annotation.AnnotationDictionary.TryGet<ArrayToken>(NameToken.Quadpoints, PdfScanner, out var quadPoints))
            {
                return null;
            }

            var pathsArray = quadPoints.Data.OfType<NumericToken>().Select(x => (float)x.Double).ToArray();

            var ab = annotation.Border;

            if (!annotation.AnnotationDictionary.TryGet<ArrayToken>(NameToken.C, PdfScanner, out var colorToken) ||
                colorToken.Data.Count == 0)
            {
                return null;
            }

            var color = colorToken.Data.OfType<NumericToken>().Select(x => x.Data).ToArray();

            double width = ab.BorderWidth;
            if (width == 0)
            {
                // value found in adobe reader
                width = 1.5;
            }

            // Adjust rectangle even if not empty, see PLPDF.com-MarkupAnnotations.pdf
            //TODO in a class structure this should be overridable
            // this is similar to polyline but different data type
            // all coordinates (unlike painting) are used because I'm lazy
            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;
            for (int i = 0; i < pathsArray.Length / 2; ++i)
            {
                float x = pathsArray[i * 2];
                float y = pathsArray[i * 2 + 1];
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }

            var setLowerLeftX = Math.Min(minX - (float)width / 2.0, rect.BottomLeft.X);
            var setLowerLeftY = Math.Min(minY - (float)width / 2.0, rect.BottomLeft.Y);
            var setUpperRightX = Math.Max(maxX + (float)width / 2.0, rect.TopRight.X);
            var setUpperRightY = Math.Max(maxY + (float)width / 2.0, rect.TopRight.Y);
            rect = new PdfRectangle(setLowerLeftX, setLowerLeftY, setUpperRightX, setUpperRightY);

            try
            {
                using (var ms = new MemoryStream())
                {
                    //setOpacity(cs, annotation.getConstantOpacity()); // TODO

                    GetAnnotationStrokeColorOperation(color)?.Write(ms);

                    //if (ab.dashArray is not null)
                    //{
                    //    cs.setLineDashPattern(ab.dashArray, 0);
                    //}                   

                    new Graphics.Operations.General.SetLineWidth(width).Write(ms);

                    // spec is incorrect
                    // https://stackoverflow.com/questions/9855814/pdf-spec-vs-acrobat-creation-quadpoints
                    for (int i = 0; i < pathsArray.Length / 8; ++i)
                    {
                        // Adobe doesn't use the lower coordinate for the line, it uses lower + delta / 7.
                        // do the math for diagonal annotations with this weird old trick:
                        // https://stackoverflow.com/questions/7740507/extend-a-line-segment-a-specific-distance
                        float len0 = (float)Math.Sqrt(Math.Pow(pathsArray[i * 8] - pathsArray[i * 8 + 4], 2) +
                                                      Math.Pow(pathsArray[i * 8 + 1] - pathsArray[i * 8 + 5], 2));
                        float x0 = pathsArray[i * 8 + 4];
                        float y0 = pathsArray[i * 8 + 5];
                        if (len0 != 0)
                        {
                            // only if both coordinates are not identical to avoid divide by zero
                            x0 += (pathsArray[i * 8] - pathsArray[i * 8 + 4]) / len0 * len0 / 7;
                            y0 += (pathsArray[i * 8 + 1] - pathsArray[i * 8 + 5]) / len0 * (len0 / 7);
                        }

                        float len1 = (float)Math.Sqrt(Math.Pow(pathsArray[i * 8 + 2] - pathsArray[i * 8 + 6], 2) +
                                                      Math.Pow(pathsArray[i * 8 + 3] - pathsArray[i * 8 + 7], 2));
                        float x1 = pathsArray[i * 8 + 6];
                        float y1 = pathsArray[i * 8 + 7];
                        if (len1 != 0)
                        {
                            // only if both coordinates are not identical to avoid divide by zero
                            x1 += (pathsArray[i * 8 + 2] - pathsArray[i * 8 + 6]) / len1 * len1 / 7;
                            y1 += (pathsArray[i * 8 + 3] - pathsArray[i * 8 + 7]) / len1 * len1 / 7;
                        }

                        new BeginNewSubpath(x0, y0).Write(ms);
                        new AppendStraightLineSegment(x1, y1).Write(ms);
                    }

                    Graphics.Operations.PathPainting.StrokePath.Value.Write(ms);

                    var dict = annotation.AnnotationDictionary
                        .With(NameToken.Rect, rect.ToArrayToken());
                    dict = setTransformationMatrix(dict, annotation.Rectangle);

                    return new StreamToken(dict, ms.ToArray());
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                // TODO - Log
            }

            return null;
        }

        private StreamToken? GenerateLinkNormalAppearanceAsStream(Annotation annotation)
        {
            // https://github.com/apache/pdfbox/blob/trunk/pdfbox/src/main/java/org/apache/pdfbox/pdmodel/interactive/annotation/handlers/PDLinkAppearanceHandler.java

            PdfRectangle rect = annotation.Rectangle;

            var ab = annotation.Border;
            try
            {
                using (var ms = new MemoryStream())
                {
                    double[]? color = null;
                    if (annotation.AnnotationDictionary.TryGet<ArrayToken>(NameToken.C, PdfScanner, out var colorToken) &&
                        colorToken.Data.Count > 0)
                    {
                        color = colorToken.Data.OfType<NumericToken>().Select(x => x.Data).ToArray();
                    }
                    else
                    {
                        // spec is unclear, but black is what Adobe does
                        //color = new decimal[] { 0 }; // DeviceGray black (from Pdfbox)
                        color = [0]; // Empty array, transparent
                    }

                    double lineWidth = ab.BorderWidth;

                    // Acrobat applies a padding to each side of the bbox so the line is completely within
                    // the bbox.
                    PdfRectangle borderEdge = GetPaddedRectangle(rect, (float)(lineWidth / 2.0));

                    float[]? pathsArray = null;
                    if (annotation.AnnotationDictionary.TryGet<ArrayToken>(NameToken.Quadpoints, PdfScanner, out var quadPoints))
                    {
                        pathsArray = quadPoints?.Data?.OfType<NumericToken>().Select(x => (float)x.Double)?.ToArray();
                    }

                    if (pathsArray is not null)
                    {
                        // QuadPoints shall be ignored if any coordinate in the array lies outside
                        // the region specified by Rect.
                        for (int i = 0; i < pathsArray.Length / 2; ++i)
                        {
                            if (!rect.Contains(new PdfPoint(pathsArray[i * 2], pathsArray[i * 2 + 1])))
                            {
                                System.Diagnostics.Debug.WriteLine("At least one /QuadPoints entry (" +
                                                                   pathsArray[i * 2] + ";" + pathsArray[i * 2 + 1] +
                                                                   ") is outside of rectangle, " + rect +
                                                                   ", /QuadPoints are ignored and /Rect is used instead");
                                pathsArray = null;
                                break;
                            }
                        }
                    }

                    if (pathsArray is null)
                    {
                        // Convert rectangle coordinates as if it was a /QuadPoints entry
                        pathsArray = new float[8];
                        pathsArray[0] = (float)borderEdge.BottomLeft.X;
                        pathsArray[1] = (float)borderEdge.BottomLeft.Y;
                        pathsArray[2] = (float)borderEdge.TopRight.X;
                        pathsArray[3] = (float)borderEdge.BottomLeft.Y;
                        pathsArray[4] = (float)borderEdge.TopRight.X;
                        pathsArray[5] = (float)borderEdge.TopRight.Y;
                        pathsArray[6] = (float)borderEdge.BottomLeft.X;
                        pathsArray[7] = (float)borderEdge.TopRight.Y;
                    }

                    bool underlined = false;
                    if (pathsArray.Length >= 8)
                    {
                        // Get border style
                        if (annotation.AnnotationDictionary.TryGet<DictionaryToken>(NameToken.Bs, PdfScanner, out var borderStyleToken))
                        {
                            // (PDF 1.2) The dictionaries for some annotation types (such as free text and
                            // polygon annotations) can include the BS entry. That entry specifies a border
                            // style dictionary that has more settings than the array specified for the Border
                            // entry. If an annotation dictionary includes the BS entry, then the Border entry
                            // is ignored.

                            if (borderStyleToken.TryGet<NameToken>(NameToken.S, PdfScanner, out var styleToken))
                            {
                                underlined = styleToken.Data.Equals("U");
                                // (Optional) The border style:
                                // S   (Solid) A solid rectangle surrounding the annotation.
                                // D   (Dashed) A dashed rectangle surrounding the annotation. The dash pattern may be specified by the D entry.
                                // B   (Beveled) A simulated embossed rectangle that appears to be raised above the surface of the page.
                                // I   (Inset) A simulated engraved rectangle that appears to be recessed below the surface of the page.
                                // U   (Underline) A single line along the bottom of the annotation rectangle.
                                // A conforming reader shall tolerate other border styles that it does not recognize and shall use the default value.
                            }

                            if (borderStyleToken.TryGet<NumericToken>(NameToken.W, PdfScanner, out var borderWidthToken))
                            {
                                // (Optional) The border width in points. If this value is 0, no border shall be
                                // drawn. Default value: 1.
                                lineWidth = borderWidthToken.Data;
                            }
                            else
                            {
                                lineWidth = 1;
                            }
                        }
                    }

                    if (lineWidth > 0 && color.Length > 0) // TODO - TO CHECK
                    {
                        GetAnnotationStrokeColorOperation(color)?.Write(ms);

                        new Graphics.Operations.General.SetLineWidth(lineWidth).Write(ms);

                        int of = 0;
                        while (of + 7 < pathsArray.Length)
                        {
                            new BeginNewSubpath(pathsArray[of], pathsArray[of + 1]).Write(ms);
                            new AppendStraightLineSegment(pathsArray[of + 2], pathsArray[of + 3]).Write(ms);
                            if (!underlined)
                            {
                                new AppendStraightLineSegment(pathsArray[of + 4], pathsArray[of + 5]).Write(ms);
                                new AppendStraightLineSegment(pathsArray[of + 6], pathsArray[of + 7]).Write(ms);
                                Graphics.Operations.PathConstruction.CloseSubpath.Value.Write(ms);
                            }

                            of += 8;
                        }

                        Graphics.Operations.PathPainting.StrokePath.Value.Write(ms);
                    }
                    //contentStream.drawShape(lineWidth, hasStroke, false);

                    var dict = annotation.AnnotationDictionary
                        .With(NameToken.Rect, rect.ToArrayToken());
                    dict = setTransformationMatrix(dict, annotation.Rectangle);

                    return new StreamToken(dict, ms.ToArray());
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                // TODO - Log
                return null;
            }
        }

        private StreamToken GenerateWidgetNormalAppearanceAsStream(Annotation annotation)
        {
            // This will create an appearance with the default background color from Acrobat reader
            PdfRectangle rect = annotation.Rectangle;
            var ab = annotation.Border;

            using (var ms = new MemoryStream())
            {
                double lineWidth = ab.BorderWidth;

                // TODO - handle no background color filling required, see `SetAppearanceContent(Annotation widget, StreamToken appearanceStream)`
                var (r, g, b) = DefaultFieldsHighlightColor.ToRGBValues();
                GetAnnotationNonStrokeColorOperation([r, g, b])?.Write(ms);

                float[]? pathsArray = null;
                if (annotation.AnnotationDictionary.TryGet<ArrayToken>(NameToken.Quadpoints, PdfScanner, out var quadPoints))
                {
                    pathsArray = quadPoints.Data?.OfType<NumericToken>().Select(x => (float)x.Double)?.ToArray();
                }

                if (pathsArray is not null)
                {
                    // QuadPoints shall be ignored if any coordinate in the array lies outside
                    // the region specified by Rect.
                    for (int i = 0; i < pathsArray.Length / 2; ++i)
                    {
                        if (!rect.Contains(new PdfPoint(pathsArray[i * 2], pathsArray[i * 2 + 1])))
                        {
                            System.Diagnostics.Debug.WriteLine("At least one /QuadPoints entry (" +
                                                               pathsArray[i * 2] + ";" + pathsArray[i * 2 + 1] +
                                                               ") is outside of rectangle, " + rect +
                                                               ", /QuadPoints are ignored and /Rect is used instead");
                            pathsArray = null;
                            break;
                        }
                    }
                }

                if (pathsArray is null)
                {
                    // Convert rectangle coordinates as if it was a /QuadPoints entry
                    pathsArray = new float[8];
                    pathsArray[0] = (float)rect.BottomLeft.X;
                    pathsArray[1] = (float)rect.BottomLeft.Y;
                    pathsArray[2] = (float)rect.TopRight.X;
                    pathsArray[3] = (float)rect.BottomLeft.Y;
                    pathsArray[4] = (float)rect.TopRight.X;
                    pathsArray[5] = (float)rect.TopRight.Y;
                    pathsArray[6] = (float)rect.BottomLeft.X;
                    pathsArray[7] = (float)rect.TopRight.Y;
                }

                int of = 0;
                while (of + 7 < pathsArray.Length)
                {
                    new BeginNewSubpath(pathsArray[of], pathsArray[of + 1]).Write(ms);
                    new AppendStraightLineSegment(pathsArray[of + 2], pathsArray[of + 3]).Write(ms);

                    new AppendStraightLineSegment(pathsArray[of + 4], pathsArray[of + 5]).Write(ms);
                    new AppendStraightLineSegment(pathsArray[of + 6], pathsArray[of + 7]).Write(ms);
                    Graphics.Operations.PathConstruction.CloseSubpath.Value.Write(ms);
                    of += 8;
                }

                Graphics.Operations.PathPainting.FillPathEvenOddRule.Value.Write(ms);

                var dict = annotation.AnnotationDictionary
                    .With(NameToken.Rect, rect.ToArrayToken());
                dict = setTransformationMatrix(dict, annotation.Rectangle);

                return new StreamToken(dict, ms.ToArray());
            }
        }

        private static DictionaryToken setTransformationMatrix(DictionaryToken annotationDictionary, PdfRectangle bbox)
        {
            // https://github.com/apache/pdfbox/blob/c4b212ecf42a1c0a55529873b132ea338a8ba901/pdfbox/src/main/java/org/apache/pdfbox/pdmodel/interactive/annotation/handlers/PDAbstractAppearanceHandler.java#L511
            return annotationDictionary
                .With(NameToken.Bbox, bbox.ToArrayToken())
                .With(NameToken.Matrix, TransformationMatrix.Identity.ToArrayToken()) // annotation.Rectangle is already transformed
                .Without(NameToken.F); // Need to remove the F entry as it will be treated as a Filter entry in ProcessFormXObject()
        }

        private static IGraphicsStateOperation? GetAnnotationStrokeColorOperation(ReadOnlySpan<double> color)
        {
            // An array of numbers in the range 0.0 to 1.0, representing a colour used for the following purposes:
            // The background of the annotation’s icon when closed
            // The title bar of the annotation’s pop - up window
            // The border of a link annotation
            // The number of array elements determines the colour space in which the colour shall be defined:
            // 0    No colour; transparent
            // 1    DeviceGray
            // 3    DeviceRGB
            // 4    DeviceCMYK
            switch (color.Length)
            {
                case 0:
                    return null;
                case 1:
                    return new SetStrokeColorDeviceGray(color[0]);
                case 3:
                    return new SetStrokeColorDeviceRgb(color[0], color[1], color[2]);
                case 4:
                    return new SetStrokeColorDeviceCmyk(color[0], color[1], color[2], color[3]);
                default:
                    throw new ArgumentException(
                        $"Invalid number of colors in annotation, expecting 0, 1, 3 or 4 but got {color.Length}.",
                        nameof(color));
            }
        }

        private static IGraphicsStateOperation? GetAnnotationNonStrokeColorOperation(ReadOnlySpan<double> color)
        {
            // An array of numbers in the range 0.0 to 1.0, representing a colour used for the following purposes:
            // The background of the annotation’s icon when closed
            // The title bar of the annotation’s pop - up window
            // The border of a link annotation
            // The number of array elements determines the colour space in which the colour shall be defined:
            // 0    No colour; transparent
            // 1    DeviceGray
            // 3    DeviceRGB
            // 4    DeviceCMYK
            switch (color.Length)
            {
                case 0:
                    return null;
                case 1:
                    return new SetNonStrokeColorDeviceGray(color[0]);
                case 3:
                    return new SetNonStrokeColorDeviceRgb(color[0], color[1], color[2]);
                case 4:
                    return new SetNonStrokeColorDeviceCmyk(color[0], color[1], color[2], color[3]);
                default:
                    throw new ArgumentException(
                        $"Invalid number of colors in annotation, expecting 0, 1, 3 or 4 but got {color.Length}.",
                        nameof(color));
            }
        }

        private static PdfRectangle GetPaddedRectangle(PdfRectangle rectangle, float padding)
        {
            return new PdfRectangle(
                rectangle.BottomLeft.X + padding,
                rectangle.BottomLeft.Y + padding,
                rectangle.BottomLeft.X + (rectangle.Width - 2 * padding),
                rectangle.BottomLeft.Y + (rectangle.Height - 2 * padding));
        }
    }
}
