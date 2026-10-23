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
using System.IO;
using SkiaSharp;
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

    /// <summary>
    /// https://github.com/BobLd/PdfPig.Rendering.Skia/issues/163: a non-embedded ZapfDingbats check mark used to be
    /// drawn with an installed font (Segoe UI Emoji on Windows: purple, offset). It must now come from the bundled
    /// substitute: drawn in the fill colour (black) and inside the widget.
    /// </summary>
    [Fact]
    public void Issue163_ZapfDingbatsCheckMarkUsesSubstitute()
    {
        using var document = PdfDocument.Open(Path.Combine(Helper.SpecificTestDocumentsFolder, "checkbox-widget.pdf"), SkiaRenderingParsingOptions.Instance);
        document.AddSkiaPageFactory();

        using SKBitmap bitmap = document.GetPageAsSKBitmap(1, Scale, SKColors.White);

        using (var fs = new FileStream(Path.Combine(OutputPath, "checkbox-widget_1.png"), FileMode.Create))
        {
            bitmap.Encode(fs, SKEncodedImageFormat.Png, 100);
        }

        // Widget /Rect [430 708 443.4934 719.6907] on a 792 pt high page. Stay inside its 1 pt border.
        var inner = new SKRectI((int)(431.5 * Scale), (int)((792 - 718.2) * Scale), (int)(442 * Scale), (int)((792 - 709.5) * Scale));

        int dark = 0, coloured = 0;
        for (int y = inner.Top; y < inner.Bottom; y++)
        {
            for (int x = inner.Left; x < inner.Right; x++)
            {
                SKColor c = bitmap.GetPixel(x, y);
                int max = Math.Max(c.Red, Math.Max(c.Green, c.Blue));
                int min = Math.Min(c.Red, Math.Min(c.Green, c.Blue));

                if (max - min > 40)
                {
                    coloured++;
                }
                else if (max < 96)
                {
                    dark++;
                }
            }
        }

        Assert.Equal(0, coloured);
        // The ✔ covers roughly a fifth of the box; a missing or tiny glyph would not.
        Assert.True(dark > inner.Width * inner.Height / 10, $"Only {dark} dark pixels in the check box.");
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
