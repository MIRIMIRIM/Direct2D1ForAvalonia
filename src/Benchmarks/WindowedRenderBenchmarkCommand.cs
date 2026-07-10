using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Win32;
using MIR.Direct2D1ForAvalonia;
using MIR.Direct2D1ForAvalonia.Diagnostics;
using MIR.DirectWriteForAvalonia;

namespace Benchmarks;

/// <summary>
/// Real-window render timing for Direct2D via Avalonia composition
/// (<c>D3D11TextureRenderTarget</c> + texture session Present hand-off).
/// <para>
/// Unlike offscreen <c>d2d-gpu</c> benches, this does <b>not</b> call
/// <see cref="Direct2DGpuOffscreenTarget.WaitForGpu"/> — frame cost matches what a
/// live UI measures (submit + Flush + composition Present path).
/// </para>
/// <para>
/// Note: ~16.7 ms wall / ~60 fps on a 60 Hz panel is the <b>display refresh / vsync</b>
/// ceiling, not an Avalonia or D2D hard FPS cap. Session time is the actual D2D budget used.
/// </para>
/// </summary>
internal static class WindowedRenderBenchmarkCommand
{
    public static int Run(string[] args)
    {
        var options = WindowedBenchmarkOptions.Parse(args);
        var scenes = options.Scenes.Count > 0
            ? options.Scenes
            : new List<string> { options.Scene };

        // Multi-scene matrix: run each scene in its own process-style AppBuilder pass so
        // composition/device state stays isolated. Emits a compare table + per-scene JSON.
        if (scenes.Count > 1)
            return RunMatrix(args, options, scenes);

        return RunOne(options, scenes[0]);
    }

    static int RunMatrix(string[] args, WindowedBenchmarkOptions template, List<string> scenes)
    {
        // Avalonia Setup can only run once per process — spawn a worker per scene.
        var rows = new List<WindowedBenchmarkReport>();
        var worst = 0;
        var outDir = Path.GetDirectoryName(Path.GetFullPath(
            template.OutputJson ?? Path.Combine("artifacts", "render-bench", "windowed-matrix.json")))
            ?? Path.Combine("artifacts", "render-bench");
        Directory.CreateDirectory(outDir);

        foreach (var scene in scenes)
        {
            var sceneJson = Path.Combine(outDir, $"windowed-{scene.ToLowerInvariant()}-matrix-part.json");
            var code = RunWindowedWorkerProcess(template, scene, sceneJson);
            if (code != 0)
            {
                worst = code;
                Console.Error.WriteLine($"Windowed worker for {scene} exited {code}.");
                continue;
            }

            if (!File.Exists(sceneJson))
            {
                Console.Error.WriteLine($"Missing worker JSON for {scene}: {sceneJson}");
                worst = worst == 0 ? 2 : worst;
                continue;
            }

            var report = JsonSerializer.Deserialize<WindowedBenchmarkReport>(File.ReadAllText(sceneJson));
            if (report is null)
            {
                Console.Error.WriteLine($"Failed to parse worker JSON for {scene}.");
                worst = worst == 0 ? 2 : worst;
                continue;
            }

            rows.Add(report);
            Console.WriteLine(
                $"  [{scene}] off/iter={report.Offscreen.PerIterationMs:0.###}ms  " +
                $"session={report.Windowed.MedianSessionTotalMs:0.###}ms  " +
                $"wall={report.Windowed.WallMedianFrameMs:0.###}ms  " +
                $"sess/off={(report.Offscreen.PerIterationMs > 0 ? report.Windowed.MedianSessionTotalMs / report.Offscreen.PerIterationMs : 0):0.###}x");
        }

        PrintMatrix(rows);
        var matrixPath = template.OutputJson
            ?? Path.Combine(outDir, "windowed-matrix.json");
        File.WriteAllText(
            matrixPath,
            JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine();
        Console.WriteLine($"matrix-json={Path.GetFullPath(matrixPath)}");
        return worst;
    }

    static int RunWindowedWorkerProcess(WindowedBenchmarkOptions template, string scene, string jsonPath)
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot resolve process path.");

        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--bench-windowed");
        psi.ArgumentList.Add("--scene");
        psi.ArgumentList.Add(scene);
        psi.ArgumentList.Add("--warmup");
        psi.ArgumentList.Add(template.WarmupFrames.ToString());
        psi.ArgumentList.Add("--frames");
        psi.ArgumentList.Add(template.MeasureFrames.ToString());
        // Let each worker resolve scene-default iterations (matrix first-scene default is wrong).
        psi.ArgumentList.Add("--offscreen-repeats");
        psi.ArgumentList.Add(template.OffscreenRepeats.ToString());
        psi.ArgumentList.Add("--timeout");
        psi.ArgumentList.Add(template.TimeoutSeconds.ToString());
        psi.ArgumentList.Add("--stress-copies");
        psi.ArgumentList.Add(template.StressCopies.ToString());
        psi.ArgumentList.Add("--visual-tree-grid");
        psi.ArgumentList.Add(template.VisualTreeGrid.ToString());
        if (template.CompositionMode is { } composition)
        {
            psi.ArgumentList.Add("--composition");
            psi.ArgumentList.Add(composition.ToString());
        }
        if (template.RenderOnUiThread)
            psi.ArgumentList.Add("--render-on-ui-thread");
        psi.ArgumentList.Add("--json");
        psi.ArgumentList.Add(jsonPath);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start windowed worker for {scene}.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            Console.Error.WriteLine(stderr.Length > 0 ? stderr : stdout);
        else if (stdout.Length > 0)
            Console.WriteLine(stdout);
        return process.ExitCode;
    }

    static int RunOne(WindowedBenchmarkOptions options, string sceneName, List<WindowedBenchmarkReport>? capture = null)
    {
        var scene = RenderBenchmarkScenes.Get(sceneName);
        // Keep options.Scene in sync for WindowedBenchApp title / report.
        options = options with { Scene = scene.Name };

        WindowedBenchState? state = null;
        OffscreenResult? offscreen = null;
        var exitCode = 0;

        Direct2D1FrameProfiler.Enable();
        Direct2D1FrameProfiler.Reset();
        DrawingContextCallStats.Enable();

        DrawingContextCallStatsSnapshot? globalStats = null;
        try
        {
            var builder = AppBuilder.Configure(() => new WindowedBenchApp(
                    scene,
                    options,
                    (o, s, g) =>
                    {
                        offscreen = o;
                        state = s;
                        globalStats = g;
                    }))
                .UseWin32()
                .UseDirect2D1()
                .UseDirectWrite();

            // Composition mode can change Present pacing. Default Avalonia path often lands on
            // WinUI/DirectComposition (vsync with the display). RedirectionSurface / LowLatency
            // are useful comparison points — still not a guaranteed "unlimited FPS" mode.
            if (options.CompositionMode is { } composition)
            {
                builder = builder.With(new Win32PlatformOptions
                {
                    CompositionMode = [composition],
                    ShouldRenderOnUIThread = options.RenderOnUiThread,
                });
            }
            else if (options.RenderOnUiThread)
            {
                builder = builder.With(new Win32PlatformOptions
                {
                    ShouldRenderOnUIThread = true,
                });
            }

            exitCode = builder.StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
        }
        finally
        {
            Direct2D1FrameProfiler.Disable();
            DrawingContextCallStats.Disable();
        }

        if (state is null || offscreen is null)
        {
            Console.Error.WriteLine($"Windowed bench did not produce a result state for {sceneName}.");
            return exitCode != 0 ? exitCode : 2;
        }

        var summary = Direct2D1FrameProfiler.Summarize(skipFirst: options.WarmupFrames);
        var globals = globalStats ?? DrawingContextCallStats.Snapshot();
        var report = new WindowedBenchmarkReport(
            DateTimeOffset.UtcNow,
            options.Scene,
            options.StressCopies,
            options.VisualTreeGrid,
            options.CompositionMode?.ToString() ?? "default",
            offscreen,
            new WindowedResult(
                state.MeasuredFrames,
                state.WarmupFrames,
                state.WallMedianFrameMs,
                state.WallP95FrameMs,
                state.WallFps,
                summary.MedianTotalMs,
                summary.MedianSurfaceBeginMs,
                summary.MedianSetupMs,
                summary.MedianDrawGapMs,
                summary.MedianEndDrawMs,
                summary.MedianFlushMs,
                summary.MedianCleanupMs,
                summary.P95TotalMs,
                summary.MeanSoftHits,
                summary.MeanSoftMisses,
                summary.MeanLayerPushes,
                summary.MeanDeferredFlushes,
                summary.MeanPushClips,
                summary.MeanPushOpacities,
                summary.MeanDrawRectangles),
            new GlobalCallStats(
                globals.Sessions,
                globals.DrawRectangles,
                globals.PushClips,
                globals.PushOpacities,
                globals.SoftHits,
                globals.SoftMisses,
                globals.LayerPushes,
                globals.SessionsByTarget.Select(static kv => $"{kv.Key}={kv.Value}").ToArray(),
                globals.DrawByTarget.Select(static kv => $"{kv.Key}={kv.Value}").ToArray(),
                globals.SoftByTarget.Select(static kv => $"{kv.Key}={kv.Value}").ToArray()));

        capture?.Add(report);

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        var modeTag = options.VisualTreeGrid > 1 ? $"grid{options.VisualTreeGrid}" : $"x{options.StressCopies}";
        var outputPath = options.OutputJson
            ?? Path.Combine("artifacts", "render-bench",
                $"windowed-{options.Scene.ToLowerInvariant()}-{modeTag}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
        File.WriteAllText(outputPath, json);

        PrintReport(report, summary, globals);
        Console.WriteLine();
        Console.WriteLine($"json={Path.GetFullPath(outputPath)}");
        return state.TimedOut ? 3 : exitCode;
    }

    internal static OffscreenResult MeasureOffscreenPublic(
        RenderBenchScene scene,
        int iterations,
        int repeats,
        int stressCopies)
    {
        using var target = new Direct2DGpuOffscreenTarget(scene.Size, scene.Dpi);

        void RenderOnce(DrawingContext ctx)
        {
            for (var c = 0; c < stressCopies; c++)
                scene.Render(ctx);
        }

        using (var ctx = RenderBenchmarkCommand.WrapDrawingContext(target.CreateDrawingContext()))
            RenderOnce(ctx);
        target.WaitForGpu();

        var elapsed = new double[repeats];
        for (var r = 0; r < repeats; r++)
        {
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < iterations; i++)
            {
                using var ctx = RenderBenchmarkCommand.WrapDrawingContext(target.CreateDrawingContext());
                RenderOnce(ctx);
            }
            target.WaitForGpu();
            sw.Stop();
            elapsed[r] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(elapsed);
        var median = elapsed[repeats / 2];
        return new OffscreenResult(
            iterations,
            repeats,
            median,
            median / iterations,
            iterations / (median / 1000.0));
    }

    static void PrintReport(
        WindowedBenchmarkReport report,
        FrameProfilerSummary summary,
        DrawingContextCallStatsSnapshot globals)
    {
        Console.WriteLine();
        Console.WriteLine("Direct2D windowed vs offscreen (no WaitForGpu on window path)");
        Console.WriteLine(new string('-', 72));
        Console.WriteLine(
            $"  scene={report.Scene}  stressCopies={report.StressCopies}  " +
            $"visualTreeGrid={report.VisualTreeGrid}  composition={report.CompositionMode}");
        Console.WriteLine();
        Console.WriteLine("  Offscreen d2d-gpu (Begin/End × N + WaitForGpu barrier):");
        Console.WriteLine(
            $"    median batch={report.Offscreen.MedianBatchMs:0.###}ms  " +
            $"per-iter={report.Offscreen.PerIterationMs:0.###}ms  " +
            $"({report.Offscreen.OpsPerSecond:0.#} ops/s, " +
            $"{report.Offscreen.Iterations} iters × {report.Offscreen.Repeats} repeats)");
        Console.WriteLine();
        Console.WriteLine("  Windowed (composition Present hand-off, wall clock between paints):");
        Console.WriteLine(
            $"    frames={report.Windowed.MeasuredFrames} (warmup {report.Windowed.WarmupFrames})  " +
            $"median={report.Windowed.WallMedianFrameMs:0.###}ms  " +
            $"p95={report.Windowed.WallP95FrameMs:0.###}ms  " +
            $"~{report.Windowed.WallFps:0.#} fps");
        Console.WriteLine();
        Console.WriteLine("  Window-surface D3D11 session profile (after warmup):");
        Console.WriteLine(Direct2D1FrameProfiler.FormatSummary(summary));
        Console.WriteLine();
        Console.WriteLine("  Process-wide DrawingContextImpl calls during window phase only:");
        Console.WriteLine(DrawingContextCallStats.Format(globals));
        Console.WriteLine();
        Console.WriteLine(
            $"  Composition layer pool: hits={MIR.Direct2D1ForAvalonia.Media.Imaging.D2DCompatibleLayerPool.Hits}  " +
            $"misses={MIR.Direct2D1ForAvalonia.Media.Imaging.D2DCompatibleLayerPool.Misses}  " +
            $"returns={MIR.Direct2D1ForAvalonia.Media.Imaging.D2DCompatibleLayerPool.Returns}  " +
            $"discards={MIR.Direct2D1ForAvalonia.Media.Imaging.D2DCompatibleLayerPool.Discards}  " +
            $"pooled={MIR.Direct2D1ForAvalonia.Media.Imaging.D2DCompatibleLayerPool.PooledCount}");
        Console.WriteLine();

        var phases = report.Windowed;
        var presentish = phases.MedianCleanupMs + phases.MedianFlushMs + phases.MedianEndDrawMs;
        var drawish = phases.MedianDrawGapMs;
        var session = phases.MedianSessionTotalMs;
        var wall = phases.WallMedianFrameMs;

        Console.WriteLine("  Interpretation:");
        Console.WriteLine(
            $"    endDraw+flush+cleanup ≈ {presentish:0.###}ms  |  drawGap ≈ {drawish:0.###}ms  |  session ≈ {session:0.###}ms  |  wall ≈ {wall:0.###}ms");

        if (wall > 0 && session > 0 && wall > session * 2)
        {
            Console.WriteLine(
                $"    → Wall ({wall:0.###}ms) ≫ D2D session ({session:0.###}ms): " +
                "display refresh / compositor pacing (vsync), NOT an Avalonia hard FPS ceiling.");
            if (Math.Abs(wall - 16.667) < 1.5)
                Console.WriteLine("    → Wall ≈ 16.7ms → classic 60Hz panel vsync lock.");
            else if (Math.Abs(wall - 8.333) < 1.0)
                Console.WriteLine("    → Wall ≈ 8.3ms → classic 120Hz panel vsync lock.");
            Console.WriteLine(
                $"    → Headroom: ~{Math.Max(0, wall - session):0.###}ms idle per frame waiting on present/refresh.");
        }
        else if (wall > 0 && session > wall * 0.85)
        {
            Console.WriteLine(
                "    → Session ≈ wall: draw/submit is now FPS-bound (exceeded vsync budget).");
        }

        if (drawish > presentish * 1.5)
            Console.WriteLine("    → Inside window session: scene/composition work dominates submit.");
        else if (presentish > drawish * 1.5)
            Console.WriteLine("    → Inside window session: Present/submit dominates.");

        // Where did draws land?
        if (globals.DrawRectangles == 0)
        {
            Console.WriteLine(
                "    → No DrawingContextImpl.DrawRectangle anywhere during window phase — unexpected; " +
                "check that D2D backend is active.");
        }
        else if (phases.MeanDrawRectangles < 0.5 && globals.DrawRectangles > 0)
        {
            Console.WriteLine(
                "    → Window-surface session drawRect≈0 but process-wide drawRect>0: " +
                "composition replays Control.Render onto intermediate DrawingContextImpl hosts " +
                "(not the window D3D11TextureRenderTarget session counters). Soft-path still applies there.");
            if (globals.SoftHits > 0)
                Console.WriteLine(
                    $"    → Soft path IS active on those intermediates (softHits={globals.SoftHits}).");
            else if (globals.PushClips > 0 || globals.PushOpacities > 0)
                Console.WriteLine(
                    "    → Clip/opacity reach DrawingContextImpl but soft path did not hit — eligibility/flush issue.");
        }
        else if (globals.SoftHits > 0)
        {
            Console.WriteLine(
                $"    → Soft path active (process softHits={globals.SoftHits}, " +
                $"surface softHits/frame≈{phases.MeanSoftHits:0.#}).");
        }

        // Explicit offscreen vs window diagnostic ratios (decision aid for next optimisations).
        var offPer = report.Offscreen.PerIterationMs;
        Console.WriteLine();
        Console.WriteLine("  Offscreen vs window ratios (stressCopies matched on both sides):");
        if (offPer > 0)
        {
            Console.WriteLine(
                $"    session/offscreen-per-iter = {session / offPer:0.###}x  " +
                $"(1.0 ≈ same draw cost; ≫1 → composition/Present overhead or vsync wait in session)");
            Console.WriteLine(
                $"    wall/offscreen-per-iter    = {wall / offPer:0.###}x  " +
                $"(≫1 expected under vsync; compare session ratio for real work)");
            if (session / offPer > 2.0 && session > 1.0)
                Console.WriteLine(
                    "    → Window session is much slower than offscreen: investigate composition " +
                    "intermediates, CreateLayer, or Present path — not offscreen-only draw opts.");
            else if (session / offPer < 1.5)
                Console.WriteLine(
                    "    → Window session ≈ offscreen: further wins are mostly draw-primitive work.");
        }
    }

    static void PrintMatrix(List<WindowedBenchmarkReport> rows)
    {
        Console.WriteLine();
        Console.WriteLine("Windowed matrix — offscreen vs window session (decision table)");
        Console.WriteLine(new string('-', 100));
        Console.WriteLine(
            $"  {"Scene",-18} {"Off/iter",8} {"Sess",8} {"Wall",8} {"Sess/Off",9} {"Wall/Off",9}  note");
        Console.WriteLine(new string('-', 100));
        foreach (var r in rows)
        {
            var off = r.Offscreen.PerIterationMs;
            var sess = r.Windowed.MedianSessionTotalMs;
            var wall = r.Windowed.WallMedianFrameMs;
            var sOff = off > 0 ? sess / off : 0;
            var wOff = off > 0 ? wall / off : 0;
            // Absolute session ms is often <1 on modern GPUs; classify by ratio only.
            var note = sOff > 2.0
                ? "composition-heavy"
                : sOff < 1.5
                    ? "draw-bound"
                    : "mixed";
            Console.WriteLine(
                $"  {r.Scene,-18} {off,8:0.###} {sess,8:0.###} {wall,8:0.###} {sOff,9:0.###} {wOff,9:0.###}  {note}");
        }
    }
}

internal sealed class WindowedBenchApp : Application
{
    private readonly RenderBenchScene _scene;
    private readonly WindowedBenchmarkOptions _options;
    private readonly Action<OffscreenResult, WindowedBenchState, DrawingContextCallStatsSnapshot> _onCompleted;

    public WindowedBenchApp(
        RenderBenchScene scene,
        WindowedBenchmarkOptions options,
        Action<OffscreenResult, WindowedBenchState, DrawingContextCallStatsSnapshot> onCompleted)
    {
        _scene = scene;
        _options = options;
        _onCompleted = onCompleted;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            throw new InvalidOperationException("Classic desktop lifetime required for windowed bench.");

        var offscreen = WindowedRenderBenchmarkCommand.MeasureOffscreenPublic(
            _scene,
            _options.OffscreenIterations,
            _options.OffscreenRepeats,
            _options.StressCopies);

        // Window-phase stats only (drop offscreen noise).
        Direct2D1FrameProfiler.Reset();
        DrawingContextCallStats.Reset();
        MIR.Direct2D1ForAvalonia.Media.Imaging.D2DCompatibleLayerPool.ResetStats();

        Control content;
        IFrameCounter frameCounter;
        if (_options.VisualTreeGrid > 1)
        {
            var grid = new VisualTreeBenchPanel(_scene, _options.VisualTreeGrid);
            content = grid;
            frameCounter = grid;
        }
        else
        {
            var surface = new WindowedBenchSurface(_scene, _options.StressCopies);
            content = surface;
            frameCounter = surface;
        }

        var window = new Window
        {
            Title = _options.VisualTreeGrid > 1
                ? $"D2D windowed — {_scene.Name} grid{_options.VisualTreeGrid}"
                : $"D2D windowed — {_scene.Name} ×{_options.StressCopies}",
            Width = content.Width > 0 ? content.Width : _scene.Size.Width,
            Height = content.Height > 0 ? content.Height : _scene.Size.Height,
            CanResize = false,
            ShowActivated = false,
            Topmost = true,
            Background = Brushes.Black,
            Content = content,
            Position = new PixelPoint(40, 40)
        };

        desktop.MainWindow = window;
        window.Show();

        var warmup = _options.WarmupFrames;
        var measure = _options.MeasureFrames;
        var total = warmup + measure;
        var frameDts = new List<double>(measure);
        long? lastRenderTs = null;
        var lastRenderCount = 0;
        var started = Stopwatch.GetTimestamp();
        var timeoutTicks = Stopwatch.Frequency * (long)Math.Max(15, _options.TimeoutSeconds);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1) };
        timer.Tick += (_, _) =>
        {
            frameCounter.RequestFrame();

            var now = Stopwatch.GetTimestamp();
            var count = frameCounter.RenderCount;
            if (count > lastRenderCount)
            {
                if (lastRenderTs is long prev && count > warmup && frameDts.Count < measure)
                {
                    var framesAdvanced = count - lastRenderCount;
                    var dtMs = (now - prev) * 1000.0 / Stopwatch.Frequency / framesAdvanced;
                    for (var i = 0; i < framesAdvanced && frameDts.Count < measure; i++)
                        frameDts.Add(dtMs);
                }

                lastRenderTs = now;
                lastRenderCount = count;
            }

            var done = count >= total && frameDts.Count >= measure;
            var timedOut = (now - started) > timeoutTicks;
            if (!done && !timedOut)
                return;

            timer.Stop();

            frameDts.Sort();
            double Median(List<double> xs) => xs.Count == 0 ? 0 : xs[xs.Count / 2];
            double P95(List<double> xs)
            {
                if (xs.Count == 0) return 0;
                var idx = (int)Math.Clamp(Math.Ceiling(0.95 * xs.Count) - 1, 0, xs.Count - 1);
                return xs[idx];
            }

            var median = Median(frameDts);
            var state = new WindowedBenchState(
                WarmupFrames: warmup,
                MeasuredFrames: frameDts.Count,
                WallMedianFrameMs: median,
                WallP95FrameMs: P95(frameDts),
                WallFps: median > 0 ? 1000.0 / median : 0,
                TimedOut: timedOut);

            _onCompleted(offscreen, state, DrawingContextCallStats.Snapshot());
            desktop.Shutdown(timedOut ? 3 : 0);
        };

        timer.Start();
        base.OnFrameworkInitializationCompleted();
    }
}

internal interface IFrameCounter
{
    int RenderCount { get; }
    void RequestFrame();
}

internal sealed class WindowedBenchSurface : Control, IFrameCounter
{
    private readonly RenderBenchScene _scene;
    private readonly int _stressCopies;

    public WindowedBenchSurface(RenderBenchScene scene, int stressCopies)
    {
        _scene = scene;
        _stressCopies = Math.Max(1, stressCopies);
        ClipToBounds = true;
        Width = scene.Size.Width;
        Height = scene.Size.Height;
    }

    public int RenderCount { get; private set; }

    public void RequestFrame() => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        RenderCount++;
        for (var i = 0; i < _stressCopies; i++)
            _scene.Render(context);
    }
}

/// <summary>
/// Grid of independent Controls each painting the scene — closer to a real visual tree
/// (many composition visuals) than replaying one Control.Render N times.
/// </summary>
internal sealed class VisualTreeBenchPanel : Panel, IFrameCounter
{
    private readonly List<SceneCell> _cells = new();

    public VisualTreeBenchPanel(RenderBenchScene scene, int grid)
    {
        grid = Math.Clamp(grid, 2, 16);
        var baseW = Math.Max(48, scene.Size.Width / grid);
        var baseH = Math.Max(48, scene.Size.Height / grid);
        // Slightly different cell sizes so CreateLayer keys diversify (pool stress).
        // Still tile without overlap using each cell's own pitch.
        double totalW = 0, totalH = 0;
        var pitchesX = new double[grid];
        var pitchesY = new double[grid];
        for (var i = 0; i < grid; i++)
        {
            pitchesX[i] = baseW + (i % 3) * 12;
            pitchesY[i] = baseH + (i % 2) * 10;
            totalW += pitchesX[i];
            totalH += pitchesY[i];
        }

        Width = totalW;
        Height = totalH;
        Background = Brushes.Black;

        double yOff = 0;
        for (var y = 0; y < grid; y++)
        {
            double xOff = 0;
            for (var x = 0; x < grid; x++)
            {
                var cellW = pitchesX[x];
                var cellH = pitchesY[y];
                var cell = new SceneCell(scene, cellW, cellH)
                {
                    Width = cellW,
                    Height = cellH,
                    Margin = new Thickness(xOff, yOff, 0, 0),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                };
                _cells.Add(cell);
                Children.Add(cell);
                xOff += cellW;
            }

            yOff += pitchesY[y];
        }
    }

    public int RenderCount { get; private set; }

    public void RequestFrame()
    {
        RenderCount++;
        foreach (var cell in _cells)
            cell.InvalidateVisual();
        InvalidateVisual();
    }

    private sealed class SceneCell : Control
    {
        private readonly RenderBenchScene _scene;

        public SceneCell(RenderBenchScene scene, double w, double h)
        {
            _scene = scene;
            Width = w;
            Height = h;
            ClipToBounds = true;
        }

        public override void Render(DrawingContext context)
        {
            // Scale scene (authored for 512²) into the cell.
            var sx = Bounds.Width / _scene.Size.Width;
            var sy = Bounds.Height / _scene.Size.Height;
            using (context.PushTransform(Matrix.CreateScale(sx, sy)))
                _scene.Render(context);
        }
    }
}

internal sealed record WindowedBenchState(
    int WarmupFrames,
    int MeasuredFrames,
    double WallMedianFrameMs,
    double WallP95FrameMs,
    double WallFps,
    bool TimedOut);

internal sealed record OffscreenResult(
    int Iterations,
    int Repeats,
    double MedianBatchMs,
    double PerIterationMs,
    double OpsPerSecond);

internal sealed record WindowedResult(
    int MeasuredFrames,
    int WarmupFrames,
    double WallMedianFrameMs,
    double WallP95FrameMs,
    double WallFps,
    double MedianSessionTotalMs,
    double MedianSurfaceBeginMs,
    double MedianSetupMs,
    double MedianDrawGapMs,
    double MedianEndDrawMs,
    double MedianFlushMs,
    double MedianCleanupMs,
    double P95SessionTotalMs,
    double MeanSoftHits,
    double MeanSoftMisses,
    double MeanLayerPushes,
    double MeanDeferredFlushes,
    double MeanPushClips,
    double MeanPushOpacities,
    double MeanDrawRectangles);

internal sealed record GlobalCallStats(
    long Sessions,
    long DrawRectangles,
    long PushClips,
    long PushOpacities,
    long SoftHits,
    long SoftMisses,
    long LayerPushes,
    string[] SessionsByTarget,
    string[] DrawByTarget,
    string[] SoftByTarget);

internal sealed record WindowedBenchmarkReport(
    DateTimeOffset TimestampUtc,
    string Scene,
    int StressCopies,
    int VisualTreeGrid,
    string CompositionMode,
    OffscreenResult Offscreen,
    WindowedResult Windowed,
    GlobalCallStats GlobalCalls);

internal sealed record WindowedBenchmarkOptions(
    string Scene,
    int WarmupFrames,
    int MeasureFrames,
    int OffscreenIterations,
    int OffscreenRepeats,
    int TimeoutSeconds,
    int StressCopies,
    int VisualTreeGrid,
    Win32CompositionMode? CompositionMode,
    bool RenderOnUiThread,
    string? OutputJson,
    List<string> Scenes)
{
    public static WindowedBenchmarkOptions Parse(string[] args)
    {
        var scene = "ClipLayerHeavy";
        var scenes = new List<string>();
        var warmup = 30;
        var measure = 120;
        int? offscreenIterations = null;
        var offscreenRepeats = 5;
        var timeoutSeconds = 30;
        var stressCopies = 1;
        var visualTreeGrid = 1;
        Win32CompositionMode? compositionMode = null;
        var renderOnUiThread = false;
        string? outputJson = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string Next()
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException($"Missing value for {arg}");
                return args[++i];
            }

            switch (arg)
            {
                case "--bench-windowed":
                    break;
                case "--scene":
                {
                    var value = Next();
                    if (string.Equals(value, "all", StringComparison.OrdinalIgnoreCase))
                    {
                        scenes.AddRange(RenderBenchmarkScenes.All.Select(static s => s.Name));
                        scene = scenes[0];
                    }
                    else
                    {
                        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            scenes.Add(part);
                        scene = scenes[0];
                    }
                    break;
                }
                case "--warmup":
                    warmup = int.Parse(Next());
                    break;
                case "--frames":
                    measure = int.Parse(Next());
                    break;
                case "--offscreen-iterations":
                    offscreenIterations = int.Parse(Next());
                    break;
                case "--offscreen-repeats":
                    offscreenRepeats = int.Parse(Next());
                    break;
                case "--timeout":
                    timeoutSeconds = int.Parse(Next());
                    break;
                case "--stress-copies":
                    stressCopies = Math.Max(1, int.Parse(Next()));
                    break;
                case "--visual-tree-grid":
                    visualTreeGrid = Math.Clamp(int.Parse(Next()), 1, 16);
                    break;
                case "--composition":
                {
                    var name = Next();
                    compositionMode = name.ToLowerInvariant() switch
                    {
                        "default" => null,
                        "winui" or "winuicomposition" => Win32CompositionMode.WinUIComposition,
                        "direct" or "directcomposition" => Win32CompositionMode.DirectComposition,
                        "lowlatency" or "lowlatencydxgiswapchain" => Win32CompositionMode.LowLatencyDxgiSwapChain,
                        "redirection" or "redirectionsurface" => Win32CompositionMode.RedirectionSurface,
                        _ => throw new ArgumentException(
                            "Unknown --composition. Supported: default, winui, direct, lowlatency, redirection"),
                    };
                    break;
                }
                case "--render-on-ui-thread":
                    renderOnUiThread = true;
                    break;
                case "--json":
                    outputJson = Next();
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        var resolvedScene = RenderBenchmarkScenes.Get(scene);
        // Validate every listed scene early.
        foreach (var s in scenes)
            _ = RenderBenchmarkScenes.Get(s);

        return new WindowedBenchmarkOptions(
            resolvedScene.Name,
            warmup,
            measure,
            offscreenIterations ?? resolvedScene.Iterations,
            offscreenRepeats,
            timeoutSeconds,
            stressCopies,
            visualTreeGrid,
            compositionMode,
            renderOnUiThread,
            outputJson,
            scenes);
    }
}
