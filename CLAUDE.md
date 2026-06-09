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

#### Image-regression tests (`PdfPigSkiaTest`)

The bulk of the suite renders a PDF page and compares it pixel-by-pixel against a committed golden PNG. Things to know before touching the renderer:

- **Must run in Release.** `PdfPigSkiaTest` `throw`s in `#if DEBUG` — image hinting/quality differs, so golden images are only valid in Release. Run `dotnet test … -c Release`. (The pure unit tests — `ParametricShadingTextureTests`, `MeshShadingDisposalTests` — run in any config.)
- **Tolerance, not exact match.** `PdfToImageHelper` allows a per-channel delta of `_threshold = 2` and up to `_maxDifferingPixelRatio = 0.001` (0.1 %) of pixels to differ, absorbing cross-platform AA/sub-pixel jitter. A failing comparison writes a diff PNG to `ErrorImages/`.
- **Golden images are committed** under `ExpectedImages/pdfpig_skia/`, with optional per-OS overrides in `ExpectedImages/{windows,linux,macos}/` (the OS-specific file wins when present, else the default is used). **Any intentional change to rendering output (e.g. tessellation, AA) means the affected goldens must be regenerated** — a green diff is not automatic.
- **Iterate fast:** add `-f net8.0` to run one TFM instead of the full matrix, and `--filter "DisplayName~<pdfname>"` to target a single document/page (e.g. `DisplayName~0000851`).

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
  - `.Shading.cs` — shared shading infrastructure + dispatch (`PaintShading`, `RenderShadingPattern`, the mesh-picture cache, the bit-stream reader, patch tessellation/texture helpers). Per-type rendering lives in sibling partials: `.Shading.Axial.cs`, `.Shading.Radial.cs`, `.Shading.Function.cs`, `.Shading.GouraudFree.cs` (Type 4), `.Shading.GouraudLattice.cs` (Type 5), `.Shading.Coons.cs` (Type 6), `.Shading.Tensor.cs` (Type 7).
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

### Mesh Shading (Coons Type 6 / Tensor Type 7)

A page often paints the same mesh shading many times (a chart re-invoking `sh`), and tessellating ~1.4 K patches per paint is what makes such pages take seconds. `SkiaStreamProcessor.Shading.cs` caches the tessellated mesh as an `SKPicture` in `_meshPictureCache` and replays it. Two invariants make this both fast and correct — **do not break them**:

- **The cache is intentionally CTM-independent.** The mesh geometry is recorded in *pattern space* (control points mapped only by the pattern transform) and the canvas CTM is applied at replay. The key is `(Shading, pattern-space transform, alpha, blend)` — **not** the device/canvas CTM. For `sh` the pattern transform is always identity, so every `sh` of one shading shares a single picture regardless of CTM. ⚠️ Keying on the CTM, or scaling `ComputePatchSubdivisions` by the device scale, turns every differently-scaled paint into a cache miss (re-tessellate every time) and saturates subdivision back toward the full 32×32 grid — both reintroduce the multi-second-per-page slowness this cache exists to kill. `PatchCellSize` is deliberately measured in pattern-space units. Alpha/blend *are* in the key (baked into recorded colours/paint, rarely vary per paint). Verify any change here with `dotnet test … -c Release --filter "DisplayName~0000851"` (≈5 s, not minutes).

- **Disposal relies on Skia native ref-counting.** `Cleanup()` (in `Process()`'s `finally`) disposes the cached mesh pictures, and the textured-patch path disposes its image/shader/paint mid-recording. This is safe only because the parent page picture (and the recorded `DrawVertices`/`DrawPicture` ops) hold their own native refs after `EndRecording()` — disposing the managed wrappers just drops *our* ref. `MeshShadingDisposalTests` enforces this (rasterises the page picture after disposal; asserts non-blank + stable replay). Don't dispose mesh resources before `EndRecording`, and don't assume "reasoned safe" without that test.

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

- **Text clip modes** (`FillClip`, `StrokeClip`, etc.): operator is recognised but clipping is not applied.
- **Image mask alpha**: ignores `colour.Alpha` (hardcoded to 255) in `SkiaStreamProcessor.Image.cs`.
- **Thread safety**: `SkiaFontCache` mutates its list under a read lock, not a write lock.
