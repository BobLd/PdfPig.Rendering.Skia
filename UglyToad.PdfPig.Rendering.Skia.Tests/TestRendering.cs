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

// Uses documents from https://github.com/pdf-association/pdf-differences (CC BY 4.0)

using System.IO;
using Xunit;

namespace UglyToad.PdfPig.Rendering.Skia.Tests;

public class TestRendering
{
    public static readonly TheoryData<string, string, int, int> DocumentsPdfPig = new()
    {
        // These are not perfect yet and can be updated once the rendering is improved
        {
            // https://github.com/BobLd/PdfPig.Rendering.Skia/issues/26
            "Page_28_1.png",
            "Page_28.pdf", 1, 2
        },

        {
            "MOZILLA-LINK-3264-0_1.png",
            "MOZILLA-LINK-3264-0.pdf", 1, 2
        },
        {
            "MOZILLA-LINK-3264-0_3.png",
            "MOZILLA-LINK-3264-0.pdf", 3, 2
        },
        {
            "MOZILLA-LINK-3264-0_4.png",
            "MOZILLA-LINK-3264-0.pdf", 4, 2
        },

        {
            "caly-issues-56-1_1.png",
            "caly-issues-56-1.pdf", 1, 2
        },
        {
            "caly-issues-58-2_1.png",
            "caly-issues-58-2.pdf", 1, 2
        },
        {
            // Output image is wrong - but renders JPX image
            "68-1990-01_A_1.png",
            "68-1990-01_A.pdf", 1, 2
        },
        {
            "68-1990-01_A_10.png",
            "68-1990-01_A.pdf", 10, 2
        },
        {
            "68-1990-01_A_19.png",
            "68-1990-01_A.pdf", 19, 2
        },
        {
            "68-1990-01_A_2.png",
            "68-1990-01_A.pdf", 2, 2
        },
        {
            "68-1990-01_A_31.png",
            "68-1990-01_A.pdf", 31, 2
        },
        {
            "68-1990-01_A_41.png",
            "68-1990-01_A.pdf", 41, 2
        },
        {
            "68-1990-01_A_7.png",
            "68-1990-01_A.pdf", 7, 2
        },
        {
            "68-1990-01_A_15.png",
            "68-1990-01_A.pdf", 15, 2
        },
        {
            "68-1990-01_A_21.png",
            "68-1990-01_A.pdf", 21, 2
        },
        {
            "11194059_2017-11_de_s_1.png",
            "11194059_2017-11_de_s.pdf", 1, 2
        },
        {
            "2108.11480_1.png",
            "2108.11480.pdf", 1, 2
        },
        {
            "2108.11480_2.png",
            "2108.11480.pdf", 2, 2
        },
        {
            "2108.11480_4.png",
            "2108.11480.pdf", 4, 2
        },
        {
            "bold-italic_1.png",
            "bold-italic.pdf", 1, 2
        },
        {
            "DeviceN_CS_test_6.png",
            "DeviceN_CS_test.pdf", 6, 2
        },
        {
            "hex_0x0006_1.png",
            "hex_0x0006.pdf", 1, 2
        },
        {
            "ICML03-081_4.png",
            "ICML03-081.pdf", 4, 2
        },
        {
            "ICML03-081_6.png",
            "ICML03-081.pdf", 6, 2
        },
        {
            "Layer pdf - 322_High_Holborn_building_Brochure_1.png",
            "Layer pdf - 322_High_Holborn_building_Brochure.pdf", 1, 2
        },
        {
            "Motor Insurance claim form_1.png",
            "Motor Insurance claim form.pdf", 1, 2
        },
        {
            "path_ext_oddeven_1.png",
            "path_ext_oddeven.pdf", 1, 2
        },
        {
            "Pig Production Handbook_15.png",
            "Pig Production Handbook.pdf", 15, 2
        },
        {
            "Pig Production Handbook_17.png",
            "Pig Production Handbook.pdf", 17, 2
        },
        {
            "Pig Production Handbook_9.png",
            "Pig Production Handbook.pdf", 9, 2
        },
        {
            "Rotated Text Libre Office_1.png",
            "Rotated Text Libre Office.pdf", 1, 2
        },
        {
            "SPARC - v9 Architecture Manual_1.png",
            "SPARC - v9 Architecture Manual.pdf", 1, 2
        },
        {
            "TIKA-1552-0_1.png",
            "TIKA-1552-0.pdf", 1, 2
        },
        {
            "TIKA-1552-0_3.png",
            "TIKA-1552-0.pdf", 3, 2
        },
        {
            "TIKA-1552-0_68.png",
            "TIKA-1552-0.pdf", 68, 2
        },
        {
            "TIKA-1552-0_75.png",
            "TIKA-1552-0.pdf", 75, 2
        },
        {
            "Type0 Font_1.png",
            "Type0 Font.pdf", 1, 2
        },
        {
            "Why.does.this.not.work_1.png",
            "Why.does.this.not.work.pdf", 1, 2
        },
        {
            "AcroFormsBasicFields_1.png",
            "AcroFormsBasicFields.pdf", 1, 2
        },
        {
            "APISmap1_1.png",
            "APISmap1.pdf", 1, 2
        },
        {
            "Apitron.PDF.Kit.Samples_patternFill_1.png",
            "Apitron.PDF.Kit.Samples_patternFill.pdf", 1, 2
        },
        {
            "Apitron.PDF.Kit.Samples_patternFill-rotated_1.png",
            "Apitron.PDF.Kit.Samples_patternFill-rotated.pdf", 1, 2
        },
        {
            "cat-genetics_bobld_1.png",
            "cat-genetics_bobld.pdf", 1, 2
        },
        {
            "cat-genetics_1.png",
            "cat-genetics.pdf", 1, 2
        },
        {
            "fseprd1102849_1.png",
            "fseprd1102849.pdf", 1, 2
        },
        {
            "GHOSTSCRIPT-692307-2.zip-3_1.png",
            "GHOSTSCRIPT-692307-2.zip-3.pdf", 1, 2
        },
        {
            "GHOSTSCRIPT-692564-0_1.png",
            "GHOSTSCRIPT-692564-0.pdf", 1, 2
        },
        {
            "GHOSTSCRIPT-693073-1_1.png",
            "GHOSTSCRIPT-693073-1.pdf", 1, 2
        },
        {
            "GHOSTSCRIPT-693073-1_2.png",
            "GHOSTSCRIPT-693073-1.pdf", 2, 2
        },
        {
            "GHOSTSCRIPT-693534-0_1.png",
            "GHOSTSCRIPT-693534-0.pdf", 1, 2
        },
        {
            "GHOSTSCRIPT-693664-0_1.png",
            "GHOSTSCRIPT-693664-0.pdf", 1, 2
        },
        {
            "GHOSTSCRIPT-694454-0.zip-36_1.png",
            "GHOSTSCRIPT-694454-0.zip-36.pdf", 1, 2
        },
        /* Not sure why it fails
        {
            "GHOSTSCRIPT-695241-0_3.png",
            "GHOSTSCRIPT-695241-0.pdf", 3, 2
        },
        */
        {
            "GHOSTSCRIPT-696116-0_1.png",
            "GHOSTSCRIPT-696116-0.pdf", 1, 2
        },
        {
            "GHOSTSCRIPT-696178-1_1.png",
            "GHOSTSCRIPT-696178-1.pdf", 1, 2
        },
        {
            "GHOSTSCRIPT-696547-0.zip-7_1.png",
            "GHOSTSCRIPT-696547-0.zip-7.pdf", 1, 2
        },
        {
            "GHOSTSCRIPT-696547-0.zip-9_1.png",
            "GHOSTSCRIPT-696547-0.zip-9.pdf", 1, 2
        },
        {
            "GHOSTSCRIPT-696547-0.zip-10_1.png",
            "GHOSTSCRIPT-696547-0.zip-10.pdf", 1, 2
        },
        {
            "GHOSTSCRIPT-697507-0_1.png",
            "GHOSTSCRIPT-697507-0.pdf", 1, 2
        },
        {
            "GHOSTSCRIPT-699375-5_1.png",
            "GHOSTSCRIPT-699375-5.pdf", 1, 2
        },
        {
            "GHOSTSCRIPT-699488-0_1.png",
            "GHOSTSCRIPT-699488-0.pdf", 1, 2
        },
        {
            "GHOSTSCRIPT-700931-0.7z-5_1.png",
            "GHOSTSCRIPT-700931-0.7z-5.pdf", 1, 2
        },
        {
            "GHOSTSCRIPT-700931-0.7z-5_2.png",
            "GHOSTSCRIPT-700931-0.7z-5.pdf", 2, 2
        },
        {
            "journal.pone.0196757_1.png",
            "journal.pone.0196757.pdf", 1, 2
        },
        {
            "journal.pone.0196757_7.png",
            "journal.pone.0196757.pdf", 7, 2
        },
        {
            "journal.pone.0196757_12.png",
            "journal.pone.0196757.pdf", 12, 2
        },
        {
            "PDFBOX-1869-4_1.png",
            "PDFBOX-1869-4.pdf", 1, 2
        },
        {
            "PDFBOX-1869-4-rotated_1.png",
            "PDFBOX-1869-4-rotated.pdf", 1, 2
        },
        {
            "Grapheme clusters emoji_1.png",
            "Grapheme clusters emoji.pdf", 1, 2
        },
        {
            "22060_A1_01_Plans-1_1.png",
            "22060_A1_01_Plans-1.pdf", 1, 2
        },
        {
            "P2P-33713919_2.png",
            "P2P-33713919.pdf", 2, 2
        },
        {
            "felltypes-test_1.png",
            "felltypes-test.pdf", 1, 2
        },
        {
            "FontMatrix-concat_1.png",
            "FontMatrix-concat.pdf", 1, 2
        },
        {
            "GHOSTSCRIPT-539359-0.zip-0_1.png",
            "GHOSTSCRIPT-539359-0.zip-0.pdf", 1, 2
        },
        {
            "annots_rotated_1.png",
            "annots_rotated.pdf", 1, 2
        },
        {
            "GHOSTSCRIPT-700370-2_1.png",
            "GHOSTSCRIPT-700370-2.pdf", 1, 2
        },
        {
            "GHOSTSCRIPT-686749-1_1.png",
            "GHOSTSCRIPT-686749-1.pdf", 1, 2
        },
        {
            "GHOSTSCRIPT-693295-0_1.png",
            "GHOSTSCRIPT-693295-0.pdf", 1, 2
        },
        {
            "GHOSTSCRIPT-693295-0_2.png",
            "GHOSTSCRIPT-693295-0.pdf", 2, 2
        },
        {
            "GHOSTSCRIPT-698721-1_1.png",
            "GHOSTSCRIPT-698721-1.pdf", 1, 2
        },
        {
            "GHOSTSCRIPT-686821-0_1.png",
            "GHOSTSCRIPT-686821-0.pdf", 1, 2
        },
        {
            "GHOSTSCRIPT-698721-1-rotated_1.png",
            "GHOSTSCRIPT-698721-1-rotated.pdf", 1, 2
        },
        {
            "blend_opacity_1.png",
            "blend_opacity.pdf", 1, 2
        },
        {
            "issue-50_2.png",
            "issue-50.pdf", 2, 2
        },
        {
            "jtehm-melillo-2679746_1.png",
            "jtehm-melillo-2679746.pdf", 1, 2
        },
        {
            "GHOSTSCRIPT-692958-0.zip-2_1.png",
            "GHOSTSCRIPT-692958-0.zip-2.pdf", 1, 2
        },

        {
            "PDFBOX-2100-gouraud-RGB-function_1.png",
            "PDFBOX-2100-gouraud-RGB-function.pdf", 1, 2
        },

        {
            "MOZILLA-LINK-6305-5_1.png",
            "MOZILLA-LINK-6305-5.pdf", 1, 2
        },
        {
            "MOZILLA-LINK-6305-5_13.png",
            "MOZILLA-LINK-6305-5.pdf", 13, 2
        },
        {
            "MOZILLA-LINK-6305-5_15.png",
            "MOZILLA-LINK-6305-5.pdf", 15, 2
        },
        {
            "MOZILLA-LINK-6305-5_20.png",
            "MOZILLA-LINK-6305-5.pdf", 20, 2
        },

        {
            // Not completely correct but good start
            "0000190_1.png",
            "0000190.pdf", 1, 2
        },
        {
            // Not completely correct but good start
            "0000190_3.png",
            "0000190.pdf", 3, 2
        },
        {
            // Rendering not correct
            "0000190_cropped_1.png",
            "0000190_cropped.pdf", 1, 2
        },

        {
            "P_1.png",
            "P.pdf", 1, 2
        },
        {
            "P_2.png",
            "P.pdf", 2, 2
        },
        {
            "P_3.png",
            "P.pdf", 3, 2
        },
        {
            "P_4.png",
            "P.pdf", 4, 2
        },
        {
            "P_5.png",
            "P.pdf", 5, 2
        },
        {
            "P_6.png",
            "P.pdf", 6, 2
        },

        {
            "2_uncolor_tiling_1.png",
            "2_uncolor_tiling.pdf", 1, 2
        },
        {
            "gs-bugzilla694385_1.png",
            "gs-bugzilla694385.pdf", 1, 2
        },

        {
            "2_color_tiling_1.png",
            "2_color_tiling.pdf", 1, 2
        },
        {
            "2_color_type3_pattern_bbox_1.png",
            "2_color_type3_pattern_bbox.pdf", 1, 2
        },
        {
            "2_shading_type_6_001_1.png",
            "2_shading_type_6_001.pdf", 1, 2
        },
        {
            "2_shading_type1_1.png",
            "2_shading_type1.pdf", 1, 2
        },
        {
            "2_shading_type1_sc__1.png",
            "2_shading_type1_sc_.pdf", 1, 2
        },
        {
            "2_shading_type3_1.png",
            "2_shading_type3.pdf", 1, 2
        },
        {
            "2_shading_type4_h_1.png",
            "2_shading_type4_h.pdf", 1, 2
        },
        {
            "2_shading_type5_h_1.png",
            "2_shading_type5_h.pdf", 1, 2
        },
        {
            "Pig Production Handbook_1.png",
            "Pig Production Handbook.pdf", 1, 2
        },
        {
            "MOZILLA-LINK-4379-0_1.png",
            "MOZILLA-LINK-4379-0.pdf", 1, 2
        },
        {
            "GWG1610_Softmasks_Text_part1_X4_1.png",
            "GWG1610_Softmasks_Text_part1_X4.pdf", 1, 2
        },
        {
            "GWG1611_Softmasks_Text_part2_X4_1.png",
            "GWG1611_Softmasks_Text_part2_X4.pdf", 1, 2
        },
        {
            "GWG166_Softmasks_Images_DeviceCMYK_X4_1.png",
            "GWG166_Softmasks_Images_DeviceCMYK_X4.pdf", 1, 2
        },
        {
            "GWG168_Softmasks_Vector_part1_X4_1.png",
            "GWG168_Softmasks_Vector_part1_X4.pdf", 1, 2
        },
        {
            "GWG169_Softmasks_Vector_part2_X4_1.png",
            "GWG169_Softmasks_Vector_part2_X4.pdf", 1, 2
        },
        {
            "GHOSTSCRIPT-686965-0_1.png",
            "GHOSTSCRIPT-686965-0.pdf", 1, 2
        },
        {
            "GWG090_Font-Support_x3_1.png",
            "GWG090_Font-Support_x3.pdf", 1, 5
        },
        {
            "GHOSTSCRIPT-692637-0-273_1.png",
            "GHOSTSCRIPT-692637-0-273.pdf", 1, 2
        },
        {
            "GHOSTSCRIPT-692637-0-270_1.png",
            "GHOSTSCRIPT-692637-0-270.pdf", 1, 2
        },
        {
            "full-size-image_1.png",
            "full-size-image.pdf", 1, 2
        },
        {
            "GWG060_Shading_x1a_1.png",
            "GWG060_Shading_x1a.pdf", 1, 2
        },
        {
            "GWG061_Shading_x1a_1.png",
            "GWG061_Shading_x1a.pdf", 1, 2
        },
        {
            "JD5008_2.png",
            "JD5008.pdf", 2, 2
        },
        {
            "0000042_1.png",
            "0000042.pdf", 1, 2
        },
        {
            "GHOSTSCRIPT-692685-0_1.png",
            "GHOSTSCRIPT-692685-0.pdf", 1, 2
        },
        {
            "GHOSTSCRIPT-699178-0_1.png",
            "GHOSTSCRIPT-699178-0.pdf", 1, 2
        },
        {
            "GHOSTSCRIPT-699178-0_2.png",
            "GHOSTSCRIPT-699178-0.pdf", 2, 2
        },
        {
            "issues-1176-1_1.png",
            "issues-1176-1.pdf", 1, 2
        },
        {
            "0000281_1.png",
            "0000281.pdf", 1, 2
        },
        {
            "0966761_1.png",
            "0966761.pdf", 1, 2
        },
        {
            "0000353_1.png",
            "0000353.pdf", 1, 2
        },
        {
            "0966681_1.png",
            "0966681.pdf", 1, 2
        },

        {
            "0000851_1.png",
            "0000851.pdf", 1, 2
        },
        {
            "0000851_2.png",
            "0000851.pdf", 2, 2
        },
        {
            "0000851_3.png",
            "0000851.pdf", 3, 2
        },
        {
            "0966203_1.png",
            "0966203.pdf", 1, 2
        },
        {
            "0966320_2.png",
            "0966320.pdf", 2, 2
        },
        {
            "Rotation 45_1.png",
            "Rotation 45.pdf", 1, 2
        },
        {
            "text-icons-all_1.png",
            "text-icons-all.pdf", 1, 2
        },
        {
            "annotation-colors-2_1.png",
            "annotation-colors-2.pdf", 1, 2
        },
        {
            "0000079_1.png",
            "0000079.pdf", 1, 2
        },
        {
            "0966260_1.png",
            "0966260.pdf", 1, 2
        },
        {
            "0010050_1.png",
            "0010050.pdf", 1, 2
        },
        {
            "0011979_2.png",
            "0011979.pdf", 2, 2
        },
        {
            "SelfIntersecting-Transparency_1.png",
            "SelfIntersecting-Transparency.pdf", 1, 2
        },

        {
            "DefaultRGBColourSpaces_1.png",
            "DefaultRGBColourSpaces.pdf", 1, 2
        },
        {
            "DefaultRGBColourSpaces-inherit_1.png",
            "DefaultRGBColourSpaces-inherit.pdf", 1, 2
        },
        {
            "NegativeFontSize_1.png",
            "NegativeFontSize.pdf", 1, 2
        },
        {
            "OverlappingGlyphClipping_1.png",
            "OverlappingGlyphClipping.pdf", 1, 2
        },
        {
            "TextClippingModeChanges_1.png",
            "TextClippingModeChanges.pdf", 1, 2
        },
        {
            "VerticalText_1.png",
            "VerticalText.pdf", 1, 2
        },
        
        // TODO - Add Type3Test.pdf + DefaultColourSpaces.230802.pdf test
    };

    public static bool IsReleaseBuild =>
#if DEBUG
        false;
#else
        true;
#endif

    [Theory(Skip = "Only valid in Release mode.", SkipUnless = nameof(IsReleaseBuild))]
    [MemberData(nameof(DocumentsPdfPig))]
    public void PdfPigSkiaTest(string expectedImage, string pdfFile, int pageNumber, int scale)
    {
        expectedImage = Path.Combine("pdfpig_skia", expectedImage);
        bool success = PdfToImageHelper.TestSinglePage(pdfFile, pageNumber, expectedImage, scale);
        Assert.True(success);
    }
}
