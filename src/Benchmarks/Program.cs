using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using Avalonia;
using MIR.Direct2D1ForAvalonia;
using MIR.DirectWriteForAvalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;

if (!OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("Benchmarks target Windows only.");

TryStabilizeProcessScheduling();

var outputJson = ParseOutputPath(args);
var caseFilters = ParseCaseFilters(args);

var benchmarkCases = new (string Name, Func<BenchmarkResult> Run)[]
{
    ("TextShaper L=32", () => RunTextShaperCase(32, 60000, 3)),
    ("TextShaper L=256", () => RunTextShaperCase(256, 15000, 3)),
    ("TextShaper L=2048", () => RunTextShaperCase(2048, 3000, 3)),
    ("TileBrush Fill 1024x1024", () => RunTileBrushCase(1200, 3)),
    ("FramebufferCopy.Buffer 4096->4096", () => RunFramebufferCopyCase_Buffer("FramebufferCopy.Buffer 4096->4096", 1080, 4096, 4096, 5000)),
    ("FramebufferCopy.Buffer 4096->4352", () => RunFramebufferCopyCase_Buffer("FramebufferCopy.Buffer 4096->4352", 1080, 4096, 4352, 5000)),
    ("FramebufferCopy.Buffer 4352->4096", () => RunFramebufferCopyCase_Buffer("FramebufferCopy.Buffer 4352->4096", 1080, 4352, 4096, 5000)),

    ("FramebufferCopy.Memcpy 4096->4096", () => RunFramebufferCopyCase_Memcpy("FramebufferCopy.Memcpy 4096->4096", 1080, 4096, 4096, 5000)),
    ("FramebufferCopy.Memcpy 4096->4352", () => RunFramebufferCopyCase_Memcpy("FramebufferCopy.Memcpy 4096->4352", 1080, 4096, 4352, 5000)),
    ("FramebufferCopy.Memcpy 4352->4096", () => RunFramebufferCopyCase_Memcpy("FramebufferCopy.Memcpy 4352->4096", 1080, 4352, 4096, 5000)),

    ("FramebufferCopy.Loop 4096->4096", () => RunFramebufferCopyCase_Loop("FramebufferCopy.Loop 4096->4096", 1080, 4096, 4096, 5000)),
    ("FramebufferCopy.Loop 4096->4352", () => RunFramebufferCopyCase_Loop("FramebufferCopy.Loop 4096->4352", 1080, 4096, 4352, 5000)),
    ("FramebufferCopy.Loop 4352->4096", () => RunFramebufferCopyCase_Loop("FramebufferCopy.Loop 4352->4096", 1080, 4352, 4096, 5000)),
};

var selectedCases = new List<(string Name, Func<BenchmarkResult> Run)>();
foreach (var benchmarkCase in benchmarkCases)
{
    if (caseFilters.Count > 0 && !caseFilters.Contains(benchmarkCase.Name))
        continue;

    selectedCases.Add(benchmarkCase);
}

if (selectedCases.Count == 0)
{
    throw new ArgumentException(
        "No benchmark case matched --case filters. Supported cases: "
        + string.Join(", ", benchmarkCases.Select(x => x.Name)));
}

var needsAvaloniaInit = selectedCases.Any(static x =>
    !x.Name.StartsWith("FramebufferCopy", StringComparison.OrdinalIgnoreCase));

if (needsAvaloniaInit)
{
    AppBuilder.Configure<BenchmarkApp>()
        .UsePlatformDetect()
        .UseDirect2D1()
        .UseDirectWrite()
        .SetupWithoutStarting();
}

var results = new List<BenchmarkResult>(selectedCases.Count);
foreach (var benchmarkCase in selectedCases)
{
    results.Add(benchmarkCase.Run());
}

var payload = new BenchmarkRun(
    DateTimeOffset.UtcNow,
    Environment.MachineName,
    Environment.ProcessPath ?? "unknown",
    results);

var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
if (!string.IsNullOrWhiteSpace(outputJson))
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputJson)) ?? ".");
    File.WriteAllText(outputJson, json);
}

Console.WriteLine(json);

static string? ParseOutputPath(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], "--json", StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }

    return null;
}

static HashSet<string> ParseCaseFilters(string[] args)
{
    var filters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    for (var i = 0; i < args.Length - 1; i++)
    {
        if (!string.Equals(args[i], "--case", StringComparison.OrdinalIgnoreCase))
            continue;

        foreach (var part in args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            filters.Add(part);
        }
    }

    return filters;
}

static void TryStabilizeProcessScheduling()
{
    try
    {
        var process = Process.GetCurrentProcess();
        process.PriorityClass = ProcessPriorityClass.High;
        process.PriorityBoostEnabled = true;
        Thread.CurrentThread.Priority = ThreadPriority.Highest;
    }
    catch
    {
        // Best effort only; keep benchmark runnable in restricted environments.
    }
}

BenchmarkResult RunTextShaperCase(int textLength, int iterations, int repeats)
{
    var fontPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "segoeui.ttf");
using var stream = File.OpenRead(fontPath);

    var fontManager = new FontManagerImpl();
    if (!fontManager.TryCreateGlyphTypeface(stream, FontSimulations.None, out var platformTypeface))
        throw new InvalidOperationException("Failed to create glyph typeface.");
    using var platformTypefaceLease = platformTypeface;
    var glyphTypeface = new GlyphTypeface(platformTypefaceLease);

    var shaper = new DirectWriteTextShaper();
    var text = BuildText(textLength);
    var options = new TextShaperOptions(glyphTypeface, 24, 0, CultureInfo.InvariantCulture, 0, 0);

    // Warmup
    shaper.ShapeText(text.AsMemory(), options);

    var (elapsedMs, charsPerSecond, allocatedBytes) = MeasureCase(
        iterations,
        repeats,
        elapsedSeconds => (textLength * (double)iterations) / elapsedSeconds,
        () => shaper.ShapeText(text.AsMemory(), options));

    return new BenchmarkResult(
        $"TextShaper L={textLength}",
        iterations,
        elapsedMs,
        charsPerSecond,
        "chars/s",
        allocatedBytes);
}

static BenchmarkResult RunTileBrushCase(int iterations, int repeats)
{
    using var tile = new RenderTargetBitmap(new PixelSize(64, 64), new Vector(96, 96));
    using (var tileContext = tile.CreateDrawingContext(false))
    {
        tileContext.DrawRectangle(Brushes.CornflowerBlue, null, new Rect(0, 0, 64, 64));
        tileContext.DrawLine(new Pen(Brushes.White, 2), new Point(0, 0), new Point(63, 63));
    }

    var brush = new ImageBrush(tile)
    {
        Stretch = Stretch.None,
        TileMode = TileMode.Tile,
        SourceRect = RelativeRect.Fill,
        DestinationRect = RelativeRect.Fill
    };

    using var target = new RenderTargetBitmap(new PixelSize(1024, 1024), new Vector(96, 96));
    using var context = target.CreateDrawingContext(false);

    context.DrawRectangle(brush, null, new Rect(0, 0, 1024, 1024)); // warmup

    var (elapsedMs, opsPerSecond, allocatedBytes) = MeasureCase(
        iterations,
        repeats,
        elapsedSeconds => iterations / elapsedSeconds,
        () => context.DrawRectangle(brush, null, new Rect(0, 0, 1024, 1024)));

    return new BenchmarkResult(
        "TileBrush Fill 1024x1024",
        iterations,
        elapsedMs,
        opsPerSecond,
        "ops/s",
        allocatedBytes);
}

static unsafe void CopyRows(IntPtr srcPtr, IntPtr dstPtr, int height, int srcStride, int dstStride)
{
    var rowBytes = Math.Min(srcStride, dstStride);
    var srcBase = (byte*)srcPtr;
    var dstBase = (byte*)dstPtr;

    for (var y = 0; y < height; y++)
    {
        var srcRow = srcBase + (y * srcStride);
        var dstRow = dstBase + (y * dstStride);
        Buffer.MemoryCopy(srcRow, dstRow, rowBytes, rowBytes);
    }
}

[DllImport("msvcrt.dll", EntryPoint = "memcpy", CallingConvention = CallingConvention.Cdecl)]
static extern IntPtr CopyMemoryPInvoke(IntPtr dest, IntPtr src, UIntPtr count);

static void CopyRows_Memcpy(IntPtr srcPtr, IntPtr dstPtr, int height, int srcStride, int dstStride)
{
    var rowBytes = Math.Min(srcStride, dstStride);
    for (var y = 0; y < height; y++)
    {
        var sRow = new IntPtr(srcPtr.ToInt64() + (long)y * srcStride);
        var dRow = new IntPtr(dstPtr.ToInt64() + (long)y * dstStride);
        CopyMemoryPInvoke(dRow, sRow, new UIntPtr((ulong)(uint)rowBytes));
    }
}

static unsafe void CopyRows_Loop(IntPtr srcPtr, IntPtr dstPtr, int height, int srcStride, int dstStride)
{
    var rowBytes = Math.Min(srcStride, dstStride);
    var srcBase = (byte*)srcPtr;
    var dstBase = (byte*)dstPtr;

    for (var y = 0; y < height; y++)
    {
        var s = srcBase + (y * srcStride);
        var d = dstBase + (y * dstStride);
        for (var i = 0; i < rowBytes; i++)
            d[i] = s[i];
    }
}

static BenchmarkResult RunFramebufferCopyCase_Buffer(string name, int height, int srcStride, int dstStride, int iterations)
{
    return RunFramebufferCopyCase_Internal(name, height, srcStride, dstStride, iterations, CopyRows);
}

static BenchmarkResult RunFramebufferCopyCase_Memcpy(string name, int height, int srcStride, int dstStride, int iterations)
{
    return RunFramebufferCopyCase_Internal(name, height, srcStride, dstStride, iterations, CopyRows_Memcpy);
}

static BenchmarkResult RunFramebufferCopyCase_Loop(string name, int height, int srcStride, int dstStride, int iterations)
{
    return RunFramebufferCopyCase_Internal(name, height, srcStride, dstStride, iterations, CopyRows_Loop);
}

static BenchmarkResult RunFramebufferCopyCase_Internal(string name, int height, int srcStride, int dstStride, int iterations, Action<IntPtr, IntPtr, int, int, int> copier)
{
    var srcSize = srcStride * height;
    var dstSize = dstStride * height;
    var rowBytes = Math.Min(srcStride, dstStride);

    var src = Marshal.AllocHGlobal(srcSize);
    var dst = Marshal.AllocHGlobal(dstSize);
    try
    {
        unsafe
        {
            new Span<byte>((void*)src, srcSize).Fill(0x5A);
            new Span<byte>((void*)dst, dstSize).Clear();
        }

        copier(src, dst, height, srcStride, dstStride); // warmup

        var (elapsedMs, gbPerSecond, allocatedBytes) = MeasureCase(
            iterations,
            5,
            elapsedSeconds =>
            {
                var totalBytes = (double)rowBytes * height * iterations;
                return totalBytes / elapsedSeconds / 1_000_000_000.0;
            },
            () => copier(src, dst, height, srcStride, dstStride),
            selectBest: true);

        return new BenchmarkResult(
            name,
            iterations,
            elapsedMs,
            gbPerSecond,
            "GB/s",
            allocatedBytes);
    }
    finally
    {
        Marshal.FreeHGlobal(src);
        Marshal.FreeHGlobal(dst);
    }
}

static (double ElapsedMs, double Throughput, long AllocatedBytes) MeasureCase(
    int iterations,
    int repeats,
    Func<double, double> throughputFromElapsedSeconds,
    Action body,
    bool selectBest = false)
{
    var elapsedValues = new double[repeats];
    var throughputValues = new double[repeats];
    var allocatedValues = new long[repeats];

    for (var repeat = 0; repeat < repeats; repeat++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var startAlloc = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
            body();
        sw.Stop();

        elapsedValues[repeat] = sw.Elapsed.TotalMilliseconds;
        throughputValues[repeat] = throughputFromElapsedSeconds(sw.Elapsed.TotalSeconds);
        allocatedValues[repeat] = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - startAlloc);
    }

    if (selectBest)
    {
        var bestIndex = 0;
        for (var i = 1; i < repeats; i++)
        {
            if (elapsedValues[i] < elapsedValues[bestIndex])
                bestIndex = i;
        }

        return (
            elapsedValues[bestIndex],
            throughputValues[bestIndex],
            allocatedValues[bestIndex]);
    }

    Array.Sort(elapsedValues);
    Array.Sort(throughputValues);
    Array.Sort(allocatedValues);

    var middle = repeats / 2;
    return (
        elapsedValues[middle],
        throughputValues[middle],
        allocatedValues[middle]);
}

static string BuildText(int length)
{
    const string seed = "AvaloniaDirect2D1Perf";
    if (length <= seed.Length)
        return seed[..length];

    var chars = new char[length];
    for (var i = 0; i < length; i++)
        chars[i] = seed[i % seed.Length];
    return new string(chars);
}

internal sealed class BenchmarkApp : Application
{
}

internal sealed record BenchmarkRun(
    DateTimeOffset TimestampUtc,
    string Machine,
    string ProcessPath,
    IReadOnlyList<BenchmarkResult> Results);

internal sealed record BenchmarkResult(
    string Name,
    int Iterations,
    double ElapsedMs,
    double Throughput,
    string ThroughputUnit,
    long AllocatedBytes);
