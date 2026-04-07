# Avalonia.Direct2D1

Unoffical. Fork from https://github.com/AvaloniaUI/Avalonia/pull/15615

Targets Avalonia `12.0.0`.

`UseDirect2D1()` is now separated from text shaping:

- Rendering backend: Direct2D1
- Text shaping: choose explicitly with `UseHarfBuzz()` or `UseDirectWrite()`

Example:

```csharp
AppBuilder.Configure<App>()
    .UseWin32()
    .UseDirect2D1()
    .UseHarfBuzz();
```

To switch shaping to DirectWrite:

```csharp
AppBuilder.Configure<App>()
    .UseWin32()
    .UseDirect2D1()
    .UseDirectWrite();
```
