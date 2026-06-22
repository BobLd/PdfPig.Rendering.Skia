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
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Geometry;
using UglyToad.PdfPig.Outline.Destinations;
using UglyToad.PdfPig.Parser;
using UglyToad.PdfPig.Parser.Parts;
using UglyToad.PdfPig.Tokenization.Scanner;
using UglyToad.PdfPig.Tokens;
using UglyToad.PdfPig.Util;

namespace UglyToad.PdfPig.Rendering.Skia;

public sealed class PageSizeFactory : IPageFactory<PdfPageSize>
{
    /// <summary>
    /// The parsing options.
    /// </summary>
    private readonly ParsingOptions _parsingOptions;

    /// <summary>
    /// The Pdf token scanner.
    /// </summary>
    private readonly IPdfTokenScanner _pdfScanner;

    /// <summary>
    /// Create a <see cref="BasePageFactory{TPage}"/>.
    /// </summary>
    public PageSizeFactory(
        IPdfTokenScanner pdfScanner,
        IResourceStore resourceStore,
        ILookupFilterProvider filterProvider,
        IPageContentParser pageContentParser,
        ParsingOptions parsingOptions)
    {
        _pdfScanner = pdfScanner;
        _parsingOptions = parsingOptions;
    }

    /// <inheritdoc/>
    public PdfPageSize Create(int number, DictionaryToken dictionary, PageTreeMembers pageTreeMembers, NamedDestinations namedDestinations)
    {
        if (dictionary is null)
        {
            throw new ArgumentNullException(nameof(dictionary));
        }

        NameToken? type = dictionary.GetNameOrDefault(NameToken.Type);

        if (type is not null && !type.Equals(NameToken.Page))
        {
            _parsingOptions.Logger.Error($"Page {number} had its type specified as {type} rather than 'Page'.");
        }

        var rotation = new PageRotationDegrees(pageTreeMembers.Rotation);
        if (dictionary.TryGet(NameToken.Rotate, _pdfScanner, out NumericToken? rotateToken))
        {
            rotation = new PageRotationDegrees(rotateToken.Int);
        }
        
        CropBox cropBox = GetCropBox(dictionary, pageTreeMembers,
            GetMediaBox(number, dictionary, pageTreeMembers));
        
        var viewBox = cropBox.GetVisibleBounds(rotation);
        return new PdfPageSize(number, viewBox.Width, viewBox.Height);
    }

    /// <summary>
    /// Get the crop box.
    /// </summary>
    private CropBox GetCropBox(DictionaryToken dictionary, PageTreeMembers pageTreeMembers, MediaBox mediaBox)
    {
        CropBox cropBox;
        if (dictionary.TryGet(NameToken.CropBox, out var cropBoxObject) &&
            DirectObjectFinder.TryGet(cropBoxObject, _pdfScanner, out ArrayToken? cropBoxArray))
        {
            if (cropBoxArray.Length != 4)
            {
                _parsingOptions.Logger.Error(
                    $"The CropBox was the wrong length in the dictionary: {dictionary}. Array was: {cropBoxArray}. Using MediaBox.");

                return new CropBox(mediaBox.Bounds);
            }

            cropBox = new CropBox(cropBoxArray.ToRectangle(_pdfScanner));
        }
        else
        {
            cropBox = new CropBox(mediaBox.Bounds);
        }

        // PDF 2.0 (ISO 32000-2:2020), 14.11.2 "Page boundaries": if the bounds of the crop box
        // extend outside of the bounds of the media box, a processor shall treat the crop box as
        // its intersection with the media box. When the two do not intersect at all (malformed
        // input), fall back to the declared crop box.
        var intersection = mediaBox.Bounds.Intersect(cropBox.Bounds);
        if (intersection.HasValue)
        {
            cropBox = new CropBox(intersection.Value);
        }

        return cropBox;
    }

    /// <summary>
    /// Get the media box.
    /// </summary>
    private MediaBox GetMediaBox(int number, DictionaryToken dictionary, PageTreeMembers pageTreeMembers)
    {
        MediaBox mediaBox;
        if (dictionary.TryGet(NameToken.MediaBox, out var mediaBoxObject)
            && DirectObjectFinder.TryGet(mediaBoxObject, _pdfScanner, out ArrayToken? mediaBoxArray))
        {
            if (mediaBoxArray.Length != 4)
            {
                _parsingOptions.Logger.Error(
                    $"The MediaBox was the wrong length in the dictionary: {dictionary}. Array was: {mediaBoxArray}. Defaulting to US Letter.");

                mediaBox = MediaBox.Letter;

                return mediaBox;
            }

            mediaBox = new MediaBox(mediaBoxArray.ToRectangle(_pdfScanner));
        }
        else
        {
            mediaBox = pageTreeMembers.MediaBox;

            if (mediaBox is null)
            {
                _parsingOptions.Logger.Error(
                    $"The MediaBox was the wrong missing for page {number}. Using US Letter.");

                // PDFBox defaults to US Letter.
                mediaBox = MediaBox.Letter;
            }
        }

        return mediaBox;
    }
}
