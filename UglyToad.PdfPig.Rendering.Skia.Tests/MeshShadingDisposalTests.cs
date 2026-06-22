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

/// <summary>
/// Locks in the disposal-safety invariant of the Coons/Tensor mesh path: the page
/// <see cref="SKPicture"/> returned by Process() references the per-patch mesh sub-pictures and
/// their texture image-shaders, but the page-scoped <c>_meshPictureCache</c> (and the textured
/// patch's image/shader/paint) are disposed before the call returns. This is only safe because
/// the parent page picture and the recorded draw ops hold their own native refs. These tests
/// rasterise the page picture *after* that disposal has run, so a regression that frees a
/// still-referenced native (a use-after-free) shows up as a crash, blank output, or unstable
/// replay rather than passing silently.
/// </summary>
public class MeshShadingDisposalTests
{
    public static readonly object[][] MeshDocuments =
    [
        // Type 6 Coons-patch mesh — exercises _meshPictureCache and, where the shading carries a
        // Function, the textured path (image shader recorded then disposed mid-recording).
        new object[] { "2_shading_type_6_001.pdf", 1 },
        // Mesh-heavy document that paints its shading many times per page (cache hit + replay).
        new object[] { "0000851.pdf", 1 }, new object[] { "0000851.pdf", 2 }, new object[] { "0000851.pdf", 3 }
    ];

    [Theory]
    [MemberData(nameof(MeshDocuments))]
    public void PagePictureRendersAfterMeshCacheDisposed(string pdfFile, int pageNumber)
    {
        string docPath = Path.Combine(Helper.DocumentsFolder, pdfFile);

        using var document = PdfDocument.Open(docPath, SkiaRenderingParsingOptions.Instance);
        document.AddSkiaPageFactory();

        // Process() builds the mesh-picture cache, records every patch (and its texture shader)
        // into the page picture, then disposes the cache in its finally — all before returning.
        using SKPicture picture = document.GetPageAsSKPicture(pageNumber);

        byte[] first = Rasterize(picture);
        byte[] second = Rasterize(picture);

        // No crash above means replaying after disposal did not touch a freed native. A freed
        // mesh sub-picture / image-shader would otherwise render nothing — assert real content.
        Assert.True(HasNonWhitePixel(first),
            $"{pdfFile} (page {pageNumber}) rendered blank after the mesh cache was disposed.");

        // The picture must replay identically every time; freed-then-reused native memory would
        // produce garbage that differs between replays.
        Assert.Equal(first, second);
    }

    private static byte[] Rasterize(SKPicture picture)
    {
        SKRect bounds = picture.CullRect;
        int width = Math.Max(1, (int)Math.Ceiling(bounds.Width));
        int height = Math.Max(1, (int)Math.Ceiling(bounds.Height));

        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            canvas.Translate(-bounds.Left, -bounds.Top);
            canvas.DrawPicture(picture);
        }

        return bitmap.GetPixelSpan().ToArray();
    }

    private static bool HasNonWhitePixel(byte[] rgbaPixels)
    {
        for (int i = 0; i + 3 < rgbaPixels.Length; i += 4)
        {
            if (rgbaPixels[i] != 255 || rgbaPixels[i + 1] != 255 || rgbaPixels[i + 2] != 255)
            {
                return true;
            }
        }

        return false;
    }
}
