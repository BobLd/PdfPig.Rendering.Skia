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

using System.IO;
using Xunit;

namespace UglyToad.PdfPig.Rendering.Skia.Tests;

public class GitHubIssues
{
    private const int Scale = 2;
    private const string OutputPath = "OutputSpecific";

    public GitHubIssues()
    {
        Directory.CreateDirectory(OutputPath);
    }

    [Fact]
    public void IssuePdfPig775()
    {
        using (var document = PdfDocument.Open(Path.Combine(Helper.SpecificTestDocumentsFolder, "Shadows.at.Sundown.-.Lvl.11_removed.pdf"), SkiaRenderingParsingOptions.Instance))
        {
            document.AddSkiaPageFactory();

            for (int p = 1; p <= document.NumberOfPages; ++p)
            {
                using (var fs = new FileStream(Path.Combine(OutputPath, $"Shadows.at.Sundown.-.Lvl.11_removed_{p}.png"), FileMode.Create))
                using (var ms = document.GetPageAsPng(p, Scale))
                {
                    ms.WriteTo(fs);
                }
            }
        }
    }

    [Fact]
    public void Issue27_1()
    {
        using (var document = PdfDocument.Open(Path.Combine(Helper.SpecificTestDocumentsFolder, "Go.pdf"), SkiaRenderingParsingOptions.Instance))
        {
            document.AddSkiaPageFactory();

            for (int p = 1; p <= document.NumberOfPages; ++p)
            {
                using (var fs = new FileStream(Path.Combine(OutputPath, $"Go_{p}.png"), FileMode.Create))
                using (var ms = document.GetPageAsPng(p, Scale))
                {
                    ms.WriteTo(fs);
                }
            }
        }
    }

    [Fact]
    public void Issue27_2()
    {
        using (var document = PdfDocument.Open(Path.Combine(Helper.SpecificTestDocumentsFolder, "new.pdf"), SkiaRenderingParsingOptions.Instance))
        {
            document.AddSkiaPageFactory();

            for (int p = 1; p <= document.NumberOfPages; ++p)
            {
                foreach (var image in document.GetPage(p).GetImages())
                {
                    Assert.True(image.TryGetPng(out _));
                }

                using (var fs = new FileStream(Path.Combine(OutputPath, $"new_{p}.png"), FileMode.Create))
                using (var ms = document.GetPageAsPng(p, Scale))
                {
                    ms.WriteTo(fs);
                }
            }
        }
    }
}
