# PdfPig.Rendering.Skia

Render pdf documents as images with `PdfPig` using `SkiaSharp`.

**This is a very early version and not everything is supported (more to come).**

Available as a *Prerelease* Nuget package https://www.nuget.org/packages/PdfPig.Rendering.Skia/

Uses parts of [PDFBox](https://github.com/apache/pdfbox) code.

## How to use
### Save pages as image to disk
```csharp
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Rendering.Skia;
using SkiaSharp;

[...]

using (var document = PdfDocument.Open(_path))
{
	string fileName = Path.GetFileName(_path);

	document.AddSkiaPageFactory(); // Same as document.AddPageFactory<SKPicture, SkiaPageFactory>()

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

using (var document = PdfDocument.Open(_path))
{
	document.AddSkiaPageFactory(); // Same as document.AddPageFactory<SKPicture, SkiaPageFactory>()

	for (int p = 1; p <= document.NumberOfPages; p++)
	{
		var bitmap = document.GetPageAsSKBitmap(p, _scale, RGBColor.White);
		// Use the SKBitmap
	}
}
```

### Get the `SKPicture` from a page
```csharp
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Rendering.Skia;
using SkiaSharp;

[...]

using (var document = PdfDocument.Open(_path))
{
	document.AddSkiaPageFactory(); // Same as document.AddPageFactory<SKPicture, SkiaPageFactory>()

	for (int p = 1; p <= document.NumberOfPages; p++)
	{
		var picture = document.GetPage<SKPicture>(p);
		// Use the SKPicture
	}
}
```
