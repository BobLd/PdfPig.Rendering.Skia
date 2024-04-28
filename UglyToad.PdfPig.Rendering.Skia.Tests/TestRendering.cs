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

        [Theory]
        [MemberData(nameof(documents))]
        public void TestWithResize(string expectedImage, string pdfFile, int pageNumber, int scale)
        {
            bool success = PdfToImageHelper.TestResizeSinglePage(pdfFile, pageNumber, expectedImage, scale);
            Assert.True(success);
        }

        [Theory]
        [MemberData(nameof(documents))]
        public void TestAtScale(string expectedImage, string pdfFile, int pageNumber, int scale)
        {
            bool success = PdfToImageHelper.TestSinglePage(pdfFile, pageNumber, expectedImage, scale);
            Assert.True(success);
        }
    }
}
