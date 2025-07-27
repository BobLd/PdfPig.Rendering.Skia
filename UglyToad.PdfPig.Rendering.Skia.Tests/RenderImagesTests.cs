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
using UglyToad.PdfPig.Rendering.Skia.Helpers;
using Xunit;

namespace UglyToad.PdfPig.Rendering.Skia.Tests
{
    public class RenderImagesTests
    {

        [Fact]
        public void PdfPigImageAsSkiaImages()
        {
            using (var document = PdfDocument.Open(Path.Combine("SpecificTestDocuments", "Shadows.at.Sundown.-.Lvl.11_removed.pdf"), SkiaRenderingParsingOptions.Instance))
            {
                var page = document.GetPage(1);
                foreach (var pdfImage in page.GetImages())
                {
                    var skImage = pdfImage.GetSKImage();
                    
                    Assert.NotNull(skImage);
                    Assert.True(skImage.Width > 0);
                    Assert.True(skImage.Height > 0);
                }
            }
        }
    }
}
