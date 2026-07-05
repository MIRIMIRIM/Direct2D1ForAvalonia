# MIR.Direct2D1ForAvalonia

Independent derivative of the deprecated Avalonia Direct2D1 backend, updated for Avalonia `12.0.0`.

This project is not affiliated with or endorsed by AvaloniaUI OÜ.

`UseDirect2D1()` is separated from text shaping:

- Rendering backend: Direct2D1
- Font backend: shared DirectWrite-backed font/typeface provider
- Text shaping: choose explicitly with `UseHarfBuzz()` or `UseDirectWrite()`

Package choice is explicit:

- DirectWrite shaping: reference `MIR.DirectWriteForAvalonia` and call `UseDirectWrite()`.
- HarfBuzz shaping: reference `Avalonia.HarfBuzz` and call `UseHarfBuzz()`.

`MIR.DirectWriteFontsForAvalonia` is a narrow shared dependency package. Applications normally get it transitively through `MIR.Direct2D1ForAvalonia` or `MIR.DirectWriteForAvalonia`; it supplies the Windows font backend that both shapers consume through Avalonia's `IPlatformTypeface` APIs.

Example:

```csharp
using MIR.Direct2D1ForAvalonia;
using MIR.DirectWriteForAvalonia;

AppBuilder.Configure<App>()
    .UseWin32()
    .UseDirect2D1()
    .UseDirectWrite();
```

To keep Avalonia's HarfBuzz shaping instead, reference `Avalonia.HarfBuzz` in your app and call `UseHarfBuzz()`:

```csharp
using MIR.Direct2D1ForAvalonia;

AppBuilder.Configure<App>()
    .UseWin32()
    .UseDirect2D1()
    .UseHarfBuzz();
```

## Known limitations

- **Windows only.** Direct2D1/DirectWrite are not available on other platforms.
- **Window surfaces.** `UseDirect2D1()` renders windows when Avalonia supplies an `IDirect3D11TexturePlatformSurface`, or when the host supplies an external Direct2D render target (`IExternalDirect2DRenderTargetSurface`). Bare-HWND swap chains and the software framebuffer fallback are not supported and will throw `NotSupportedException` at render-target creation.
- **Bitmap formats.** `Bitmap.Save` accepts `.png`, `.jpg/.jpeg`, `.bmp`, `.tif/.tiff`, `.gif`, and `.webp` via the file extension. The optional `quality` parameter is applied to JPEG output through WIC's `ImageQuality` encoder option.
- **Path segmenting.** `Geometry.TryGetSegment` is supported through a Direct2D length-sampled polyline approximation. It preserves path trimming behavior, but returned curve segments are flattened rather than retaining their original Bezier/arc commands.
- **Text parity.** DirectWrite shaping is validated against Avalonia's Skia/HarfBuzz path, but a small set of engine/font-metric divergences is tracked as known rather than bit-for-bit identical.
- **3D transforms.** Only the 2D `Transform` (Matrix) is honored. There is no per-context 4x4/perspective transform.
- **Thread affinity.** The Direct2D device context is thread-affine. The standard single-render-thread Avalonia model is fine; the `EnsureCurrent()` hook does not currently marshal or assert thread ownership.

Licensing:

- This repository is distributed under the MIT license. See `LICENSE`.
- Avalonia-derived source lineage and notices are documented in `THIRD_PARTY_NOTICES.md`.

Packaging:

- Packages target Avalonia `12.0.0`; NuGet package versions come from the `<Version>` values in the package projects.
- Pack with `dotnet pack src/DirectWriteFontsForAvalonia/MIR.DirectWriteFontsForAvalonia.csproj -c Release`
- Pack with `dotnet pack src/Direct2D1ForAvalonia/MIR.Direct2D1ForAvalonia.csproj -c Release`
- Pack with `dotnet pack src/DirectWriteForAvalonia/MIR.DirectWriteForAvalonia.csproj -c Release`
- Packages embed `README.md`, `LICENSE`, and `THIRD_PARTY_NOTICES.md`.

Validation:

- Fast local check: `pwsh scripts/validate.ps1` builds the solution, runs the TextParity, AotSmoke, and HarfBuzz smoke test projects, and runs the TextParity CLI report.
- Include Skia/D2D render parity scenes: `pwsh scripts/validate.ps1 -RunRenderParity` (via `ParityTools render`)
- Include real window screenshot smoke: `pwsh scripts/validate.ps1 -RunWindowSmoke`
- Include benchmark smoke: `pwsh scripts/validate.ps1 -RunBenchmarks`
