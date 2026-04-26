# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

### Build
```bash
dotnet build UglyToad.PdfPig.Rendering.Skia.sln
# Or just the main library:
dotnet build UglyToad.PdfPig.Rendering.Skia/UglyToad.PdfPig.Rendering.Skia.csproj -c Release
```

### Test
```bash
# All tests
dotnet test UglyToad.PdfPig.Rendering.Skia.sln

# Single test class or method
dotnet test --filter "FullyQualifiedName~ClassName" UglyToad.PdfPig.Rendering.Skia.sln
dotnet test --filter "FullyQualifiedName~ClassName.MethodName" UglyToad.PdfPig.Rendering.Skia.sln
```

Test framework: **xUnit**. Test PDFs live in `UglyToad.PdfPig.Rendering.Skia.Tests/Documents/`.

## Architecture

### Rendering Pipeline

```
PdfDocument.Open(path, SkiaRenderingParsingOptions.Instance)
  → AddSkiaPageFactory()           registers SkiaPageFactory + PageSizeFactory
  → document.GetPage<SKPicture>(n) → SkiaPageFactory.ProcessPage()
      → SkiaStreamProcessor.Process()
          → SKPictureRecorder records canvas operations
          → returns SKPicture (vector)
  → PdfPigExtensions helpers rasterize to SKBitmap / PNG
```

### Key Classes

- **`PdfPigExtensions`** — Public API surface: `AddSkiaPageFactory()`, `GetPageAsSKBitmap()`, `GetPageAsPng()`, `GetPageAsSKPicture()`, `GetPageSize()`.

- **`SkiaPageFactory`** — Implements `IPageFactory<SKPicture>`. Creates one `SkiaStreamProcessor` per page. Owns the document-scoped `SkiaFontCache`.

- **`SkiaStreamProcessor`** — Core rendering engine (internal, split across partial files by content type). Wraps a `SKPictureRecorder`; processes the PDF content stream and dispatches drawing calls.
  - `.Glyph.cs` — text/glyph rendering via HarfBuzz
  - `.Path.cs` — fill/stroke path operations
  - `.Image.cs` — image and image-mask rendering
  - `.Shading.cs` — gradient and mesh shading
  - `.Annotations.cs` — annotation rendering

- **`PageSizeFactory`** — Lightweight `IPageFactory<PdfPageSize>` that extracts page dimensions without full rendering (handles MediaBox, CropBox, rotation, UserUnit).

- **`SKPaintCache`** — Page-scoped cache for `SKPaint` objects keyed by a property hash. Disposed after each page.

- **`SkiaFontCache`** — Document-scoped cache for typefaces and glyph paths. Shared across all pages of a document.

### Coordinate System

PDF has origin at bottom-left; Skia has origin at top-left. The Y-axis is flipped at the start of each page:
```csharp
SKMatrix.CreateScale(1, -1, 0, _height / 2f)
```
All subsequent transforms are concatenated on top of this base matrix.

### Graphics State & Transparency

State is stack-based (inherited from PdfPig's `BaseStreamProcessor`). Transparency groups use `SaveLayer()` with a pending paint; regular state push/pop uses `Save()`/`Restore()`. Layer paints are disposed in `PopState`.

### Configuration

`SkiaRenderingParsingOptions.Instance` is a singleton that enables lenient parsing, skips missing fonts, and wires up the custom `SkiaRenderingFilterProvider` for decompression filters.

## Code Conventions

- **No `var` for primitives** — use explicit types.
- **`#nullable enable`** throughout — null propagation (`?.`, `??`) preferred over null checks.
- **Accessibility modifiers required** on all members.
- **File headers** — Apache 2.0 license comment block required on every source file.
- **Pattern matching** preferred over `is`/cast combos.
- Assembly is strong-name signed with `UglyToad.PdfPig.Rendering.Skia.snk`.

## Known Limitations

- **Uncoloured tiling patterns**: currently a no-op (returns early).
- **Gouraud / Coons / Tensor-product mesh shadings**: no-op in Release builds.
- **Text clip modes** (`FillClip`, `StrokeClip`, etc.): operator is recognised but clipping is not applied.
- **Image mask alpha**: ignores `colour.Alpha` (hardcoded to 255) in `SkiaStreamProcessor.Image.cs`.
- **Thread safety**: `SkiaFontCache` mutates its list under a read lock, not a write lock.
