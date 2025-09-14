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
                // https://github.com/BobLd/PdfPig.Rendering.Skia/issues/26
                "Page_28_1.png",
                "Page_28.pdf", 1, 2
            },

            new object[]
            {
                "MOZILLA-LINK-3264-0_1.png",
                "MOZILLA-LINK-3264-0.pdf", 1, 2
            },
            new object[]
            {
                "MOZILLA-LINK-3264-0_3.png",
                "MOZILLA-LINK-3264-0.pdf", 3, 2
            },
            new object[]
            {
                "MOZILLA-LINK-3264-0_4.png",
                "MOZILLA-LINK-3264-0.pdf", 4, 2
            },

            new object[]
            {
                "caly-issues-56-1_1.png",
                "caly-issues-56-1.pdf", 1, 2
            },
            new object[]
            {
                "caly-issues-58-2_1.png",
                "caly-issues-58-2.pdf", 1, 2
            },
            new object[]
            {
                // Output image is wrong - but renders JPX image
                "68-1990-01_A_1.png",
                "68-1990-01_A.pdf", 1, 2
            },
            new object[]
            {
                "68-1990-01_A_10.png",
                "68-1990-01_A.pdf", 10, 2
            },
            new object[]
            {
                "68-1990-01_A_19.png",
                "68-1990-01_A.pdf", 19, 2
            },
            new object[]
            {
                "68-1990-01_A_2.png",
                "68-1990-01_A.pdf", 2, 2
            },
            new object[]
            {
                "68-1990-01_A_31.png",
                "68-1990-01_A.pdf", 31, 2
            },
            new object[]
            {
                "68-1990-01_A_41.png",
                "68-1990-01_A.pdf", 41, 2
            },
            new object[]
            {
                "68-1990-01_A_7.png",
                "68-1990-01_A.pdf", 7, 2
            },
            new object[]
            {
                "11194059_2017-11_de_s_1.png",
                "11194059_2017-11_de_s.pdf", 1, 2
            },
            new object[]
            {
                "2108.11480_1.png",
                "2108.11480.pdf", 1, 2
            },
            new object[]
            {
                "2108.11480_2.png",
                "2108.11480.pdf", 2, 2
            },
            new object[]
            {
                "2108.11480_4.png",
                "2108.11480.pdf", 4, 2
            },
            new object[]
            {
                "bold-italic_1.png",
                "bold-italic.pdf", 1, 2
            },
            new object[]
            {
                "DeviceN_CS_test_6.png",
                "DeviceN_CS_test.pdf", 6, 2
            },
            new object[]
            {
                "hex_0x0006_1.png",
                "hex_0x0006.pdf", 1, 2
            },
            new object[]
            {
                "ICML03-081_4.png",
                "ICML03-081.pdf", 4, 2
            },
            new object[]
            {
                "ICML03-081_6.png",
                "ICML03-081.pdf", 6, 2
            },
            new object[]
            {
                "Layer pdf - 322_High_Holborn_building_Brochure_1.png",
                "Layer pdf - 322_High_Holborn_building_Brochure.pdf", 1, 2
            },
            new object[]
            {
                "Motor Insurance claim form_1.png",
                "Motor Insurance claim form.pdf", 1, 2
            },
            new object[]
            {
                "path_ext_oddeven_1.png",
                "path_ext_oddeven.pdf", 1, 2
            },
            new object[]
            {
                "Pig Production Handbook_15.png",
                "Pig Production Handbook.pdf", 15, 2
            },
            new object[]
            {
                "Pig Production Handbook_17.png",
                "Pig Production Handbook.pdf", 17, 2
            },
            new object[]
            {
                "Pig Production Handbook_9.png",
                "Pig Production Handbook.pdf", 9, 2
            },
            new object[]
            {
                "Rotated Text Libre Office_1.png",
                "Rotated Text Libre Office.pdf", 1, 2
            },
            new object[]
            {
                "SPARC - v9 Architecture Manual_1.png",
                "SPARC - v9 Architecture Manual.pdf", 1, 2
            },
            new object[]
            {
                "TIKA-1552-0_1.png",
                "TIKA-1552-0.pdf", 1, 2
            },
            new object[]
            {
                "TIKA-1552-0_3.png",
                "TIKA-1552-0.pdf", 3, 2
            },
            new object[]
            {
                "TIKA-1552-0_68.png",
                "TIKA-1552-0.pdf", 68, 2
            },
            new object[]
            {
                "TIKA-1552-0_75.png",
                "TIKA-1552-0.pdf", 75, 2
            },
            new object[]
            {
                "Type0 Font_1.png",
                "Type0 Font.pdf", 1, 2
            },
            new object[]
            {
                "Why.does.this.not.work_1.png",
                "Why.does.this.not.work.pdf", 1, 2
            },
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
            new object[]
            {
                "Grapheme clusters emoji_1.png",
                "Grapheme clusters emoji.pdf", 1, 2
            },
            new object[]
            {
                "22060_A1_01_Plans-1_1.png",
                "22060_A1_01_Plans-1.pdf", 1, 2
            },
            new object[]
            {
                "P2P-33713919_2.png",
                "P2P-33713919.pdf", 2, 2
            },
            new object[]
            {
                "felltypes-test_1.png",
                "felltypes-test.pdf", 1, 2
            },
            new object[]
            {
                "FontMatrix-concat_1.png",
                "FontMatrix-concat.pdf", 1, 2
            },
            new object[]
            {
                "GHOSTSCRIPT-539359-0.zip-0_1.png",
                "GHOSTSCRIPT-539359-0.zip-0.pdf", 1, 2
            },
            new object[]
            {
                "GHOSTSCRIPT-700370-2_1.png",
                "GHOSTSCRIPT-700370-2.pdf", 1, 2
            },
            new object[]
            {
                "GHOSTSCRIPT-686749-1_1.png",
                "GHOSTSCRIPT-686749-1.pdf", 1, 2
            },
            new object[]
            {
                "GHOSTSCRIPT-693295-0_1.png",
                "GHOSTSCRIPT-693295-0.pdf", 1, 2
            },
            new object[]
            {
                "GHOSTSCRIPT-698721-1_1.png",
                "GHOSTSCRIPT-698721-1.pdf", 1, 2
            },
        };

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
#if DEBUG
            throw new System.ArgumentException("PdfPigSkiaTest needs to run in Release mode.");
#endif

            expectedImage = Path.Combine("pdfpig_skia", expectedImage);
            bool success = PdfToImageHelper.TestSinglePage(pdfFile, pageNumber, expectedImage, scale);
            Assert.True(success);
        }
    }
}
