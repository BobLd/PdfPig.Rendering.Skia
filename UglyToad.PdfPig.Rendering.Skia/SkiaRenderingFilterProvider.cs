﻿// Copyright 2024 BobLd
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
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Filters.Dct.JpegLibrary;
using UglyToad.PdfPig.Filters.Jbig2.PdfboxJbig2;
using UglyToad.PdfPig.Filters.Jpx.OpenJpeg;
using UglyToad.PdfPig.Tokens;

namespace UglyToad.PdfPig.Rendering.Skia
{
    /// <summary>
    /// Skia rendering filter provider to add support for JBIG2 and DCT filters.
    /// </summary>
    public sealed class SkiaRenderingFilterProvider : BaseFilterProvider
    {
        /// <summary>
        /// The single instance of this provider.
        /// </summary>
        public static readonly SkiaRenderingFilterProvider Instance = new SkiaRenderingFilterProvider();

        /// <inheritdoc/>
        private SkiaRenderingFilterProvider() : base(GetDictionary())
        {
        }

        private static Dictionary<string, IFilter> GetDictionary()
        {
            // New filters
            var dct = new JpegLibraryDctDecodeFilter();
            var jbig2 = new PdfboxJbig2DecodeFilter();
            var jpx = new OpenJpegJpxDecodeFilter();

            // Standard filters
            var ascii85 = new Ascii85Filter();
            var asciiHex = new AsciiHexDecodeFilter();
            var ccitt = new CcittFaxDecodeFilter();
            var flate = new FlateFilter();
            var runLength = new RunLengthFilter();
            var lzw = new LzwFilter();

            return new Dictionary<string, IFilter>
            {
                { NameToken.Ascii85Decode.Data, ascii85 },
                { NameToken.Ascii85DecodeAbbreviation.Data, ascii85 },
                { NameToken.AsciiHexDecode.Data, asciiHex },
                { NameToken.AsciiHexDecodeAbbreviation.Data, asciiHex },
                { NameToken.CcittfaxDecode.Data, ccitt },
                { NameToken.CcittfaxDecodeAbbreviation.Data, ccitt },
                { NameToken.DctDecode.Data, dct },
                { NameToken.DctDecodeAbbreviation.Data, dct },
                { NameToken.FlateDecode.Data, flate },
                { NameToken.FlateDecodeAbbreviation.Data, flate },
                { NameToken.Jbig2Decode.Data, jbig2 },
                { NameToken.JpxDecode.Data, jpx },
                { NameToken.RunLengthDecode.Data, runLength },
                { NameToken.RunLengthDecodeAbbreviation.Data, runLength },
                { NameToken.LzwDecode.Data, lzw },
                { NameToken.LzwDecodeAbbreviation.Data, lzw }
            };
        }
    }
}
