using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Win32;
using MIR.Direct2D1ForAvalonia;
using MIR.DirectWriteForAvalonia;

namespace Benchmarks;

/// <summary>
/// Controller for D2D-vs-Skia rendering benchmarks.
/// Runs each backend in a separate child process (matching the ParityTools pattern)
/// because only one IPlatformRenderInterface can be active per process.
/// Backends: d2d (WIC/CPU), d2d-gpu, skia (CPU), skia-gpu (Angle EGL offscreen).
/// </summary>
internal static class RenderBenchmarkCommand
{
    static readonly string[] AllBackends = ["d2d", "d2d-gpu", "skia", "skia-gpu"];

    public static int Run(RenderBenchmarkOptions options)
    {
        if (options.Backend is not null)
            return RunWorker(options);

        return RunController(options);
    }

    static int RunController(RenderBenchmarkOptions options)
    {
        var sceneNames = options.Scenes.Count == 0
            ? RenderBenchmarkScenes.All.Select(static x => x.Name).ToArray()
            : options.Scenes.ToArray();

        var allResults = new List<BackendBenchmarkResult>();

        foreach (var backend in AllBackends)
        {
            foreach (var sceneName in sceneNames)
            {
                var scene = RenderBenchmarkScenes.Get(sceneName);
                var result = RunWorkerProcess(
                    backend,
                    sceneName,
                    scene.Iterations,
                    options.Repeats,
                    options.SessionMode);
                allResults.Add(result);
            }
        }

        var report = new RenderBenchmarkReport(
            DateTimeOffset.UtcNow,
            allResults);

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(report, jsonOptions);

        var outputPath = options.OutputJson ?? Path.Combine("artifacts", "render-bench", "report.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
        File.WriteAllText(outputPath, json);

        Console.WriteLine(json);
        PrintComparison(report);
        return 0;
    }

    static void PrintComparison(RenderBenchmarkReport report)
    {
        Console.WriteLine();
        Console.WriteLine("D2D vs Skia Rendering Benchmark (CPU + GPU)");
        Console.WriteLine(new string('-', 90));

        var byScene = report.Results
            .GroupBy(static x => x.Scene)
            .OrderBy(static x => x.Key);

        foreach (var group in byScene)
        {
            var d2d = group.FirstOrDefault(static x => x.Backend == "d2d");
            var d2dGpu = group.FirstOrDefault(static x => x.Backend == "d2d-gpu");
            var skia = group.FirstOrDefault(static x => x.Backend == "skia");
            var skiaGpu = group.FirstOrDefault(static x => x.Backend == "skia-gpu");

            var iters = d2d?.Iterations ?? d2dGpu?.Iterations ?? skia?.Iterations ?? skiaGpu?.Iterations ?? 0;
            var repeats = d2d?.Repeats ?? d2dGpu?.Repeats ?? skia?.Repeats ?? skiaGpu?.Repeats ?? 0;

            Console.WriteLine();
            Console.WriteLine($"  {group.Key} ({iters} iters x {repeats} repeats):");
            PrintLine("D2D (WIC/CPU)", d2d);
            PrintLine("D2D (GPU)    ", d2dGpu);
            PrintLine("Skia (CPU)   ", skia);
            PrintLine("Skia (GPU)   ", skiaGpu);

            PrintRatio("D2D-GPU vs Skia-GPU", d2dGpu, skiaGpu);
            PrintRatio("D2D-GPU vs Skia-CPU", d2dGpu, skia);
            PrintRatio("Skia-GPU vs Skia-CPU", skiaGpu, skia);
            PrintRatio("D2D-GPU vs D2D-CPU ", d2dGpu, d2d);
            PrintRatio("D2D-CPU vs Skia-CPU", d2d, skia);
        }
    }

    static void PrintLine(string label, BackendBenchmarkResult? r)
    {
        if (r is null)
        {
            Console.WriteLine($"    {label}: n/a");
            return;
        }

        var extra = "";
        if (r.FirstFrameMs is double ff && r.SteadyPerIterMs is double st)
            extra = $"  first={ff:0.###}ms  steady/iter={st:0.####}ms";
        Console.WriteLine(
            $"    {label}: {r.MedianMs:0.###} ms  ({r.OpsPerSecond:0.###} ops/s)  alloc={r.AllocatedBytes} B{extra}");
    }

    static void PrintRatio(string label, BackendBenchmarkResult? a, BackendBenchmarkResult? b)
    {
        if (a is null || b is null || b.MedianMs <= 0)
            return;

        var ratio = a.MedianMs / b.MedianMs;
        var faster = ratio < 1.0 ? "former faster" : "latter faster";
        Console.WriteLine($"      {label}: {ratio:0.###}x ({faster} by {Math.Abs(1.0 - ratio) * 100:0.#}%)");
    }

    static BackendBenchmarkResult RunWorkerProcess(
        string backend,
        string scene,
        int iterations,
        int repeats,
        string sessionMode)
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot resolve current process path.");

        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        psi.ArgumentList.Add("--bench-render-worker");
        psi.ArgumentList.Add("--backend");
        psi.ArgumentList.Add(backend);
        psi.ArgumentList.Add("--scene");
        psi.ArgumentList.Add(scene);
        psi.ArgumentList.Add("--iterations");
        psi.ArgumentList.Add(iterations.ToString());
        psi.ArgumentList.Add("--repeats");
        psi.ArgumentList.Add(repeats.ToString());
        psi.ArgumentList.Add("--session-mode");
        psi.ArgumentList.Add(sessionMode);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {backend} worker.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Render benchmark worker '{backend}' failed for scene '{scene}' (exit {process.ExitCode})."
                + Environment.NewLine + stderr);
        }

        var result = JsonSerializer.Deserialize<BackendBenchmarkResult>(stdout)
            ?? throw new InvalidOperationException("Worker returned no result.");
        return result;
    }

    static int RunWorker(RenderBenchmarkOptions options)
    {
        var backend = options.Backend ?? throw new ArgumentException("Worker requires --backend.");
        var sceneName = options.Scenes.SingleOrDefault()
            ?? throw new ArgumentException("Worker requires exactly one --scene.");
        var scene = RenderBenchmarkScenes.Get(sceneName);
        var iterations = options.Iterations ?? scene.Iterations;
        var repeats = options.Repeats;

        var isD2dGpu = string.Equals(backend, "d2d-gpu", StringComparison.OrdinalIgnoreCase);
        var isSkiaGpu = string.Equals(backend, "skia-gpu", StringComparison.OrdinalIgnoreCase);
        var isD2d = string.Equals(backend, "d2d", StringComparison.OrdinalIgnoreCase) || isD2dGpu;
        var isSkia = string.Equals(backend, "skia", StringComparison.OrdinalIgnoreCase) || isSkiaGpu;

        var builder = AppBuilder.Configure<BenchmarkApp>().UseWin32();

        if (isSkiaGpu)
        {
            // Force Angle EGL so IPlatformGraphics is a real GPU context (not software).
            builder = builder.With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.AngleEgl],
                CompositionMode = [Win32CompositionMode.RedirectionSurface],
            });
        }

        if (isD2d)
        {
            builder.UseDirect2D1().UseDirectWrite();
        }
        else if (isSkia)
        {
            builder.UseSkia().UseHarfBuzz();
        }
        else
        {
            throw new ArgumentException(
                $"Unsupported backend: {backend}. Supported: {string.Join(", ", AllBackends)}");
        }

        builder.SetupWithoutStarting();

        var elapsedValues = new double[repeats];
        var allocatedValues = new long[repeats];
        var firstFrameValues = isD2dGpu ? new double[repeats] : null;
        var steadyValues = isD2dGpu ? new double[repeats] : null;

        var sessionMode = options.SessionMode;
        if (isD2dGpu)
            RunD2dGpu(scene, iterations, repeats, elapsedValues, allocatedValues, sessionMode, firstFrameValues, steadyValues);
        else if (isSkiaGpu)
            RunSkiaGpu(scene, iterations, repeats, elapsedValues, allocatedValues, sessionMode);
        else
            RunCpu(scene, iterations, repeats, elapsedValues, allocatedValues, sessionMode);

        Array.Sort(elapsedValues);
        Array.Sort(allocatedValues);

        var median = elapsedValues[repeats / 2];
        var medianAlloc = allocatedValues[repeats / 2];
        var opsPerSecond = iterations / (median / 1000.0);

        double? firstFrameMedian = null;
        double? steadyPerIterMedian = null;
        if (firstFrameValues is not null)
        {
            Array.Sort(firstFrameValues);
            firstFrameMedian = firstFrameValues[repeats / 2];
        }

        if (steadyValues is not null)
        {
            Array.Sort(steadyValues);
            steadyPerIterMedian = steadyValues[repeats / 2];
        }

        var result = new BackendBenchmarkResult(
            backend,
            sceneName,
            iterations,
            repeats,
            median,
            opsPerSecond,
            medianAlloc,
            firstFrameMedian,
            steadyPerIterMedian);

        Console.Write(JsonSerializer.Serialize(result));
        return 0;
    }

    /// <summary>
    /// Software path: Avalonia's RenderTargetBitmap. D2D rasterises via WIC/CPU; Skia via CPU.
    /// Fully synchronous.
    /// </summary>
    static void RunCpu(
        RenderBenchScene scene,
        int iterations,
        int repeats,
        double[] elapsedValues,
        long[] allocatedValues,
        string sessionMode)
    {
        using var bitmap = new RenderTargetBitmap(scene.Size, scene.Dpi);

        using (var context = bitmap.CreateDrawingContext(false))
            scene.Render(context);

        var batched = IsBatchedSession(sessionMode);
        for (var repeat = 0; repeat < repeats; repeat++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var startAlloc = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();

            if (batched)
            {
                using var context = bitmap.CreateDrawingContext(false);
                for (var i = 0; i < iterations; i++)
                    scene.Render(context);
            }
            else
            {
                for (var i = 0; i < iterations; i++)
                {
                    using var context = bitmap.CreateDrawingContext(false);
                    scene.Render(context);
                }
            }

            sw.Stop();
            elapsedValues[repeat] = sw.Elapsed.TotalMilliseconds;
            allocatedValues[repeat] = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - startAlloc);
        }
    }

    /// <summary>
    /// D2D GPU path: D3D11 texture + ID2D1DeviceContext (same DrawingContextImpl as window rendering).
    /// EndDraw only submits; WaitForGpu() barriers capture real GPU completion.
    /// <paramref name="sessionMode"/> <c>batched</c> holds one BeginDraw/EndDraw across all
    /// iterations (diagnostic only — real Avalonia frames are always per-iter).
    /// </summary>
    static void RunD2dGpu(
        RenderBenchScene scene,
        int iterations,
        int repeats,
        double[] elapsedValues,
        long[] allocatedValues,
        string sessionMode,
        double[]? firstFrameMs = null,
        double[]? steadyMs = null)
    {
        using var target = new Direct2DGpuOffscreenTarget(scene.Size, scene.Dpi);

        // Cold paint (builds caches / first command list). Not included in steady timing.
        using (var context = WrapDrawingContext(target.CreateDrawingContext()))
            scene.Render(context);
        target.WaitForGpu();

        var batched = IsBatchedSession(sessionMode);
        for (var repeat = 0; repeat < repeats; repeat++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var startAlloc = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            double firstMs = 0;

            if (batched)
            {
                using (var context = WrapDrawingContext(target.CreateDrawingContext()))
                {
                    var t0 = Stopwatch.GetTimestamp();
                    scene.Render(context);
                    firstMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    for (var i = 1; i < iterations; i++)
                        scene.Render(context);
                }
            }
            else
            {
                // First timed frame after cold warmup (usually command-list hit when reuse is on).
                var t0 = Stopwatch.GetTimestamp();
                using (var context = WrapDrawingContext(target.CreateDrawingContext()))
                    scene.Render(context);
                firstMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

                for (var i = 1; i < iterations; i++)
                {
                    using var context = WrapDrawingContext(target.CreateDrawingContext());
                    scene.Render(context);
                }
            }

            target.WaitForGpu();

            sw.Stop();
            elapsedValues[repeat] = sw.Elapsed.TotalMilliseconds;
            allocatedValues[repeat] = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - startAlloc);
            if (firstFrameMs is not null)
                firstFrameMs[repeat] = firstMs;
            if (steadyMs is not null && iterations > 1)
                steadyMs[repeat] = (elapsedValues[repeat] - firstMs) / (iterations - 1);
        }
    }

    /// <summary>
    /// Skia GPU path: Angle EGL platform graphics + CreateOffscreenRenderTarget (GPU surface).
    /// Several Avalonia Skia GPU types are internal, so context creation uses a small reflection
    /// bridge over the public IPlatformGraphics / IPlatformRenderInterface surface.
    /// glFinish barriers make timing comparable to D2D's WaitForGpu.
    /// </summary>
    static void RunSkiaGpu(
        RenderBenchScene scene,
        int iterations,
        int repeats,
        double[] elapsedValues,
        long[] allocatedValues,
        string sessionMode)
    {
        using var session = SkiaGpuOffscreenSession.Create(scene.Size, scene.Dpi);

        using (var context = WrapDrawingContext(session.CreateDrawingContextImpl()))
            scene.Render(context);
        session.WaitForGpu();

        var batched = IsBatchedSession(sessionMode);
        for (var repeat = 0; repeat < repeats; repeat++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var startAlloc = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();

            if (batched)
            {
                using (var context = WrapDrawingContext(session.CreateDrawingContextImpl()))
                {
                    for (var i = 0; i < iterations; i++)
                        scene.Render(context);
                }
            }
            else
            {
                for (var i = 0; i < iterations; i++)
                {
                    using var context = WrapDrawingContext(session.CreateDrawingContextImpl());
                    scene.Render(context);
                }
            }

            session.WaitForGpu();

            sw.Stop();
            elapsedValues[repeat] = sw.Elapsed.TotalMilliseconds;
            allocatedValues[repeat] = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - startAlloc);
        }
    }

    static bool IsBatchedSession(string sessionMode)
        => string.Equals(sessionMode, "batched", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Pixel-diff D2D GPU vs D2D WIC for every scene.
    /// </summary>
    public static int VerifyGpuMatchesWic()
    {
        AppBuilder.Configure<BenchmarkApp>().UseWin32().UseDirect2D1().UseDirectWrite().SetupWithoutStarting();

        var allPassed = true;
        Console.WriteLine("GPU-vs-WIC pixel verification (Direct2D backend):");

        foreach (var scene in RenderBenchmarkScenes.All)
        {
            byte[] wicPixels;
            using (var bitmap = new RenderTargetBitmap(scene.Size, scene.Dpi))
            {
                using (var ctx = bitmap.CreateDrawingContext(false))
                    scene.Render(ctx);
                wicPixels = ReadRenderTargetBitmap(bitmap, scene.Size);
            }

            byte[] gpuPixels;
            using (var target = new Direct2DGpuOffscreenTarget(scene.Size, scene.Dpi))
            {
                using (var ctx = WrapDrawingContext(target.CreateDrawingContext()))
                    scene.Render(ctx);
                target.WaitForGpu();
                gpuPixels = target.ReadBgra();
            }

            var (meanDelta, maxDelta, nonBackgroundPct) = ComparePixels(wicPixels, gpuPixels);
            var passed = meanDelta <= 3.0 && nonBackgroundPct > 1.0;
            allPassed &= passed;

            Console.WriteLine(
                $"  {(passed ? "PASS" : "FAIL")} {scene.Name,-16} meanDelta={meanDelta:0.###} maxDelta={maxDelta} nonBackground={nonBackgroundPct:0.#}%");
        }

        Console.WriteLine(allPassed ? "All scenes match." : "MISMATCH — GPU path suspect.");
        return allPassed ? 0 : 1;
    }

    static byte[] ReadRenderTargetBitmap(RenderTargetBitmap bitmap, PixelSize size)
    {
        var buffer = new byte[size.Width * size.Height * 4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), handle.AddrOfPinnedObject(), buffer.Length, size.Width * 4);
        }
        finally
        {
            handle.Free();
        }
        return buffer;
    }

    static (double MeanDelta, int MaxDelta, double NonBackgroundPct) ComparePixels(byte[] a, byte[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        long sum = 0;
        var max = 0;
        var nonBackground = 0;
        var pixels = n / 4;

        for (var i = 0; i < n; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                var d = Math.Abs(a[i + c] - b[i + c]);
                sum += d;
                if (d > max) max = d;
            }
            if (a[i] < 250 || a[i + 1] < 250 || a[i + 2] < 250)
                nonBackground++;
        }

        return (sum / (pixels * 3.0), max, nonBackground * 100.0 / pixels);
    }

    // Avalonia's PlatformDrawingContext (which adapts an IDrawingContextImpl to the public
    // DrawingContext API) is internal. The scenes are written against DrawingContext, so wrap
    // GPU/impl contexts through it. ownsImpl:true so disposing the returned context ends the frame.
    // Constructor is cached to avoid Activator metadata lookup per iteration.
    static readonly ConstructorInfo s_platformDrawingContextCtor =
        (typeof(DrawingContext).Assembly.GetType("Avalonia.Media.PlatformDrawingContext")
            ?? throw new InvalidOperationException("Avalonia.Media.PlatformDrawingContext not found."))
        .GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(IDrawingContextImpl), typeof(bool)],
            modifiers: null)
        ?? throw new InvalidOperationException("PlatformDrawingContext(IDrawingContextImpl, bool) not found.");

    static readonly object[] s_wrapArgs = new object[2];

    internal static DrawingContext WrapDrawingContext(IDrawingContextImpl impl)
    {
        s_wrapArgs[0] = impl;
        s_wrapArgs[1] = true;
        return (DrawingContext)s_platformDrawingContextCtor.Invoke(s_wrapArgs);
    }

    /// <summary>
    /// Reflection bridge for Avalonia Skia GPU offscreen targets. Public surface is incomplete for
    /// headless GPU: CreateBackendContext / CreateOffscreenRenderTarget / GlInterface.Finish are
    /// accessible reflectively after UseSkia + AngleEgl setup.
    /// </summary>
    sealed class SkiaGpuOffscreenSession : IDisposable
    {
        static readonly MethodInfo s_createBackendContext =
            typeof(IPlatformRenderInterface).GetMethod("CreateBackendContext")
            ?? throw new InvalidOperationException("IPlatformRenderInterface.CreateBackendContext not found.");

        readonly IPlatformGraphicsContext _graphicsContext;
        readonly IDisposable _backend;
        readonly IDisposable _target;
        readonly MethodInfo _createDrawingContext;
        readonly MethodInfo? _glFinish;
        readonly object? _glInterface;
        bool _disposed;

        SkiaGpuOffscreenSession(
            IPlatformGraphicsContext graphicsContext,
            IDisposable backend,
            IDisposable target,
            MethodInfo createDrawingContext,
            object? glInterface,
            MethodInfo? glFinish)
        {
            _graphicsContext = graphicsContext;
            _backend = backend;
            _target = target;
            _createDrawingContext = createDrawingContext;
            _glInterface = glInterface;
            _glFinish = glFinish;
        }

        public static SkiaGpuOffscreenSession Create(PixelSize size, Vector dpi)
        {
            // AvaloniaLocator.Current is not always visible to all package reference graphs; resolve via reflection.
            var locator = typeof(AvaloniaLocator).GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                ?? throw new InvalidOperationException("AvaloniaLocator.Current is unavailable.");
            var getService = locator.GetType().GetMethod("GetService", [typeof(Type)])
                ?? throw new InvalidOperationException("AvaloniaLocator.GetService(Type) not found.");

            var graphics = getService.Invoke(locator, [typeof(IPlatformGraphics)]) as IPlatformGraphics
                ?? throw new InvalidOperationException(
                    "IPlatformGraphics is not available. Skia GPU requires AngleEgl rendering mode.");

            var render = getService.Invoke(locator, [typeof(IPlatformRenderInterface)]) as IPlatformRenderInterface
                ?? throw new InvalidOperationException("IPlatformRenderInterface is not registered.");
            var graphicsContext = graphics.UsesSharedContext ? graphics.GetSharedContext() : graphics.CreateContext();
            graphicsContext.EnsureCurrent();

            var backend = (IDisposable?)s_createBackendContext.Invoke(render, [graphicsContext])
                ?? throw new InvalidOperationException("CreateBackendContext returned null.");

            var createOff = backend.GetType().GetMethod(
                    "CreateOffscreenRenderTarget",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("CreateOffscreenRenderTarget not found on backend context.");

            object? targetObj;
            var parameters = createOff.GetParameters();
            if (parameters.Length >= 3 && parameters[2].ParameterType == typeof(bool))
                targetObj = createOff.Invoke(backend, [size, dpi, true]);
            else if (parameters.Length >= 2 && parameters[1].ParameterType == typeof(Vector))
                targetObj = createOff.Invoke(backend, [size, dpi]);
            else
                throw new InvalidOperationException("Unexpected CreateOffscreenRenderTarget signature: " + createOff);

            if (targetObj is not IDisposable target)
                throw new InvalidOperationException("Offscreen target is not disposable.");

            var createDc = target.GetType().GetMethod(
                    "CreateDrawingContext",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null)
                ?? throw new InvalidOperationException("CreateDrawingContext() not found on Skia offscreen target.");

            object? glInterface = null;
            MethodInfo? glFinish = null;
            var glProp = graphicsContext.GetType().GetProperty(
                "GlInterface",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (glProp?.GetValue(graphicsContext) is { } gl)
            {
                glInterface = gl;
                glFinish = gl.GetType().GetMethod("Finish", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? gl.GetType().GetMethod("Flush", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }

            // Prefer GRContext.Flush/Submit when present on the GPU surface.
            return new SkiaGpuOffscreenSession(graphicsContext, backend, target, createDc, glInterface, glFinish);
        }

        public IDrawingContextImpl CreateDrawingContextImpl()
        {
            EnsureNotDisposed();
            _graphicsContext.EnsureCurrent();
            var dc = _createDrawingContext.Invoke(_target, null)
                ?? throw new InvalidOperationException("CreateDrawingContext returned null.");
            return (IDrawingContextImpl)dc;
        }

        public void WaitForGpu()
        {
            EnsureNotDisposed();
            _graphicsContext.EnsureCurrent();

            // Flush Skia GPU work if a GRContext is attached to the surface.
            var grField = _target.GetType().GetField(
                "_grContext",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (grField?.GetValue(_target) is { } grContext)
            {
                grContext.GetType().GetMethod("Flush", Type.EmptyTypes)?.Invoke(grContext, null);
                grContext.GetType().GetMethod("Submit", [typeof(bool)])?.Invoke(grContext, [true]);
            }

            // Hard GPU barrier via OpenGL finish (Angle).
            _glFinish?.Invoke(_glInterface, null);
        }

        void EnsureNotDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SkiaGpuOffscreenSession));
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _target.Dispose();
            _backend.Dispose();
            _graphicsContext.Dispose();
        }
    }
}

internal sealed record RenderBenchmarkReport(
    DateTimeOffset TimestampUtc,
    IReadOnlyList<BackendBenchmarkResult> Results);

internal sealed record BackendBenchmarkResult(
    string Backend,
    string Scene,
    int Iterations,
    int Repeats,
    double MedianMs,
    double OpsPerSecond,
    long AllocatedBytes,
    /// <summary>D2D-GPU only: median wall time of the first timed frame (often still CL-warm after external warmup).</summary>
    double? FirstFrameMs = null,
    /// <summary>D2D-GPU only: median (total − first) / (iters − 1) after GPU barrier — steady per-frame cost.</summary>
    double? SteadyPerIterMs = null);

internal sealed record RenderBenchmarkOptions(
    string? Backend,
    HashSet<string> Scenes,
    int? Iterations,
    int Repeats,
    string? OutputJson,
    string SessionMode)
{
    public static RenderBenchmarkOptions Parse(string[] args)
    {
        string? backend = null;
        var scenes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int? iterations = null;
        var repeats = 5;
        string? outputJson = null;
        // per-iter: BeginDraw/EndDraw each iteration (matches real Avalonia frames).
        // batched: one session for all iterations (diagnostic — isolates draw cost from Begin/End).
        var sessionMode = "per-iter";

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
                case "--bench-render":
                case "--bench-render-worker":
                    break;
                case "--backend":
                    backend = NextValue();
                    break;
                case "--scene":
                    foreach (var part in NextValue().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        scenes.Add(part);
                    break;
                case "--iterations":
                    iterations = int.Parse(NextValue());
                    break;
                case "--repeats":
                    repeats = int.Parse(NextValue());
                    break;
                case "--json":
                    outputJson = NextValue();
                    break;
                case "--session-mode":
                    sessionMode = NextValue();
                    if (!string.Equals(sessionMode, "per-iter", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(sessionMode, "batched", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException(
                            "Unknown --session-mode. Supported: per-iter, batched");
                    }
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        return new RenderBenchmarkOptions(backend, scenes, iterations, repeats, outputJson, sessionMode);
    }
}

