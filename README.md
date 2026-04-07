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

Licensing:

- This repository is distributed under the MIT license. See `LICENSE`.
- Avalonia-derived source lineage and notices are documented in `THIRD_PARTY_NOTICES.md`.

Packaging:

- Current package version baseline is `12.0.0`.
- Pack with `dotnet pack src/Direct2D1ForAvalonia/MIR.Direct2D1ForAvalonia.csproj -c Release`
- Pack with `dotnet pack src/DirectWriteForAvalonia/MIR.DirectWriteForAvalonia.csproj -c Release`
- Both packages embed `README.md`, `LICENSE`, and `THIRD_PARTY_NOTICES.md`.
