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
using UglyToad.PdfPig.Graphics.Colors;
using Xunit;

namespace UglyToad.PdfPig.Rendering.Skia.Tests
{
    public class GitHubIssues
    {
        private const int _scale = 2;
        private const string _outputPath = "OutputSpecific";

        public GitHubIssues()
        {
            Directory.CreateDirectory(_outputPath);
        }

        [Fact]
        public void IssuePdfPig775()
        {
            using (var document = PdfDocument.Open(Path.Combine("SpecificTestDocuments", "Shadows.at.Sundown.-.Lvl.11_removed.pdf"), SkiaRenderingParsingOptions.Instance))
            {
                document.AddSkiaPageFactory();

                for (int p = 1; p <= document.NumberOfPages; ++p)
                {
                    using (var fs = new FileStream(Path.Combine(_outputPath, $"Shadows.at.Sundown.-.Lvl.11_removed_{p}.png"), FileMode.Create))
                    using (var ms = document.GetPageAsPng(p, _scale))
                    {
                        ms.WriteTo(fs);
                    }
                }
            }
        }

        [Fact]
        public void Issue27_1()
        {
            using (var document = PdfDocument.Open(Path.Combine("SpecificTestDocuments", "Go.pdf"), SkiaRenderingParsingOptions.Instance))
            {
                document.AddSkiaPageFactory();

                for (int p = 1; p <= document.NumberOfPages; ++p)
                {
                    using (var fs = new FileStream(Path.Combine(_outputPath, $"Go_{p}.png"), FileMode.Create))
                    using (var ms = document.GetPageAsPng(p, _scale))
                    {
                        ms.WriteTo(fs);
                    }
                }
            }
        }

        [Fact]
        public void Issue27_2()
        {
            using (var document = PdfDocument.Open(Path.Combine("SpecificTestDocuments", "new.pdf"), SkiaRenderingParsingOptions.Instance))
            {
                document.AddSkiaPageFactory();

                for (int p = 1; p <= document.NumberOfPages; ++p)
                {
                    foreach (var image in document.GetPage(p).GetImages())
                    {
                        Assert.True(image.TryGetPng(out _));
                    }
                    
                    using (var fs = new FileStream(Path.Combine(_outputPath, $"new_{p}.png"), FileMode.Create))
                    using (var ms = document.GetPageAsPng(p, _scale))
                    {
                        ms.WriteTo(fs);
                    }
                }
            }
        }
    }
}
