# PdfPig.Rendering.Skia

[![Windows](https://github.com/BobLd/PdfPig.Rendering.Skia/actions/workflows/unit-tests-windows.yml/badge.svg)](https://github.com/BobLd/PdfPig.Rendering.Skia/actions/workflows/unit-tests-windows.yml)
[![Linux](https://github.com/BobLd/PdfPig.Rendering.Skia/actions/workflows/unit-tests-linux.yml/badge.svg)](https://github.com/BobLd/PdfPig.Rendering.Skia/actions/workflows/unit-tests-linux.yml)
[![macOS](https://github.com/BobLd/PdfPig.Rendering.Skia/actions/workflows/unit-tests-macos.yml/badge.svg)](https://github.com/BobLd/PdfPig.Rendering.Skia/actions/workflows/unit-tests-macos.yml)

Cross-platform library to render pdf documents as images with `PdfPig` using `SkiaSharp`, or to extract images contained in a pdf page as `SkiaSharp` images.

Available as a Nuget package https://www.nuget.org/packages/PdfPig.Rendering.Skia/

Uses parts of [PDFBox](https://github.com/apache/pdfbox) code.

## How to Render pages as images
### Save pages as image to disk
```csharp
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Rendering.Skia;
using SkiaSharp;

[...]

using (var document = PdfDocument.Open(_path, SkiaRenderingParsingOptions.Instance))
{
	string fileName = Path.GetFileName(_path);

	document.AddSkiaPageFactory(); // Same as document.AddPageFactory<SKPicture, SkiaPageFactory>() and document.AddPageFactory<PdfPageSize, PageSizeFactory>()

	for (int p = 1; p <= document.NumberOfPages; p++)
	{
		using (var fs = new FileStream($"{fileName}_{p}.png", FileMode.Create))
		using (var ms = document.GetPageAsPng(p, _scale, RGBColor.White))
		{
			ms.WriteTo(fs);
		}
	}
}
```

### Get the `SKBitmap` from a page
```csharp
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Rendering.Skia;
using SkiaSharp;

[...]

using (var document = PdfDocument.Open(_path, SkiaRenderingParsingOptions.Instance))
{
	document.AddSkiaPageFactory(); // Same as document.AddPageFactory<SKPicture, SkiaPageFactory>() and document.AddPageFactory<PdfPageSize, PageSizeFactory>()

	for (int p = 1; p <= document.NumberOfPages; p++)
	{
		using var skBitmap = document.GetPageAsSKBitmap(p, _scale, RGBColor.White);
		// Use the SKBitmap
	}
}
```

### Get the `SKPicture` from a page
Starting from version `0.1.14.1`, the `SKPicture.CullRect` cannot be used anymore to get the page size.
```csharp
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Rendering.Skia;
using SkiaSharp;

[...]

using (var document = PdfDocument.Open(_path, SkiaRenderingParsingOptions.Instance))
{
	document.AddSkiaPageFactory(); // Same as document.AddPageFactory<SKPicture, SkiaPageFactory>() and document.AddPageFactory<PdfPageSize, PageSizeFactory>()

	for (int p = 1; p <= document.NumberOfPages; p++)
	{
		using var skPicture = document.GetPage<SKPicture>(p);
		// Use the SKPicture
	}
}
```

### Consistent rendering of non-embedded standard fonts (opt-in)
When a PDF uses one of the 14 standard fonts (Helvetica, Times, Courier, Symbol, ZapfDingbats and their
variants, plus names mapped to them such as `Arial`) **without embedding it**, the text is drawn with a font
installed on the machine. The result therefore differs between Windows, Linux and macOS, and Symbol /
ZapfDingbats glyphs (check marks, bullets) can be missing.

To render those fonts the same everywhere, register the bundled substitute fonts (Liberation and Foxit, shipped
in the [`Caly.PdfPig.Fonts`](https://www.nuget.org/packages/Caly.PdfPig.Fonts) package, which you reference yourself)
**once, at application startup, before opening any document**:
```csharp
Caly.PdfPig.Fonts.StandardFontSubstitutes.Register();
```
This is opt-in: this library does not reference that package and nothing registers them for you. Be aware that
the registration is **process-wide**: it uses PdfPig's `SystemFontFinder.AddSubstituteFont`, so it also applies to PdfPig text extraction in the same process,
and it replaces any substitute your application registered earlier for the same font names. Fonts already loaded
by an open document are not affected.

## How to extract images contained in a pdf page
```csharp
using UglyToad.PdfPig.Rendering.Skia.Helpers;

[...]

using (var document = PdfDocument.Open(_path, SkiaRenderingParsingOptions.Instance))
{
	for (int p = 1; p <= document.NumberOfPages; p++)
	{
		var page = document.GetPage(p);
		foreach (var pdfImage in page.GetImages())
		{
			using var skBitmap = pdfImage.GetSKBitmap();			
			// Use SKBitmap

			// In order to get the SKImage, use the following:
			using var skImage = SKImage.FromBitmap(skBitmap);
			// Use SKImage
		}
	}
}
```

## Testing
The visual regression tests (`TestRendering.PdfPigSkiaTest`) compare the rendered output against expected
images and must be run in **Release** mode.

> [!IMPORTANT]
> Expected images are **platform specific**: Windows, Linux and macOS do not produce identical output.
> The differences are font related. When a glyph has no usable embedded outline, rendering falls back to a
> system font, and the available installed fonts (and their substitutions) differ from one OS / machine to another.
> As a result, an expected image baseline generated on one OS will not match pixel-for-pixel on another.
> The test assembly registers the bundled standard-font substitutes (`StandardFontSubstitutesFixture`), so
> non-embedded standard 14 fonts no longer cause such differences; other non-embedded fonts still do.
