using System.Diagnostics;
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
/// </summary>
internal static class RenderBenchmarkCommand
{
    public static int Run(RenderBenchmarkOptions options)
    {
        if (options.Backend is not null)
        {
            return RunWorker(options);
        }

        return RunController(options);
    }

    static int RunController(RenderBenchmarkOptions options)
    {
        var sceneNames = options.Scenes.Count == 0
            ? RenderBenchmarkScenes.All.Select(static x => x.Name).ToArray()
            : options.Scenes.ToArray();

        var allResults = new List<BackendBenchmarkResult>();

        foreach (var backend in new[] { "d2d", "skia" })
        {
            foreach (var sceneName in sceneNames)
            {
                var scene = RenderBenchmarkScenes.Get(sceneName);
                var result = RunWorkerProcess(backend, sceneName, scene.Iterations, options.Repeats);
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
        Console.WriteLine("D2D vs Skia Rendering Benchmark");
        Console.WriteLine(new string('-', 78));

        var byScene = report.Results
            .GroupBy(static x => x.Scene)
            .OrderBy(static x => x.Key);

        foreach (var group in byScene)
        {
            var d2d = group.FirstOrDefault(static x => x.Backend == "d2d");
            var skia = group.FirstOrDefault(static x => x.Backend == "skia");

            Console.WriteLine();
            Console.WriteLine($"  {group.Key} ({d2d?.Iterations ?? 0} iters x {d2d?.Repeats ?? 0} repeats):");
            Console.WriteLine($"    D2D:   {d2d?.MedianMs:0.###} ms  ({d2d?.OpsPerSecond:0.###} ops/s)  alloc={d2d?.AllocatedBytes ?? 0} B");
            Console.WriteLine($"    Skia:  {skia?.MedianMs:0.###} ms  ({skia?.OpsPerSecond:0.###} ops/s)  alloc={skia?.AllocatedBytes ?? 0} B");

            if (d2d is not null && skia is not null && skia.MedianMs > 0)
            {
                var ratio = d2d.MedianMs / skia.MedianMs;
                var faster = ratio < 1.0 ? "D2D faster" : "Skia faster";
                Console.WriteLine($"    Ratio: {ratio:0.###}x ({faster} by {Math.Abs(1.0 - ratio) * 100:0.#}%)");
            }
        }

        Console.WriteLine();
    }

    static BackendBenchmarkResult RunWorkerProcess(string backend, string scene, int iterations, int repeats)
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

        var builder = AppBuilder.Configure<BenchmarkApp>().UseWin32();

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

        using var bitmap = new RenderTargetBitmap(scene.Size, scene.Dpi);

        // Warmup
        using (var context = bitmap.CreateDrawingContext(false))
        {
            scene.Render(context);
        }

        var elapsedValues = new double[repeats];
        var allocatedValues = new long[repeats];

        for (var repeat = 0; repeat < repeats; repeat++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var startAlloc = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();

            for (var i = 0; i < iterations; i++)
            {
                using var context = bitmap.CreateDrawingContext(false);
                scene.Render(context);
            }

            sw.Stop();
            elapsedValues[repeat] = sw.Elapsed.TotalMilliseconds;
            allocatedValues[repeat] = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - startAlloc);
        }

        Array.Sort(elapsedValues);
        Array.Sort(allocatedValues);

        var median = elapsedValues[repeats / 2];
        var medianAlloc = allocatedValues[repeats / 2];
        var opsPerSecond = iterations / (median / 1000.0);

        var result = new BackendBenchmarkResult(
            backend,
            sceneName,
            iterations,
            repeats,
            median,
            opsPerSecond,
            medianAlloc);

        Console.Write(JsonSerializer.Serialize(result));
        return 0;
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
    long AllocatedBytes);

internal sealed record RenderBenchmarkOptions(
    string? Backend,
    HashSet<string> Scenes,
    int? Iterations,
    int Repeats,
    string? OutputJson)
{
    public static RenderBenchmarkOptions Parse(string[] args)
    {
        string? backend = null;
        var scenes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int? iterations = null;
        var repeats = 5;
        string? outputJson = null;

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
                    // controller mode flag — no value
                    break;
                case "--bench-render-worker":
                    // worker mode flag — no value; backend comes from --backend
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
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        return new RenderBenchmarkOptions(backend, scenes, iterations, repeats, outputJson);
    }
}
