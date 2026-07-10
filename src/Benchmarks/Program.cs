using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using Avalonia;
using MIR.Direct2D1ForAvalonia;
using MIR.DirectWriteFontsForAvalonia;
using MIR.DirectWriteForAvalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Win32;
using Benchmarks;

if (!OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("Benchmarks target Windows only.");

// Route to D2D-vs-Skia rendering benchmark if requested.
if (args.Length > 0 && args[0] == "--bench-render-verify")
{
    return RenderBenchmarkCommand.VerifyGpuMatchesWic();
}

if (args.Length > 0 && args[0] == "--bench-windowed")
{
    return WindowedRenderBenchmarkCommand.Run(args);
}

if (args.Length > 0 && (args[0] == "--bench-render" || args[0] == "--bench-render-worker"))
{
    var renderOptions = RenderBenchmarkOptions.Parse(args);
    return RenderBenchmarkCommand.Run(renderOptions);
}

TryStabilizeProcessScheduling();

var options = BenchmarkOptions.Parse(args);

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

if (options.ListCases)
{
    foreach (var benchmarkCase in benchmarkCases)
        Console.WriteLine(benchmarkCase.Name);

    return 0;
}

var selectedCases = new List<(string Name, Func<BenchmarkResult> Run)>();
foreach (var benchmarkCase in benchmarkCases)
{
    if (options.CaseFilters.Count > 0 && !options.CaseFilters.Contains(benchmarkCase.Name))
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
        .UseWin32()
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
    $"{RuntimeInformation.OSArchitecture}/{RuntimeInformation.ProcessArchitecture}",
    Path.GetFileName(Environment.ProcessPath) ?? "unknown",
    results);

var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
var json = JsonSerializer.Serialize(payload, jsonOptions);
if (!string.IsNullOrWhiteSpace(options.OutputJson))
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputJson)) ?? ".");
    File.WriteAllText(options.OutputJson, json);
}

Console.WriteLine(json);

if (!string.IsNullOrWhiteSpace(options.BaselineJson))
{
    var baseline = ReadBenchmarkRun(options.BaselineJson);
    var comparison = CompareBenchmarkRuns(baseline, payload, options.MaxRegressionPercent);
    PrintComparison(comparison);

    if (!string.IsNullOrWhiteSpace(options.ComparisonJson))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.ComparisonJson)) ?? ".");
        File.WriteAllText(options.ComparisonJson, JsonSerializer.Serialize(comparison, jsonOptions));
    }

    return comparison.Passed ? 0 : 1;
}

return 0;

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

static BenchmarkRun ReadBenchmarkRun(string path)
{
    using var stream = File.OpenRead(path);
    var run = JsonSerializer.Deserialize<BenchmarkRun>(stream);
    if (run is null)
        throw new InvalidOperationException($"Failed to read benchmark baseline: {path}");

    return run;
}

static BenchmarkComparison CompareBenchmarkRuns(
    BenchmarkRun baseline,
    BenchmarkRun current,
    double maxRegressionPercent)
{
    if (maxRegressionPercent < 0 || double.IsNaN(maxRegressionPercent))
        throw new ArgumentOutOfRangeException(nameof(maxRegressionPercent), maxRegressionPercent, "Regression threshold must be non-negative.");

    var baselineByName = baseline.Results.ToDictionary(static x => x.Name, StringComparer.OrdinalIgnoreCase);
    var results = new List<BenchmarkComparisonResult>(current.Results.Count);

    foreach (var currentResult in current.Results)
    {
        if (!baselineByName.TryGetValue(currentResult.Name, out var baselineResult))
        {
            results.Add(new BenchmarkComparisonResult(
                currentResult.Name,
                currentResult.ThroughputUnit,
                BaselineThroughput: null,
                CurrentThroughput: currentResult.Throughput,
                ThroughputChangePercent: null,
                BaselineAllocatedBytes: null,
                CurrentAllocatedBytes: currentResult.AllocatedBytes,
                AllocatedBytesChangePercent: null,
                Passed: false,
                Message: "Missing baseline result."));
            continue;
        }

        if (!string.Equals(baselineResult.ThroughputUnit, currentResult.ThroughputUnit, StringComparison.Ordinal))
        {
            results.Add(new BenchmarkComparisonResult(
                currentResult.Name,
                currentResult.ThroughputUnit,
                baselineResult.Throughput,
                currentResult.Throughput,
                ThroughputChangePercent: null,
                baselineResult.AllocatedBytes,
                currentResult.AllocatedBytes,
                AllocatedBytesChangePercent: null,
                Passed: false,
                $"Throughput unit changed from {baselineResult.ThroughputUnit} to {currentResult.ThroughputUnit}."));
            continue;
        }

        var throughputChangePercent = PercentChange(baselineResult.Throughput, currentResult.Throughput);
        var allocationChangePercent = PercentChange(baselineResult.AllocatedBytes, currentResult.AllocatedBytes);
        var throughputPassed = throughputChangePercent >= -maxRegressionPercent;
        var allocationPassed = allocationChangePercent <= maxRegressionPercent;
        var passed = throughputPassed && allocationPassed;

        results.Add(new BenchmarkComparisonResult(
            currentResult.Name,
            currentResult.ThroughputUnit,
            baselineResult.Throughput,
            currentResult.Throughput,
            throughputChangePercent,
            baselineResult.AllocatedBytes,
            currentResult.AllocatedBytes,
            allocationChangePercent,
            passed,
            passed ? "OK" : "Regression threshold exceeded."));
    }

    return new BenchmarkComparison(
        DateTimeOffset.UtcNow,
        maxRegressionPercent,
        results.All(static x => x.Passed),
        results);
}

static double PercentChange(double baseline, double current)
{
    if (baseline == 0)
        return current == 0 ? 0 : double.PositiveInfinity;

    return ((current - baseline) / baseline) * 100.0;
}

static void PrintComparison(BenchmarkComparison comparison)
{
    Console.WriteLine();
    Console.WriteLine($"Baseline comparison (max regression {comparison.MaxRegressionPercent:0.##}%): {(comparison.Passed ? "PASS" : "FAIL")}");

    foreach (var result in comparison.Results)
    {
        if (result.BaselineThroughput is null || result.ThroughputChangePercent is null)
        {
            Console.WriteLine($"  FAIL {result.Name}: {result.Message}");
            continue;
        }

        Console.WriteLine(
            $"  {(result.Passed ? "PASS" : "FAIL")} {result.Name}: " +
            $"throughput {result.CurrentThroughput:0.###} {result.ThroughputUnit} " +
            $"({FormatPercent(result.ThroughputChangePercent.Value)}), " +
            $"alloc {result.CurrentAllocatedBytes} B ({FormatPercent(result.AllocatedBytesChangePercent ?? 0)})");
    }
}

static string FormatPercent(double value)
{
    if (double.IsPositiveInfinity(value))
        return "+inf%";

    if (double.IsNegativeInfinity(value))
        return "-inf%";

    return value.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture) + "%";
}

internal sealed record BenchmarkOptions(
    string? OutputJson,
    string? BaselineJson,
    string? ComparisonJson,
    double MaxRegressionPercent,
    HashSet<string> CaseFilters,
    bool ListCases)
{
    public static BenchmarkOptions Parse(string[] args)
    {
        string? outputJson = null;
        string? baselineJson = null;
        string? comparisonJson = null;
        var maxRegressionPercent = 5.0;
        var filters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var listCases = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string NextValue()
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException($"Missing value for {arg}");
                i++;
                return args[i];
            }

            switch (arg)
            {
                case "--json":
                    outputJson = NextValue();
                    break;
                case "--baseline":
                    baselineJson = NextValue();
                    break;
                case "--comparison-json":
                    comparisonJson = NextValue();
                    break;
                case "--max-regression-percent":
                    maxRegressionPercent = double.Parse(NextValue(), CultureInfo.InvariantCulture);
                    break;
                case "--case":
                    foreach (var part in NextValue().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        filters.Add(part);
                    break;
                case "--list-cases":
                    listCases = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        return new BenchmarkOptions(
            outputJson,
            baselineJson,
            comparisonJson,
            maxRegressionPercent,
            filters,
            listCases);
    }
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

internal sealed record BenchmarkComparison(
    DateTimeOffset TimestampUtc,
    double MaxRegressionPercent,
    bool Passed,
    IReadOnlyList<BenchmarkComparisonResult> Results);

internal sealed record BenchmarkComparisonResult(
    string Name,
    string ThroughputUnit,
    double? BaselineThroughput,
    double CurrentThroughput,
    double? ThroughputChangePercent,
    long? BaselineAllocatedBytes,
    long CurrentAllocatedBytes,
    double? AllocatedBytesChangePercent,
    bool Passed,
    string Message);
