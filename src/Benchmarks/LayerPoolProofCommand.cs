using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Win32;
using MIR.Direct2D1ForAvalonia;
using MIR.Direct2D1ForAvalonia.Media.Imaging;
using MIR.DirectWriteForAvalonia;

namespace Benchmarks;

/// <summary>
/// Proves composition layer pool steady-state hits under multi-size CreateLayer/Dispose churn.
/// Does not go through Avalonia's compositor (which often holds a single layer); drives
/// <see cref="ILayerFactory.CreateLayer"/> directly like a hostile visual tree.
/// </summary>
internal static class LayerPoolProofCommand
{
    public static int Run(string[] args)
    {
        var iterations = 80;
        var sizes = new[] { 64.0, 96.0, 128.0, 192.0, 256.0 };
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--iterations" && i + 1 < args.Length)
                iterations = int.Parse(args[++i]);
        }

        AppBuilder.Configure<BenchmarkApp>()
            .UseWin32()
            .UseDirect2D1()
            .UseDirectWrite()
            .SetupWithoutStarting();

        D2DCompatibleLayerPool.ResetStats();
        D2DCompatibleLayerPool.Clear();
        D2DCompatibleLayerPool.ResetStats();

        using var target = new Direct2DGpuOffscreenTarget(new PixelSize(512, 512), new Vector(96, 96));

        // Warm: create+dispose each size once (all misses).
        foreach (var s in sizes)
        {
            using var layer = (D2DRenderTargetBitmapImpl)target.CreateLayer(new Size(s, s));
            using var dc = layer.CreateDrawingContext(useScaledDrawing: false);
            // Touch the session so clear-on-rent path runs.
        }

        var hitsBefore = D2DCompatibleLayerPool.Hits;
        var missesBefore = D2DCompatibleLayerPool.Misses;

        // Steady: same sizes again — should be pool hits.
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            foreach (var s in sizes)
            {
                using var layer = (D2DRenderTargetBitmapImpl)target.CreateLayer(new Size(s, s));
                using (var dc = layer.CreateDrawingContext(useScaledDrawing: false))
                {
                    // Leave transparent (clear-on-open already ran) — Skia-like empty layer.
                }
            }
        }
        sw.Stop();

        var hits = D2DCompatibleLayerPool.Hits - hitsBefore;
        var misses = D2DCompatibleLayerPool.Misses - missesBefore;
        var expectedHits = iterations * sizes.Length;
        // Allow a few misses if quantisation/DPI edge cases; require strong hit rate.
        var hitRate = expectedHits > 0 ? hits / (double)(hits + misses) : 0;
        var pass = hits >= expectedHits * 0.9 && hitRate >= 0.9;

        var report = new
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Iterations = iterations,
            Sizes = sizes,
            SteadyHits = hits,
            SteadyMisses = misses,
            ExpectedHits = expectedHits,
            HitRate = hitRate,
            ElapsedMs = sw.Elapsed.TotalMilliseconds,
            PoolStats = new
            {
                D2DCompatibleLayerPool.Hits,
                D2DCompatibleLayerPool.Misses,
                D2DCompatibleLayerPool.Returns,
                D2DCompatibleLayerPool.Discards,
                D2DCompatibleLayerPool.PooledCount,
            },
            Verdict = pass
                ? "PASS: multi-size CreateLayer/Dispose hits the composition layer pool"
                : "FAIL: layer pool hit rate too low",
        };

        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine();
        Console.WriteLine(report.Verdict);
        Console.WriteLine(
            $"  steady hits={hits} misses={misses} expected≈{expectedHits} hitRate={hitRate:P1} " +
            $"elapsed={sw.Elapsed.TotalMilliseconds:0.###}ms");

        return pass ? 0 : 1;
    }
}
