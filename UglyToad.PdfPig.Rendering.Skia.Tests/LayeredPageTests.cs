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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SkiaSharp;
using Xunit;

namespace UglyToad.PdfPig.Rendering.Skia.Tests;

public class LayeredPageTests
{
    private sealed class Gate : SKDrawable
    {
        private readonly SKPicture picture;
        public bool Visible;

        public Gate(SKPicture picture) { this.picture = picture; }

        protected override void OnDraw(SKCanvas canvas)
        {
            if (Visible)
            {
                canvas.DrawPicture(picture);
            }
        }

        protected override SKRect OnGetBounds() => picture.CullRect;
    }

    [Fact]
    public void RecordedDrawablesAreDrawnAgainOnEverySnapshot()
    {
        using var inner = new SKPictureRecorder();
        var innerCanvas = inner.BeginRecording(SKRect.Create(10, 10));
        using (var red = new SKPaint { Color = SKColors.Red })
        {
            innerCanvas.DrawRect(SKRect.Create(10, 10), red);
        }

        using var redPicture = inner.EndRecording();
        using var gate = new Gate(redPicture);

        using var outer = new SKPictureRecorder();
        var canvas = outer.BeginRecording(SKRect.Create(10, 10));
        canvas.ClipRect(SKRect.Create(5, 10));          // state that must apply to the drawable
        canvas.DrawDrawable(gate, SKMatrix.Identity);
        using var root = outer.EndRecordingAsDrawable();

        gate.Visible = false;
        root.NotifyDrawingChanged();
        using (var hidden = root.Snapshot())
        {
            Assert.Equal(SKColors.Empty, Pixel(hidden, 2, 2));
        }

        gate.Visible = true;
        root.NotifyDrawingChanged();
        using (var shown = root.Snapshot())
        {
            Assert.Equal(SKColors.Red, Pixel(shown, 2, 2));
            Assert.Equal(SKColors.Empty, Pixel(shown, 7, 2)); // clipped by the outer state
        }
    }

    [Theory]
    [InlineData("GWG150_OptionalContent-OCCD_X4.pdf")]
    [InlineData("GWG151_OptionalContent-RBGroup_X4.pdf")]
    [InlineData("GWG152_OptionalContent-OCMD_X4.pdf")]
    [InlineData("GWG166_Softmasks_Images_DeviceCMYK_X4.pdf")]
    [InlineData("GWG168_Softmasks_Vector_part1_X4.pdf")]
    [InlineData("GWG169_Softmasks_Vector_part2_X4.pdf")]
    [InlineData("68-1990-01_A.pdf")]
    public void Compose_DefaultState_MatchesTheDirectRender(string file)
    {
        using var document = PdfDocument.Open(Path.Combine(Helper.DocumentsFolder, file), SkiaRenderingParsingOptions.Instance);
        document.AddSkiaPageFactory();

        using var direct = document.GetPageAsSKBitmap(1, 2, SKColors.White);
        using var layered = document.GetPage<SkiaLayeredPage>(1);
        using var composed = layered.Compose(document.OptionalContent);
        using var composedBitmap = Rasterize(composed, direct.Width, direct.Height, 2);

        Assert.True(PdfToImageHelper.AreEquivalent(direct, composedBitmap));
    }

    [Fact]
    public void Compose_FollowsTheState()
    {
        using var document = PdfDocument.Open(Path.Combine(Helper.DocumentsFolder, "GWG151_OptionalContent-RBGroup_X4.pdf"), SkiaRenderingParsingOptions.Instance);
        document.AddSkiaPageFactory();
        using var layered = document.GetPage<SkiaLayeredPage>(1);
        var state = document.OptionalContent!;
        var viewOne = state.Groups.Single(g => g.Name == "GWG View 1");

        Assert.True(layered.HasOptionalContent);

        using var defaultView = layered.Compose(state);
        using var viewOneOn = layered.Compose(state.WithGroupState(viewOne, true));
        using var everything = layered.Compose(null);

        int width = (int)Math.Ceiling(layered.Bounds.Width * 2);
        int height = (int)Math.Ceiling(layered.Bounds.Height * 2);
        using var a = Rasterize(defaultView, width, height, 2);
        using var b = Rasterize(viewOneOn, width, height, 2);
        using var c = Rasterize(everything, width, height, 2);
        Assert.False(PdfToImageHelper.AreEquivalent(a, b));
        Assert.False(PdfToImageHelper.AreEquivalent(a, c));
        Assert.False(PdfToImageHelper.AreEquivalent(b, c));

        // Composing again with the first state gives the first picture again.
        using var again = layered.Compose(state);
        using var d = Rasterize(again, width, height, 2);
        Assert.True(PdfToImageHelper.AreEquivalent(a, d));
    }

    [Fact]
    public void Compose_HiddenClipText_StillClips()
    {
        // Same PDF as TestRendering.HiddenOptionalContentStillAddsToTextClip (hidden 7 Tr glyph, then a visible fill).
        byte[] pdf = TestRendering.BuildHiddenClipTextPdf();
        using var document = PdfDocument.Open(pdf, SkiaRenderingParsingOptions.Instance);
        document.AddSkiaPageFactory();

        using var direct = document.GetPageAsSKBitmap(1, 1, SKColors.White);
        using var layered = document.GetPage<SkiaLayeredPage>(1);
        using var hidden = layered.Compose(document.OptionalContent);
        using var shown = layered.Compose(null);
        using var hiddenBitmap = Rasterize(hidden, direct.Width, direct.Height, 1);
        using var shownBitmap = Rasterize(shown, direct.Width, direct.Height, 1);

        Assert.True(PdfToImageHelper.AreEquivalent(direct, hiddenBitmap));
        // Mode 7 paints nothing: showing the glyph's layer changes no pixel.
        Assert.True(PdfToImageHelper.AreEquivalent(direct, shownBitmap));
    }

    [Fact]
    public void Compose_StateFromAnotherDocument_Throws()
    {
        using var document = PdfDocument.Open(Path.Combine(Helper.DocumentsFolder, "GWG151_OptionalContent-RBGroup_X4.pdf"), SkiaRenderingParsingOptions.Instance);
        using var other = PdfDocument.Open(Path.Combine(Helper.DocumentsFolder, "GWG150_OptionalContent-OCCD_X4.pdf"), SkiaRenderingParsingOptions.Instance);
        document.AddSkiaPageFactory();
        using var layered = document.GetPage<SkiaLayeredPage>(1);
        Assert.True(layered.HasOptionalContent);

        int width = (int)Math.Ceiling(layered.Bounds.Width * 2);
        int height = (int)Math.Ceiling(layered.Bounds.Height * 2);
        using var expected = layered.Compose(document.OptionalContent);
        using var expectedBitmap = Rasterize(expected, width, height, 2);

        // Rejected in managed code, before Skia calls back into the conditional drawables.
        Assert.Throws<ArgumentException>(() => layered.Compose(other.OptionalContent));

        // The page is unharmed: a valid state still composes the same picture.
        using var again = layered.Compose(document.OptionalContent);
        using var againBitmap = Rasterize(again, width, height, 2);
        Assert.True(PdfToImageHelper.AreEquivalent(expectedBitmap, againBitmap));
    }

    [Fact]
    public void Compose_PictureOutlivesTheLayeredPage()
    {
        using var document = PdfDocument.Open(Path.Combine(Helper.DocumentsFolder, "GWG151_OptionalContent-RBGroup_X4.pdf"), SkiaRenderingParsingOptions.Instance);
        document.AddSkiaPageFactory();

        var layered = document.GetPage<SkiaLayeredPage>(1);
        int width = (int)Math.Ceiling(layered.Bounds.Width * 2);
        int height = (int)Math.Ceiling(layered.Bounds.Height * 2);
        using var composed = layered.Compose(null);
        using var before = Rasterize(composed, width, height, 2);

        // The composed picture holds its own references to the conditional sub-pictures.
        layered.Dispose();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        using var after = Rasterize(composed, width, height, 2);
        Assert.True(PdfToImageHelper.AreEquivalent(before, after));
        Assert.Throws<ObjectDisposedException>(() => layered.Compose(null));
    }

    [Fact]
    public void Compose_TransparencyGroupAndSoftMaskSpanningLayers_MatchesTheDirectRender()
    {
        // Two layers drawn (a) inside one transparency group form XObject painted with /Multiply (and /Screen
        // between them inside it) and (b) under one ExtGState /SMask, plus (c) a BDC /OC nested in another:
        // layer B (OFF by default) outside, layer A (ON) inside.
        byte[] defaultPdf = BuildLayersSpanningTransparencyPdf("[6 0 R]");
        byte[] bOnPdf = BuildLayersSpanningTransparencyPdf("[]");

        using var document = PdfDocument.Open(defaultPdf, SkiaRenderingParsingOptions.Instance);
        document.AddSkiaPageFactory();
        using var direct = document.GetPageAsSKBitmap(1, 2, SKColors.White);
        using var layered = document.GetPage<SkiaLayeredPage>(1);
        Assert.True(layered.HasOptionalContent);

        var state = document.OptionalContent!;
        using var composed = layered.Compose(state);
        using var composedBitmap = Rasterize(composed, direct.Width, direct.Height, 2);
        Assert.True(PdfToImageHelper.AreEquivalent(direct, composedBitmap));

        using var bOnDocument = PdfDocument.Open(bOnPdf, SkiaRenderingParsingOptions.Instance);
        bOnDocument.AddSkiaPageFactory();
        using var bOnDirect = bOnDocument.GetPageAsSKBitmap(1, 2, SKColors.White);
        Assert.False(PdfToImageHelper.AreEquivalent(direct, bOnDirect));

        var layerB = state.Groups.Single(g => g.Name == "B");
        using var bOnComposed = layered.Compose(state.WithGroupState(layerB, true));
        using var bOnComposedBitmap = Rasterize(bOnComposed, bOnDirect.Width, bOnDirect.Height, 2);
        Assert.True(PdfToImageHelper.AreEquivalent(bOnDirect, bOnComposedBitmap));
    }

    /// <summary>
    /// A one-page 300x300 PDF with layers A (5 0 R, ON) and B (6 0 R, OFF when listed in <paramref name="offList"/>)
    /// drawn inside a transparency group with blend modes, under a luminosity soft mask, and nested.
    /// </summary>
    private static byte[] BuildLayersSpanningTransparencyPdf(string offList)
    {
        const string content =
            "1 1 0 rg 0 150 300 150 re f\n" +
            "q /GSmul gs /Fm1 Do Q\n" +
            "q /GSmask gs\n" +
            "/OC /ocA BDC 0 1 0 rg 20 20 150 100 re f EMC\n" +
            "/OC /ocB BDC 1 0 1 rg 120 40 150 100 re f EMC\n" +
            "Q\n" +
            "/OC /ocB BDC /OC /ocA BDC 0 g 250 250 30 30 re f EMC EMC";

        const string group =
            "/OC /ocA BDC 1 0 0 rg 20 170 120 100 re f EMC\n" +
            "/GSscr gs\n" +
            "/OC /ocB BDC 0 0 1 rg 100 190 120 100 re f EMC";

        const string mask = "1 g 0 0 150 150 re f 0.4 g 150 0 150 150 re f";

        return BuildPdf(
            $"<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [5 0 R 6 0 R] /D << /OFF {offList} >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Resources << /Properties << /ocA 5 0 R /ocB 6 0 R >> /ExtGState << /GSmul 8 0 R /GSmask 9 0 R >> /XObject << /Fm1 7 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /OCG /Name (A) >>",
            "<< /Type /OCG /Name (B) >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 150 300 300] /Group << /S /Transparency >> /Resources << /Properties << /ocA 5 0 R /ocB 6 0 R >> /ExtGState << /GSscr 10 0 R >> >> /Length {group.Length} >>\nstream\n{group}\nendstream",
            "<< /Type /ExtGState /BM /Multiply >>",
            "<< /Type /ExtGState /SMask << /Type /Mask /S /Luminosity /G 11 0 R >> >>",
            "<< /Type /ExtGState /BM /Screen >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 300 150] /Group << /S /Transparency /CS /DeviceGray >> /Length {mask.Length} >>\nstream\n{mask}\nendstream");
    }

    /// <summary>
    /// A PDF made of <paramref name="objects"/>, numbered from 1; the first is the catalog.
    /// </summary>
    private static byte[] BuildPdf(params string[] objects)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            sb.Append($"{offset:D10} 00000 n \n");
        }

        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    public void LayeredPage_WithoutOptionalContent_HasNone()
    {
        using var document = PdfDocument.Open(Path.Combine(Helper.DocumentsFolder, "68-1990-01_A.pdf"), SkiaRenderingParsingOptions.Instance);
        document.AddSkiaPageFactory();
        using var layered = document.GetPage<SkiaLayeredPage>(1);

        Assert.False(layered.HasOptionalContent);
    }

    private static SKBitmap Rasterize(SKPicture picture, int width, int height, float scale)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        canvas.Scale(scale);
        canvas.DrawPicture(picture);
        return bitmap;
    }

    private static SKColor Pixel(SKPicture picture, int x, int y)
    {
        using var bitmap = new SKBitmap(10, 10);
        using (var c = new SKCanvas(bitmap))
        {
            c.Clear(SKColors.Transparent);
            c.DrawPicture(picture);
        }

        return bitmap.GetPixel(x, y);
    }
}
