using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Win32;
using MIR.Direct2D1ForAvalonia;
using MIR.DirectWriteForAvalonia;

namespace ParityTools;

internal static class RenderParityCommand
{
public static int Run(RenderParityOptions options)
{
    if (!OperatingSystem.IsWindows())
        throw new PlatformNotSupportedException("Render parity targets Windows only.");

    if (options.Backend is null)
    {
        return RunController(options);
    }

    RunRenderer(options);
    return 0;
}

static int RunController(RenderParityOptions options)
{
    var outputDirectory = options.OutputDirectory ?? Path.GetFullPath(Path.Combine("artifacts", "renderparity"));
    Directory.CreateDirectory(outputDirectory);

    var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot resolve current process path.");
    var sceneNames = options.Scenes.Count == 0 ? RenderScenes.All.Select(static x => x.Name).ToArray() : options.Scenes.ToArray();
    var results = new List<RenderParitySceneResult>(sceneNames.Length);

    foreach (var sceneName in sceneNames)
    {
        var scene = RenderScenes.Get(sceneName);
        var prefix = SanitizeFileName(scene.Name);
        var d2dPng = Path.Combine(outputDirectory, $"{prefix}.d2d.png");
        var skiaPng = Path.Combine(outputDirectory, $"{prefix}.skia.png");
        var d2dRaw = Path.Combine(outputDirectory, $"{prefix}.d2d.bgra");
        var skiaRaw = Path.Combine(outputDirectory, $"{prefix}.skia.bgra");

        RunBackend(exePath, "d2d", scene.Name, d2dPng, d2dRaw);
        RunBackend(exePath, "skia", scene.Name, skiaPng, skiaRaw);

        var metrics = CompareRawImages(d2dRaw, skiaRaw, scene.Size, options.PixelTolerance);
        var passed =
            metrics.MeanChannelDelta <= options.MaxMeanChannelDelta &&
            metrics.PixelsOverTolerancePercent <= options.MaxPixelsOverTolerancePercent;

        results.Add(new RenderParitySceneResult(
            scene.Name,
            scene.Size.Width,
            scene.Size.Height,
            metrics.MeanChannelDelta,
            metrics.MaxChannelDelta,
            metrics.PixelsOverTolerancePercent,
            passed));
    }

    var report = new RenderParityReport(
        DateTimeOffset.UtcNow,
        options.PixelTolerance,
        options.MaxMeanChannelDelta,
        options.MaxPixelsOverTolerancePercent,
        results.All(static x => x.Passed),
        results);

    var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
    var reportPath = options.ReportJson ?? Path.Combine(outputDirectory, "renderparity-report.json");
    File.WriteAllText(reportPath, JsonSerializer.Serialize(report, jsonOptions));
    Console.WriteLine(JsonSerializer.Serialize(report, jsonOptions));

    return report.Passed ? 0 : 1;
}

static void RunBackend(string exePath, string backend, string scene, string pngPath, string rawPath)
{
    var psi = new ProcessStartInfo(exePath)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    psi.ArgumentList.Add("render-worker");
    psi.ArgumentList.Add("--backend");
    psi.ArgumentList.Add(backend);
    psi.ArgumentList.Add("--scene");
    psi.ArgumentList.Add(scene);
    psi.ArgumentList.Add("--png");
    psi.ArgumentList.Add(pngPath);
    psi.ArgumentList.Add("--raw");
    psi.ArgumentList.Add(rawPath);

    using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start render backend process.");
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();

    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"Render backend '{backend}' failed for scene '{scene}' with exit code {process.ExitCode}." +
            Environment.NewLine + stdout + Environment.NewLine + stderr);
    }
}

static void RunRenderer(RenderParityOptions options)
{
    var backend = options.Backend ?? throw new ArgumentException("Missing backend.");
    var sceneName = options.Scenes.SingleOrDefault() ?? throw new ArgumentException("Renderer mode requires exactly one --scene.");
    var pngPath = options.PngPath ?? throw new ArgumentException("Renderer mode requires --png.");
    var rawPath = options.RawPath ?? throw new ArgumentException("Renderer mode requires --raw.");

    var builder = AppBuilder.Configure<RenderParityApp>().UseWin32();
    if (string.Equals(backend, "d2d", StringComparison.OrdinalIgnoreCase))
    {
        builder.UseDirect2D1().UseDirectWrite();
    }
    else if (string.Equals(backend, "skia", StringComparison.OrdinalIgnoreCase))
    {
        builder.UseSkia().UseHarfBuzz();
    }
    else
    {
        throw new ArgumentException($"Unsupported backend: {backend}");
    }

    builder.SetupWithoutStarting();

    var scene = RenderScenes.Get(sceneName);
    using var bitmap = new RenderTargetBitmap(scene.Size, new Vector(96, 96));
    using (var context = bitmap.CreateDrawingContext(false))
    {
        scene.Render(context);
    }

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pngPath)) ?? ".");
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(rawPath)) ?? ".");
    bitmap.Save(pngPath);
    WriteRawBgra(bitmap, scene.Size, rawPath);
}

static void WriteRawBgra(RenderTargetBitmap bitmap, PixelSize size, string rawPath)
{
    using var framebuffer = new RawFramebuffer(size);
    bitmap.CopyPixels(framebuffer);
    File.WriteAllBytes(rawPath, framebuffer.Pixels);
}

static ImageComparisonMetrics CompareRawImages(string expectedPath, string actualPath, PixelSize size, int pixelTolerance)
{
    var expected = File.ReadAllBytes(expectedPath);
    var actual = File.ReadAllBytes(actualPath);
    var expectedLength = checked(size.Width * size.Height * 4);

    if (expected.Length != expectedLength || actual.Length != expectedLength)
        throw new InvalidOperationException($"Raw image size mismatch for {size.Width}x{size.Height}.");

    long channelDeltaSum = 0;
    var maxChannelDelta = 0;
    var pixelsOverTolerance = 0;

    for (var i = 0; i < expected.Length; i += 4)
    {
        var b = Math.Abs(expected[i] - actual[i]);
        var g = Math.Abs(expected[i + 1] - actual[i + 1]);
        var r = Math.Abs(expected[i + 2] - actual[i + 2]);
        var pixelMax = Math.Max(r, Math.Max(g, b));

        channelDeltaSum += r + g + b;
        maxChannelDelta = Math.Max(maxChannelDelta, pixelMax);
        if (pixelMax > pixelTolerance)
            pixelsOverTolerance++;
    }

    var pixelCount = size.Width * size.Height;
    return new ImageComparisonMetrics(
        channelDeltaSum / (pixelCount * 3.0),
        maxChannelDelta,
        pixelsOverTolerance * 100.0 / pixelCount);
}

static string SanitizeFileName(string name)
{
    var invalid = Path.GetInvalidFileNameChars();
    var chars = name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
    return new string(chars).Replace(' ', '_');
}
}

internal sealed class RenderParityApp : Application
{
}

internal static class RenderScenes
{
    public static readonly RenderScene[] All =
    [
        new("ShapesAndBrushes", new PixelSize(256, 160), DrawShapesAndBrushes),
        new("TextAndGeometry", new PixelSize(256, 160), DrawTextAndGeometry),
        new("ClipsAndOpacity", new PixelSize(192, 128), DrawClipsAndOpacity),
    ];

    public static RenderScene Get(string name)
    {
        foreach (var scene in All)
        {
            if (string.Equals(scene.Name, name, StringComparison.OrdinalIgnoreCase))
                return scene;
        }

        throw new ArgumentException(
            "Unknown scene. Supported scenes: " + string.Join(", ", All.Select(static x => x.Name)));
    }

    private static void DrawShapesAndBrushes(DrawingContext context)
    {
        context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 256, 160));
        context.DrawRectangle(
            new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(24, 118, 210), 0),
                    new GradientStop(Color.FromRgb(71, 193, 162), 1)
                }
            },
            null,
            new RoundedRect(new Rect(16, 16, 104, 72), 10));
        context.DrawEllipse(new SolidColorBrush(Color.FromArgb(210, 255, 196, 72)), new Pen(Brushes.Black, 1.5), new Rect(140, 22, 72, 54));
        context.DrawLine(new Pen(Brushes.DarkRed, 3), new Point(24, 124), new Point(224, 104));
        context.DrawRectangle(SmokeTileBrush.Create(), null, new Rect(150, 94, 72, 42));
    }

    private static void DrawTextAndGeometry(DrawingContext context)
    {
        context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 256, 160));
        var geometry = Geometry.Parse("M 18,130 C 58,58 122,158 174,74 L 230,112");
        context.DrawGeometry(null, new Pen(Brushes.Black, 2), geometry);

        var text = new FormattedText(
            "Direct2D 替代 Skia",
            CultureInfo.GetCultureInfo("zh-CN"),
            FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"),
            22,
            Brushes.DarkBlue);
        context.DrawText(text, new Point(16, 18));
    }

    private static void DrawClipsAndOpacity(DrawingContext context)
    {
        context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 192, 128));
        context.DrawRectangle(Brushes.Red, null, new Rect(16, 18, 64, 36));
        using (context.PushClip(new RoundedRect(new Rect(30, 18, 36, 36), 12)))
        {
            context.DrawRectangle(Brushes.Lime, null, new Rect(16, 18, 64, 36));
        }

        context.DrawRectangle(Brushes.Blue, null, new Rect(104, 18, 54, 36));
        using (context.PushOpacity(0.5))
        {
            context.DrawRectangle(Brushes.Yellow, null, new Rect(104, 18, 54, 36));
        }

        using (context.PushTransform(Matrix.CreateTranslation(38, 78)))
        {
            context.DrawRectangle(Brushes.Purple, null, new Rect(0, 0, 76, 24));
        }
    }
}

internal static class SmokeTileBrush
{
    public static ImageBrush Create()
    {
        var pixels = new byte[12 * 12 * 4];
        for (var y = 0; y < 12; y++)
        {
            for (var x = 0; x < 12; x++)
            {
                var offset = ((y * 12) + x) * 4;
                var even = ((x / 3) + (y / 3)) % 2 == 0;
                pixels[offset] = even ? (byte)70 : (byte)235;
                pixels[offset + 1] = even ? (byte)210 : (byte)120;
                pixels[offset + 2] = even ? (byte)245 : (byte)90;
                pixels[offset + 3] = 255;
            }
        }

        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            return new ImageBrush(new Bitmap(
                PixelFormats.Bgra8888,
                AlphaFormat.Opaque,
                handle.AddrOfPinnedObject(),
                new PixelSize(12, 12),
                new Vector(96, 96),
                12 * 4))
            {
                Stretch = Stretch.Fill,
                TileMode = TileMode.FlipXY
            };
        }
        finally
        {
            handle.Free();
        }
    }
}

internal sealed record RenderScene(string Name, PixelSize Size, Action<DrawingContext> Render);

internal sealed record ImageComparisonMetrics(
    double MeanChannelDelta,
    int MaxChannelDelta,
    double PixelsOverTolerancePercent);

internal sealed record RenderParityReport(
    DateTimeOffset TimestampUtc,
    int PixelTolerance,
    double MaxMeanChannelDelta,
    double MaxPixelsOverTolerancePercent,
    bool Passed,
    IReadOnlyList<RenderParitySceneResult> Results);

internal sealed record RenderParitySceneResult(
    string Scene,
    int Width,
    int Height,
    double MeanChannelDelta,
    int MaxChannelDelta,
    double PixelsOverTolerancePercent,
    bool Passed);

internal sealed record RenderParityOptions(
    string? Backend,
    HashSet<string> Scenes,
    string? OutputDirectory,
    string? ReportJson,
    string? PngPath,
    string? RawPath,
    int PixelTolerance,
    double MaxMeanChannelDelta,
    double MaxPixelsOverTolerancePercent);

internal sealed class RawFramebuffer : ILockedFramebuffer
{
    private readonly GCHandle _handle;

    public RawFramebuffer(PixelSize size)
    {
        Size = size;
        RowBytes = size.Width * 4;
        Pixels = new byte[RowBytes * size.Height];
        _handle = GCHandle.Alloc(Pixels, GCHandleType.Pinned);
    }

    public byte[] Pixels { get; }

    public IntPtr Address => _handle.AddrOfPinnedObject();

    public PixelSize Size { get; }

    public int RowBytes { get; }

    public Vector Dpi => new(96, 96);

    public PixelFormat Format => PixelFormats.Bgra8888;

    public AlphaFormat AlphaFormat => AlphaFormat.Opaque;

    public nint Key => 0;

    public void Dispose()
    {
        _handle.Free();
    }
}
