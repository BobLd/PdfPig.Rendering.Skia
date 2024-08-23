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

using System.IO;
using Xunit;

namespace UglyToad.PdfPig.Rendering.Skia.Tests
{
    public class TestRendering
    {
        public static readonly object[][] documents = new object[][]
        {
            new object[]
            {
                "Apitron.PDF.Kit.Samples_patternFill-mupdf-1.png",
                "Apitron.PDF.Kit.Samples_patternFill.pdf", 1, 3
            },
            new object[]
            {
                "Apitron.PDF.Kit.Samples_patternFill-pdfium-1.png",
                "Apitron.PDF.Kit.Samples_patternFill.pdf", 1, 3
            },

            new object[]
            {
                "cat-genetics-mupdf-1.png",
                "cat-genetics.pdf", 1, 3
            },
            new object[]
            {
                "cat-genetics-pdfium-1.png",
                "cat-genetics.pdf", 1, 3
            },

            new object[]
            {
                "cat-genetics_bobld-mupdf-1.png",
                "cat-genetics_bobld.pdf", 1, 3
            },
            new object[]
            {
                "cat-genetics_bobld-pdfium-1.png",
                "cat-genetics_bobld.pdf", 1, 3
            },

            new object[]
            {
                "GHOSTSCRIPT-699554-0.zip-4-mupdf-1.png",
                "GHOSTSCRIPT-699554-0.zip-4.pdf", 1, 3
            },
            new object[]
            {
                "GHOSTSCRIPT-699554-0.zip-4-pdfium-1.png",
                "GHOSTSCRIPT-699554-0.zip-4.pdf", 1, 3
            },

            /*
            new object[]
            {
                "fseprd1102849-mupdf-1.png",
                "fseprd1102849.pdf", 1, 3
            },
            new object[]
            {
                "fseprd1102849-pdfium-1.png",
                "fseprd1102849.pdf", 1, 3
            },
            */

            new object[]
            {
                "GHOSTSCRIPT-696547-0.zip-7-mupdf-1.png",
                "GHOSTSCRIPT-696547-0.zip-7.pdf", 1, 3
            },
            new object[]
            {
                "GHOSTSCRIPT-696547-0.zip-7-pdfium-1.png",
                "GHOSTSCRIPT-696547-0.zip-7.pdf", 1, 3
            },
        };

        public static readonly object[][] documentsPdfPig = new object[][]
        {
            // These are not perfect yet and can be updated once the rendering is improved

            new object[]
            {
                "AcroFormsBasicFields_1.png",
                "AcroFormsBasicFields.pdf", 1, 2
            },
            new object[]
            {
                "APISmap1_1.png",
                "APISmap1.pdf", 1, 2
            },
            new object[]
            {
                "Apitron.PDF.Kit.Samples_patternFill_1.png",
                "Apitron.PDF.Kit.Samples_patternFill.pdf", 1, 2
            },
            new object[]
            {
                "cat-genetics_bobld_1.png",
                "cat-genetics_bobld.pdf", 1, 2
            },
            new object[]
            {
                "fseprd1102849_1.png",
                "fseprd1102849.pdf", 1, 2
            },
            new object[]
            {
                "GHOSTSCRIPT-692307-2.zip-3_1.png",
                "GHOSTSCRIPT-692307-2.zip-3.pdf", 1, 2
            },
            new object[]
            {
                "GHOSTSCRIPT-692564-0_1.png",
                "GHOSTSCRIPT-692564-0.pdf", 1, 2
            },
            new object[]
            {
                "GHOSTSCRIPT-693073-1_1.png",
                "GHOSTSCRIPT-693073-1.pdf", 1, 2
            },
            new object[]
            {
                "GHOSTSCRIPT-693073-1_2.png",
                "GHOSTSCRIPT-693073-1.pdf", 2, 2
            },
            new object[]
            {
                "GHOSTSCRIPT-693534-0_1.png",
                "GHOSTSCRIPT-693534-0.pdf", 1, 2
            },
            new object[]
            {
                "GHOSTSCRIPT-693664-0_1.png",
                "GHOSTSCRIPT-693664-0.pdf", 1, 2
            },
            new object[]
            {
                "GHOSTSCRIPT-694454-0.zip-36_1.png",
                "GHOSTSCRIPT-694454-0.zip-36.pdf", 1, 2
            },
            /* Not sure why it fails
            new object[]
            {
                "GHOSTSCRIPT-695241-0_3.png",
                "GHOSTSCRIPT-695241-0.pdf", 3, 2
            },
            */
            new object[]
            {
                "GHOSTSCRIPT-696116-0_1.png",
                "GHOSTSCRIPT-696116-0.pdf", 1, 2
            },
            new object[]
            {
                "GHOSTSCRIPT-696178-1_1.png",
                "GHOSTSCRIPT-696178-1.pdf", 1, 2
            },
            new object[]
            {
                "GHOSTSCRIPT-696547-0.zip-7_1.png",
                "GHOSTSCRIPT-696547-0.zip-7.pdf", 1, 2
            },
            new object[]
            {
                "GHOSTSCRIPT-696547-0.zip-9_1.png",
                "GHOSTSCRIPT-696547-0.zip-9.pdf", 1, 2
            },
            new object[]
            {
                "GHOSTSCRIPT-696547-0.zip-10_1.png",
                "GHOSTSCRIPT-696547-0.zip-10.pdf", 1, 2
            },
            new object[]
            {
                "GHOSTSCRIPT-697507-0_1.png",
                "GHOSTSCRIPT-697507-0.pdf", 1, 2
            },
            new object[]
            {
                "GHOSTSCRIPT-699375-5_1.png",
                "GHOSTSCRIPT-699375-5.pdf", 1, 2
            },
            new object[]
            {
                "GHOSTSCRIPT-699488-0_1.png",
                "GHOSTSCRIPT-699488-0.pdf", 1, 2
            },
            new object[]
            {
                "GHOSTSCRIPT-700931-0.7z-5_1.png",
                "GHOSTSCRIPT-700931-0.7z-5.pdf", 1, 2
            },
            new object[]
            {
                "GHOSTSCRIPT-700931-0.7z-5_2.png",
                "GHOSTSCRIPT-700931-0.7z-5.pdf", 2, 2
            },
            new object[]
            {
                "journal.pone.0196757_1.png",
                "journal.pone.0196757.pdf", 1, 2
            },
            new object[]
            {
                "journal.pone.0196757_7.png",
                "journal.pone.0196757.pdf", 7, 2
            },
            new object[]
            {
                "journal.pone.0196757_12.png",
                "journal.pone.0196757.pdf", 12, 2
            },
            new object[]
            {
                "PDFBOX-1869-4_1.png",
                "PDFBOX-1869-4.pdf", 1, 2
            },
        };


        [Theory(Skip = "for debugging purpose.")]
        [MemberData(nameof(documents))]
        public void TestWithResize(string expectedImage, string pdfFile, int pageNumber, int scale)
        {
            bool success = PdfToImageHelper.TestResizeSinglePage(pdfFile, pageNumber, expectedImage, scale);
            Assert.True(success);
        }

        [Theory(Skip = "for debugging purpose.")]
        [MemberData(nameof(documents))]
        public void TestAtScale(string expectedImage, string pdfFile, int pageNumber, int scale)
        {
            bool success = PdfToImageHelper.TestSinglePage(pdfFile, pageNumber, expectedImage, scale);
            Assert.True(success);
        }

        [Theory]
        [MemberData(nameof(documentsPdfPig))]
        public void PdfPigSkiaTest(string expectedImage, string pdfFile, int pageNumber, int scale)
        {
            expectedImage = Path.Combine("pdfpig_skia", expectedImage);
            bool success = PdfToImageHelper.TestSinglePage(pdfFile, pageNumber, expectedImage, scale);
            Assert.True(success);
        }
    }
}
