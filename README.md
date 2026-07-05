# MIR.Direct2D1ForAvalonia

Independent derivative of the deprecated Avalonia Direct2D1 backend, updated for Avalonia `12.0.0`.

This project is not affiliated with or endorsed by AvaloniaUI OÜ.

`UseDirect2D1()` is separated from text shaping:

- Rendering backend: Direct2D1
- Text shaping: choose explicitly with `UseHarfBuzz()` or `UseDirectWrite()`

Example:

```csharp
using MIR.Direct2D1ForAvalonia;

AppBuilder.Configure<App>()
    .UseWin32()
    .UseDirect2D1()
    .UseHarfBuzz();
```

To switch shaping to DirectWrite:

```csharp
using MIR.Direct2D1ForAvalonia;
using MIR.DirectWriteForAvalonia;

AppBuilder.Configure<App>()
    .UseWin32()
    .UseDirect2D1()
    .UseDirectWrite();
```

## Known limitations

- **Windows only.** Direct2D1/DirectWrite are not available on other platforms.
- **Window surfaces.** `UseDirect2D1()` renders via `IDirect3D11TexturePlatformSurface` (the composition path Avalonia lights up through WinUI Composition / DirectComposition) or an external Direct2D render target (`IExternalDirect2DRenderTargetSurface`). Bare-HWND swap chains and the software framebuffer fallback are not supported and will throw `NotSupportedException` at render-target creation.
- **Bitmap formats.** `Bitmap.Save` accepts `.png`, `.jpg/.jpeg`, `.bmp`, `.tif/.tiff`, `.gif`, and `.webp` via the file extension. The optional `quality` parameter is applied to JPEG output through WIC's `ImageQuality` encoder option.
- **Path segmenting.** `Geometry.TryGetSegment` is supported through a Direct2D length-sampled polyline approximation. It preserves path trimming behavior, but returned curve segments are flattened rather than retaining their original Bezier/arc commands.
- **3D transforms.** Only the 2D `Transform` (Matrix) is honored. There is no per-context 4x4/perspective transform.
- **Thread affinity.** The Direct2D device context is thread-affine. The standard single-render-thread Avalonia model is fine; the `EnsureCurrent()` hook does not currently marshal or assert thread ownership.

Licensing:

- This repository is distributed under the MIT license. See `LICENSE`.
- Avalonia-derived source lineage and notices are documented in `THIRD_PARTY_NOTICES.md`.

Packaging:

- Current package version baseline is `12.0.0`.
- Pack with `dotnet pack src/Direct2D1ForAvalonia/MIR.Direct2D1ForAvalonia.csproj -c Release`
- Pack with `dotnet pack src/DirectWriteForAvalonia/MIR.DirectWriteForAvalonia.csproj -c Release`
- Both packages embed `README.md`, `LICENSE`, and `THIRD_PARTY_NOTICES.md`.

Validation:

- Fast local check: `pwsh scripts/validate.ps1`
- Include real window screenshot smoke: `pwsh scripts/validate.ps1 -RunWindowSmoke`
- Include Skia/D2D render parity smoke: `pwsh scripts/validate.ps1 -RunRenderParity`
- Include benchmark smoke: `pwsh scripts/validate.ps1 -RunBenchmarks`
