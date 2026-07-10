using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using MIR.Direct2D1ForAvalonia;
using MIR.Direct2D1ForAvalonia.Media;
using MIR.DirectWriteForAvalonia;

namespace Benchmarks;

/// <summary>
/// Proves device-scoped command-list steady state: new DrawingContextImpl each paint
/// (composition-like) should still hit DrawImage after the first frame.
/// </summary>
internal static class DeviceClProofCommand
{
    public static int Run(string[] args)
    {
        var sceneName = "RoundedRectGrid";
        var iterations = 80;
        var repeats = 7;
        string? jsonPath = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--bench-device-cl":
                    break;
                case "--scene":
                    sceneName = args[++i];
                    break;
                case "--iterations":
                    iterations = int.Parse(args[++i]);
                    break;
                case "--repeats":
                    repeats = int.Parse(args[++i]);
                    break;
                case "--json":
                    jsonPath = args[++i];
                    break;
            }
        }

        var scene = RenderBenchmarkScenes.Get(sceneName);
        AppBuilder.Configure<BenchmarkApp>().UseWin32().UseDirect2D1().UseDirectWrite().SetupWithoutStarting();

        using var target = new Direct2DGpuOffscreenTarget(scene.Size, scene.Dpi);

        // --- Path A: reuse DrawingContextImpl (same host) ---
        var reuse = Measure(target, scene, iterations, repeats, forceNewDc: false);
        // --- Path B: new DrawingContextImpl every paint (composition intermediate-like) ---
        var fresh = Measure(target, scene, iterations, repeats, forceNewDc: true);

        var report = new
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Scene = scene.Name,
            Iterations = iterations,
            Repeats = repeats,
            ReuseDc = reuse,
            FreshDcEachPaint = fresh,
            Verdict = fresh.SteadyPerIterMs <= reuse.SteadyPerIterMs * 1.35
                ? "PASS: fresh-DC steady within 35% of reuse-DC (device CL effective)"
                : "WARN: fresh-DC much slower than reuse-DC (device CL may not be hitting)"
        };

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        Console.WriteLine();
        Console.WriteLine($"Device-CL proof ({scene.Name}):");
        Console.WriteLine($"  reuse DC:  cold={reuse.ColdMs:0.###}ms  steady/iter={reuse.SteadyPerIterMs:0.####}ms  clHits={reuse.ClHits} clStores={reuse.ClStores}");
        Console.WriteLine($"  fresh DC:  cold={fresh.ColdMs:0.###}ms  steady/iter={fresh.SteadyPerIterMs:0.####}ms  clHits={fresh.ClHits} clStores={fresh.ClStores}");
        Console.WriteLine($"  {report.Verdict}");

        if (jsonPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonPath)) ?? ".");
            File.WriteAllText(jsonPath, json);
        }

        // Require meaningful hits on fresh-DC path (composition-like).
        var minHits = iterations * repeats / 2;
        if (fresh.ClHits < minHits)
        {
            Console.Error.WriteLine(
                $"FAIL: expected device CL hits >= {minHits} on fresh-DC path, got {fresh.ClHits} (stores={fresh.ClStores})");
            return 2;
        }

        if (fresh.SteadyPerIterMs > reuse.SteadyPerIterMs * 1.5)
        {
            Console.Error.WriteLine("FAIL: fresh-DC steady cost too high vs reuse-DC");
            return 3;
        }

        return 0;
    }

    static ProofResult Measure(
        Direct2DGpuOffscreenTarget target,
        RenderBenchScene scene,
        int iterations,
        int repeats,
        bool forceNewDc)
    {
        // Cold: first paint (store CL).
        target.ResetDeviceCommandListStats();
        var coldSw = Stopwatch.StartNew();
        using (var ctx = RenderBenchmarkCommand.WrapDrawingContext(target.CreateDrawingContext(forceNewDc)))
            scene.Render(ctx);
        coldSw.Stop();
        target.WaitForGpu();
        var (_, storesAfterCold) = target.GetDeviceCommandListStats();

        // One diagnostic open to read batch path counters from the reusable impl (reuse path only).
        if (!forceNewDc)
        {
            var impl = (DrawingContextImpl)target.CreateDrawingContext(false);
            using (var wrap = RenderBenchmarkCommand.WrapDrawingContext(impl))
                scene.Render(wrap);
            var stats = impl.CommandListStats;
            Console.WriteLine(
                $"  [diag] deferredEnds={stats.DeferredEnds} liveEnds={stats.LiveEnds} " +
                $"batchHits={stats.Hits} batchMisses={stats.Misses} lastOps={stats.LastOps} " +
                $"recordError={stats.RecordError ?? "none"} deviceStores={target.GetDeviceCommandListStats().Stores}");
        }

        var steadySamples = new double[repeats];
        var hits = 0;
        var stores = 0;

        for (var r = 0; r < repeats; r++)
        {
            target.ResetDeviceCommandListStats();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            var sw = Stopwatch.StartNew();
            for (var i = 0; i < iterations; i++)
            {
                using var ctx = RenderBenchmarkCommand.WrapDrawingContext(target.CreateDrawingContext(forceNewDc));
                scene.Render(ctx);
            }

            target.WaitForGpu();
            sw.Stop();
            steadySamples[r] = sw.Elapsed.TotalMilliseconds / iterations;
            var (h, s) = target.GetDeviceCommandListStats();
            hits += h;
            stores += s;
        }

        Array.Sort(steadySamples);
        return new ProofResult(
            coldSw.Elapsed.TotalMilliseconds,
            steadySamples[repeats / 2],
            hits,
            stores + storesAfterCold);
    }

    private sealed record ProofResult(double ColdMs, double SteadyPerIterMs, int ClHits, int ClStores);
}
