# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository layout

Three projects in `UglyToad.PdfPig.Rendering.Skia.sln`:

| Project | Purpose |
|---|---|
| `UglyToad.PdfPig.Rendering.Skia` | The library. Multi-targets `netstandard2.0;netstandard2.1;net462;net471;net6.0;net8.0;net9.0`, ships as NuGet `PdfPig.Rendering.Skia`. |
| `UglyToad.PdfPig.Rendering.Skia.Tests` | xUnit v3 / MTP test app. `net472;net8.0;net10.0`. |
| `UglyToad.PdfPig.Rendering.Skia.Benchmarks` | BenchmarkDotNet console app (`net8.0`, `PublishAot`). |

`global.json` pins SDK `10.0.100` (`rollForward: latestMajor`) and sets `"test": { "runner": "Microsoft.Testing.Platform" }` — that is why `dotnet test` accepts MTP arguments (`--filter-class`, `--report-trx`, …) instead of VSTest ones.

The library multi-targets down to `netstandard2.0` / `net462`: new code must compile on all of them (`LangVersion` 12, `Microsoft.Bcl.HashCode` polyfill on the old TFMs, `#if NET` guards for modern-only APIs).

## Commands

### Build
```bash
dotnet build UglyToad.PdfPig.Rendering.Skia.sln
# Or just the main library:
dotnet build UglyToad.PdfPig.Rendering.Skia/UglyToad.PdfPig.Rendering.Skia.csproj -c Release
```

### Test
```bash
# All tests (fans out over net472 + net8.0 + net10.0, run in parallel)
dotnet test UglyToad.PdfPig.Rendering.Skia.sln

# Single test class or method
dotnet test --filter "FullyQualifiedName~ClassName" UglyToad.PdfPig.Rendering.Skia.sln
dotnet test --filter-class "*ClassName*" UglyToad.PdfPig.Rendering.Skia.sln
dotnet test --filter-method "*ClassName.MethodName*" UglyToad.PdfPig.Rendering.Skia.sln

# The test project is a self-contained MTP app - run it directly, no `dotnet test` needed
./UglyToad.PdfPig.Rendering.Skia.Tests/bin/Release/net10.0/UglyToad.PdfPig.Rendering.Skia.Tests.exe --help
```

### Benchmarks
```bash
dotnet run -c Release --project UglyToad.PdfPig.Rendering.Skia.Benchmarks
```
`Program.Main` runs `ShadingBenchmarks` (edit it to run `RenderPagesBenchmarks` instead). `NuGetPackageConfig` A/B-tests **Local** (this working tree, via `ProjectReference`) against **Latest** (the published NuGet, the baseline) by passing `/p:PdfPigSkiaVersion=Local|Latest` — use it to prove a perf change against the shipped version. Its `InvocationCount` is deliberately low for iteration; raise it (see the comment in the file) for a real measurement.

## Test suite

Test framework: **xUnit.net v3** (`xunit.v3` 4.x) on **Microsoft.Testing.Platform** (MTP).

Assets are read from the **source tree**, not the build output — `Helper` resolves `Documents/`, `ExpectedImages/` and `SpecificTestDocuments/` relative to `AppDomain.CurrentDomain.BaseDirectory/../../../`, and the PDFs are `CopyToOutputDirectory=Never`. Copying the built test exe elsewhere therefore breaks every asset-backed test.

| Test class | What it covers |
|---|---|
| `TestRendering.PdfPigSkiaTest` | The bulk of the suite: golden-image regression (see below). |
| `VisualTests.RenderToFolder` | Renders **every** PDF in `Documents/` (minus an ignore list) to `Output/` at 2×. No assertions — a crash smoke test plus eyeball output. |
| `PageSizeTests` | `PageSizeFactory` dimensions across all documents. |
| `RenderImagesTests` | The image-*extraction* API (`page.GetImages()` → `GetSKBitmap()`). |
| `CancellationTests` | `CancellationToken` honoured mid-render and before start (uses `SpecificTestDocuments/`). |
| `ConcurrencyTests` | Parallel renders must be byte-identical — guards `SkiaFontCache`. |
| `MeshShadingDisposalTests` | Mesh pictures stay valid after `Cleanup()` disposal. |
| `ParametricShadingTextureTests` | Pure unit tests for the shading texture. |
| `GitHubIssues` | Regressions for reported issues. |

`VisualTests` / `PageSizeTests` enumerate `Documents/` automatically, so dropping a PDF there adds it to those theories. The image-regression cases are an **explicit** `TheoryData` list (`TestRendering.DocumentsPdfPig`) — a new regression case needs both an entry there and a committed golden.

> **Binary test assets must stay binary in git.** `.gitattributes` has `* text=auto`, but uncompressed PDFs contain no NUL bytes, so git's heuristic misdetects them as *text* and strips CR bytes on storage. A PDF's xref uses absolute byte offsets, so dropping CRs shifts the layout and breaks them: the file checks out fine on Windows (`autocrlf` restores CRLF) but corrupt on Linux/macOS (LF), giving `IndexOutOfRangeException` in `MemoryInputBytes.Seek` / `PdfTokenScanner.TryReadStream`. The bug is byte-based, not OS-based — feeding the LF blob to the renderer fails on any OS. `.gitattributes` therefore force-marks `*.pdf` (and `*.png/jpg/jpeg/gif/ico/snk`) as `binary`. After adding a new test PDF, confirm `git check-attr -a <file>` reports `binary: set`; if it was committed before the rule, run `git add --renormalize .`.

### Image-regression tests (`PdfPigSkiaTest`)

Renders a PDF page and compares it pixel-by-pixel against a committed golden PNG. Things to know before touching the renderer:

- **Must run in Release.** Image hinting/quality differs under DEBUG, so the golden images are only valid in Release. `PdfPigSkiaTest` is gated with `[Theory(SkipUnless = nameof(IsReleaseBuild), Skip = …)]`, so a DEBUG run reports every image case as *skipped* with a reason instead of a wall of failures — it is not a green run. Run `dotnet test … -c Release`. (The pure unit tests — `ParametricShadingTextureTests`, `MeshShadingDisposalTests` — run in any config.)
- **Tolerance, not exact match.** `PdfToImageHelper` allows a per-channel delta of `Threshold = 2` and up to `MaxDifferingPixelRatio = 0.001` (0.1 %) of pixels to differ, absorbing cross-platform AA/sub-pixel jitter. A failing comparison writes a diff PNG to `ErrorImages/`.
- **Golden images are committed** under `ExpectedImages/pdfpig_skia/`, with optional per-OS overrides in `ExpectedImages/{windows,linux,macos}/` (the OS-specific file wins when present, else the default is used; arm64 and x64 share one folder). Overrides exist because rendering is **font-dependent**: when a glyph has no usable embedded outline the renderer falls back to a system font, and the installed fonts and their substitutions differ per OS/machine. **Any intentional change to rendering output (e.g. tessellation, AA) means the affected goldens must be regenerated** — a green diff is not automatic.
- **Iterate fast:** add `-f net8.0` to run one TFM instead of the full matrix, and `--filter-display-name "*<pdfname>*"` to target a single document/page (e.g. `--filter-display-name "*0000851*"`). The VSTest form `--filter "DisplayName~0000851"` still works.

## CI

`.github/workflows/unit-tests.yml` is a **reusable** workflow; `unit-tests-{windows,linux,macos}.yml` are thin callers that each pass a runner/arch/framework matrix, so each OS gets its own status badge. The legs are net472 (Windows x64 only) plus net8.0 and net10.0 on x64 and arm64.

Each job installs the DejaVu / Liberation / Noto (incl. CJK) families before testing. That font set is part of the contract with the committed goldens — changing it, or changing fallback in `SkiaFontCache`, can move images on CI without moving them locally.

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

Rendering is **cancellable** via the `GetPageAsSKPicture(document, pageNumber, cancellationToken)` overload only. PdfPig's `GetPage<T>` signature has no token, so the token is smuggled to the in-flight render through an `AsyncLocal<CancellationToken>` on `SkiaPageFactory` (`CurrentToken`), saved/restored around the call; the processor checks it every 100 content-stream operators — top-level stream, form XObjects, soft masks, tiling patterns — and throws `OperationCanceledException` (`CancellationTests`). The call remains **synchronous and blocking** despite the token.

**Concurrency contract:** rendering two pages of the *same* `PdfDocument` in parallel is unsupported (PdfPig shares document-scoped token scanner / resource store / xref, and rendering mutates per-document graphics state) — serialise per document. Different `PdfDocument` instances in parallel are safe, and `ConcurrencyTests` enforces that they stay pixel-identical.

### Key Classes

- **`PdfPigExtensions`** — Public API surface: `AddSkiaPageFactory()`, `GetPageAsSKBitmap()`, `GetPageAsPng()`, `GetPageAsSKPicture()`, `GetPageSize()`. `Helpers/SkiaImageExtensions` adds the separate image-*extraction* API (`IPdfImage.GetSKBitmap()`).

- **`SkiaPageFactory`** — Implements `IPageFactory<SKPicture>`. Creates one `SkiaStreamProcessor` per page. Owns the document-scoped `SkiaFontCache`.

- **`SkiaStreamProcessor`** — Core rendering engine (internal, split across partial files by content type). Wraps a `SKPictureRecorder`; processes the PDF content stream and dispatches drawing calls.
  - `.Glyph.cs` — text/glyph rendering via HarfBuzz
  - `.Path.cs` — fill/stroke path operations
  - `.Image.cs` — image and image-mask rendering
  - `.SoftMask.cs` — `/SMask` support. Renders the mask's transparency group to an offscreen `SKImage` at 2× page resolution (keeps luminosity edges sharp when the picture is later replayed at higher DPI), pre-cleared with the `/Luminosity` backdrop colour `BC`, then applies it with `DstIn` + `SKColorFilter.CreateLumaColor`. Per PDF 1.7 §11.6.5.2 a graphics-state soft mask applies to *every* painting operation while in scope, not just to whole form groups — hence both a form-level path and a per-paint wrapper.
  - `.Shading.cs` — shared shading infrastructure: the single `RenderShading` dispatch (serves both the `sh` operator and shading patterns; stroke patterns use the *stroking* alpha constant), the BBox-clip / Background / shader-draw / tile-mode helpers shared by the Axial/Radial/Function renderers, the mesh-picture cache, the bit-stream reader, the shared Type 6/7 stream-reading driver (`DrawPatchMeshUnclipped` — the record layout and edge-continuation rules are identical for Coons and Tensor), patch tessellation/texture helpers, `MapPointAffine`, and the static per-subdivision index/texcoord caches. Per-type rendering (samplers/tessellators) lives in sibling partials: `.Shading.Axial.cs`, `.Shading.Radial.cs`, `.Shading.Function.cs`, `.Shading.GouraudFree.cs` (Type 4), `.Shading.GouraudLattice.cs` (Type 5), `.Shading.Coons.cs` (Type 6), `.Shading.Tensor.cs` (Type 7). Review status and remaining known shading issues: `docs/SHADING-CODE-REVIEW.md`.
  - `.Annotations.cs` / `.Annotations.Text.cs` — annotation rendering; the latter generates the built-in `/Text` annotation icons.

- **`PageSizeFactory`** — Lightweight `IPageFactory<PdfPageSize>` that extracts page dimensions without full rendering (handles MediaBox, CropBox, rotation, UserUnit).

- **`PatternAwareColorSpaceContext`** — Decorator over PdfPig's `IColorSpaceContext` that captures the colour operands supplied alongside a pattern name in `SCN`/`scn`. PdfPig's default context drops them; **uncoloured tiling patterns need them** to compute the underlying-space colour to paint with.

- **`SkiaRenderingFilterProvider`** — Filter provider wired up by `SkiaRenderingParsingOptions`, adding the DCT / JBIG2 / JPX decoders that ship as separate `PdfPig.Filters.*` packages.

- **`SKPaintCache`** — Page-scoped cache for `SKPaint` objects keyed by a property hash. Disposed after each page.

- **`SkiaFontCache`** — Document-scoped cache for typefaces, glyph paths and Type 3 glyphs (`.Font.cs` / `.Glyph.cs` / `.Type3.cs`). Shared across all pages of a document. See Thread Safety below.

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

- **Mesh geometry is drawn as *indexed* triangle lists with exact-size vertex arrays.** The `UInt16[]` index buffers and texture-coordinate grids depend only on the subdivision count and are cached process-wide as statics (`GetGridTriangleIndices`, `GetPatchTexCoords`, `GetGouraudSubTriangleIndices`) — deterministic content, benign-race publish, so parallel renders stay byte-identical. ⚠️ SkiaSharp's `DrawVertices` takes vertex/index counts from `Array.Length`: never pass an oversized or pooled scratch buffer as vertices, it would copy stale entries into the recorded picture.

- **Disposal relies on Skia native ref-counting.** `Cleanup()` (in `Process()`'s `finally`) disposes the cached mesh pictures, and the textured-patch path disposes its image/shader/paint mid-recording. This is safe only because the parent page picture (and the recorded `DrawVertices`/`DrawPicture` ops) hold their own native refs after `EndRecording()` — disposing the managed wrappers just drops *our* ref. `MeshShadingDisposalTests` enforces this (rasterises the page picture after disposal; asserts non-blank + stable replay). Don't dispose mesh resources before `EndRecording`, and don't assume "reasoned safe" without that test.

### Thread Safety (`SkiaFontCache`)

Font *fallback resolution* is serialised process-wide behind the static `FontManagerLock`. This is load-bearing, not an optimisation: `SKFontManager.Default` is the process-global `SkFontMgr::RefDefault()` singleton, and on macOS the CoreText-backed manager resolves character fallback **non-deterministically under concurrent access** — two threads matching the same CJK codepoint can get different (near-identical) system Han fonts, which manifested as intermittent macOS CI image-diff failures (`Rotation 45`, `Page_28`, `VerticalText`). Within the lock, resolve + publish is atomic (double-checked), and the per-key `List<SkiaFontCacheItem>` is only read/mutated under its own list lock. Lock order: `FontManagerLock` → list lock. **Disposal is render-safe**: `SkiaPageFactory.ProcessPage` brackets each page render in `BeginUse()`/`EndUse()` (a bare `Interlocked` counter, one pair per page); `Dispose()` atomically sets the disposed flag — refusing new renders — then spin-waits until in-flight renders reach zero before tearing down natives. A hung render therefore blocks `Dispose`. `_sortedFontFamilies` is static (process-wide font set, only touched under `FontManagerLock`); never dispose `SKFontManager.Default`; `DefaultSkiaFontCacheItem` must stay per-instance (its `SKShaper` *is* disposed per-document). `ConcurrencyTests` renders the affected documents in parallel and requires byte-identical pixels — keep it green when touching this class.

### Configuration

`SkiaRenderingParsingOptions.Instance` is a singleton that enables lenient parsing, skips missing fonts, and wires up the custom `SkiaRenderingFilterProvider` for decompression filters.

## Code Conventions

`.editorconfig` is the authority (CRLF, 4-space indent, no final newline, `file_header_template`). Notably:

- **No `var` for primitives** — use explicit types.
- **`#nullable enable`** throughout — null propagation (`?.`, `??`) preferred over null checks.
- **Accessibility modifiers required** on all members.
- **File headers** — Apache 2.0 license comment block required on every source file.
- **Pattern matching** preferred over `is`/cast combos.
- Assembly is strong-name signed with `UglyToad.PdfPig.Rendering.Skia.snk`.
- Parts of the rendering logic are ported from [PDFBox](https://github.com/apache/pdfbox) — keep the attribution comments when adapting more of it.

## Known Limitations

- **Text clip modes** (`FillClip`, `StrokeClip`, etc.): operator is recognised but clipping is not applied.
- **Image mask alpha**: ignores `colour.Alpha` (hardcoded to 255) in `SkiaStreamProcessor.Image.cs`.
- **Mesh shadings (Types 4–7) as stroke patterns**: clipped to the path's fill interior, not the stroke outline (`isStroke` is not plumbed into the mesh renderers). See `docs/SHADING-CODE-REVIEW.md` #4.
