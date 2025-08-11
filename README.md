# PdfPig.Rendering.Skia

Cross-platform library to render pdf documents as images with `PdfPig` using `SkiaSharp`, or to extract images contained in a pdf page as `SkiaSharp` images.

> [!IMPORTANT]
> **This is a very early version and the code is constantly evolving.**

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

using (var document = PdfDocument.Open(_path, SkiaRenderingParsingOptions.Instance))
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

using (var document = PdfDocument.Open(_path, SkiaRenderingParsingOptions.Instance))
{
	document.AddSkiaPageFactory(); // Same as document.AddPageFactory<SKPicture, SkiaPageFactory>()

	for (int p = 1; p <= document.NumberOfPages; p++)
	{
		var picture = document.GetPage<SKPicture>(p);
		// Use the SKPicture
	}
}
```
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
			var skImage = pdfImage.GetSKImage();			
			// Use SKImage
		}
	}
}
```
