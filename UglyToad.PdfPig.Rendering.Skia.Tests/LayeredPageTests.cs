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
using System.Linq;
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
