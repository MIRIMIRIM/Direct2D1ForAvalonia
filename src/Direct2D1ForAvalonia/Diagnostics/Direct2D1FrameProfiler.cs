#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace MIR.Direct2D1ForAvalonia.Diagnostics
{
    /// <summary>
    /// Opt-in per-frame phase timing for the real-window D3D11 texture path
    /// (<see cref="D3D11TextureRenderTarget"/>) and drawing-context EndDraw.
    /// <para>
    /// Enable from benchmarks before creating windows. Not enabled by default —
    /// zero cost when disabled (single bool check per mark).
    /// </para>
    /// </summary>
    internal static class Direct2D1FrameProfiler
    {
        private static readonly object s_lock = new();
        private static readonly List<FrameRecord> s_records = new(512);

        // Partial state for the in-flight frame (UI render thread only).
        private static long s_t0;
        private static long s_tSurface;
        private static long s_tSetup;
        private static long s_tEndDrawStart;
        private static long s_tEndDraw;
        private static long s_tFlush;
        private static bool s_inFrame;
        // Captured at EndDraw (before session reopen can zero DrawingContextImpl counters).
        private static int s_softHits;
        private static int s_softMisses;
        private static int s_layerPushes;
        private static int s_deferredFlushes;
        private static int s_pushClips;
        private static int s_pushOpacities;
        private static int s_drawRectangles;

        public static bool IsEnabled { get; private set; }

        public static void Enable()
        {
            lock (s_lock)
            {
                IsEnabled = true;
                s_records.Clear();
                s_inFrame = false;
            }
        }

        public static void Disable()
        {
            lock (s_lock)
            {
                IsEnabled = false;
                s_inFrame = false;
            }
        }

        public static void Reset()
        {
            lock (s_lock)
            {
                s_records.Clear();
                s_inFrame = false;
            }
        }

        public static void MarkSurfaceBegin()
        {
            if (!IsEnabled)
                return;
            s_t0 = Stopwatch.GetTimestamp();
            s_inFrame = true;
        }

        public static void MarkSurfaceReady()
        {
            if (!IsEnabled || !s_inFrame)
                return;
            s_tSurface = Stopwatch.GetTimestamp();
        }

        public static void MarkSetupDone()
        {
            if (!IsEnabled || !s_inFrame)
                return;
            s_tSetup = Stopwatch.GetTimestamp();
        }

        public static void MarkEndDrawStart(
            int softHits,
            int softMisses,
            int layerPushes,
            int deferredFlushes,
            int pushClips = 0,
            int pushOpacities = 0,
            int drawRectangles = 0)
        {
            if (!IsEnabled || !s_inFrame)
                return;
            s_tEndDrawStart = Stopwatch.GetTimestamp();
            s_softHits = softHits;
            s_softMisses = softMisses;
            s_layerPushes = layerPushes;
            s_deferredFlushes = deferredFlushes;
            s_pushClips = pushClips;
            s_pushOpacities = pushOpacities;
            s_drawRectangles = drawRectangles;
        }

        public static void MarkEndDrawDone()
        {
            if (!IsEnabled || !s_inFrame)
                return;
            s_tEndDraw = Stopwatch.GetTimestamp();
        }

        public static void MarkFlushDone()
        {
            if (!IsEnabled || !s_inFrame)
                return;
            s_tFlush = Stopwatch.GetTimestamp();
        }

        /// <summary>
        /// Completes the frame after composition session dispose (Present hand-off).
        /// Soft/layer counters were snapshotted at EndDraw start.
        /// </summary>
        public static void MarkCleanupDone()
        {
            if (!IsEnabled || !s_inFrame)
                return;

            var tCleanup = Stopwatch.GetTimestamp();
            var freq = (double)Stopwatch.Frequency;

            static double Ms(long a, long b, double frequency)
                => a > 0 && b >= a ? (b - a) * 1000.0 / frequency : 0;

            var record = new FrameRecord(
                SurfaceBeginMs: Ms(s_t0, s_tSurface, freq),
                SetupMs: Ms(s_tSurface, s_tSetup, freq),
                DrawGapMs: Ms(s_tSetup, s_tEndDrawStart, freq),
                EndDrawMs: Ms(s_tEndDrawStart, s_tEndDraw, freq),
                FlushMs: Ms(s_tEndDraw, s_tFlush, freq),
                CleanupMs: Ms(s_tFlush > 0 ? s_tFlush : s_tEndDraw, tCleanup, freq),
                TotalMs: Ms(s_t0, tCleanup, freq),
                SoftHits: s_softHits,
                SoftMisses: s_softMisses,
                LayerPushes: s_layerPushes,
                DeferredFlushes: s_deferredFlushes,
                PushClips: s_pushClips,
                PushOpacities: s_pushOpacities,
                DrawRectangles: s_drawRectangles);

            lock (s_lock)
            {
                s_records.Add(record);
            }

            s_inFrame = false;
            s_t0 = s_tSurface = s_tSetup = s_tEndDrawStart = s_tEndDraw = s_tFlush = 0;
            s_softHits = s_softMisses = s_layerPushes = s_deferredFlushes = 0;
            s_pushClips = s_pushOpacities = s_drawRectangles = 0;
        }

        public static int SampleCount
        {
            get
            {
                lock (s_lock)
                    return s_records.Count;
            }
        }

        /// <summary>
        /// Builds a summary after discarding the first <paramref name="skipFirst"/> samples (warmup).
        /// </summary>
        public static FrameProfilerSummary Summarize(int skipFirst = 0)
        {
            lock (s_lock)
            {
                var slice = skipFirst <= 0
                    ? s_records
                    : s_records.Skip(Math.Min(skipFirst, s_records.Count)).ToList();

                if (slice.Count == 0)
                {
                    return new FrameProfilerSummary(
                        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
                }

                return new FrameProfilerSummary(
                    SampleCount: slice.Count,
                    MedianTotalMs: Median(slice.Select(static r => r.TotalMs)),
                    MedianSurfaceBeginMs: Median(slice.Select(static r => r.SurfaceBeginMs)),
                    MedianSetupMs: Median(slice.Select(static r => r.SetupMs)),
                    MedianDrawGapMs: Median(slice.Select(static r => r.DrawGapMs)),
                    MedianEndDrawMs: Median(slice.Select(static r => r.EndDrawMs)),
                    MedianFlushMs: Median(slice.Select(static r => r.FlushMs)),
                    MedianCleanupMs: Median(slice.Select(static r => r.CleanupMs)),
                    MeanSoftHits: slice.Average(static r => r.SoftHits),
                    MeanSoftMisses: slice.Average(static r => r.SoftMisses),
                    MeanLayerPushes: slice.Average(static r => r.LayerPushes),
                    MeanDeferredFlushes: slice.Average(static r => r.DeferredFlushes),
                    MeanPushClips: slice.Average(static r => r.PushClips),
                    MeanPushOpacities: slice.Average(static r => r.PushOpacities),
                    MeanDrawRectangles: slice.Average(static r => r.DrawRectangles),
                    P95TotalMs: Percentile(slice.Select(static r => r.TotalMs), 0.95));
            }
        }

        public static string FormatSummary(FrameProfilerSummary s)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"  samples={s.SampleCount}  medianTotal={s.MedianTotalMs:0.###}ms  p95Total={s.P95TotalMs:0.###}ms");
            sb.AppendLine(
                $"  phases(ms median): surfaceBegin={s.MedianSurfaceBeginMs:0.###}  " +
                $"setup={s.MedianSetupMs:0.###}  drawGap={s.MedianDrawGapMs:0.###}  " +
                $"endDraw={s.MedianEndDrawMs:0.###}  flush={s.MedianFlushMs:0.###}  " +
                $"cleanup(present)={s.MedianCleanupMs:0.###}");
            sb.AppendLine(
                $"  softHits={s.MeanSoftHits:0.##}  softMisses={s.MeanSoftMisses:0.##}  " +
                $"layers={s.MeanLayerPushes:0.##}  deferredFlushes={s.MeanDeferredFlushes:0.##}");
            sb.Append(
                $"  dcCalls: pushClip={s.MeanPushClips:0.##}  pushOpacity={s.MeanPushOpacities:0.##}  " +
                $"drawRect={s.MeanDrawRectangles:0.##}");
            return sb.ToString();
        }

        private static double Median(IEnumerable<double> values)
        {
            var arr = values.OrderBy(static x => x).ToArray();
            if (arr.Length == 0)
                return 0;
            return arr[arr.Length / 2];
        }

        private static double Percentile(IEnumerable<double> values, double p)
        {
            var arr = values.OrderBy(static x => x).ToArray();
            if (arr.Length == 0)
                return 0;
            var idx = (int)Math.Clamp(Math.Ceiling(p * arr.Length) - 1, 0, arr.Length - 1);
            return arr[idx];
        }

        private readonly record struct FrameRecord(
            double SurfaceBeginMs,
            double SetupMs,
            double DrawGapMs,
            double EndDrawMs,
            double FlushMs,
            double CleanupMs,
            double TotalMs,
            int SoftHits,
            int SoftMisses,
            int LayerPushes,
            int DeferredFlushes,
            int PushClips,
            int PushOpacities,
            int DrawRectangles);
    }

    internal readonly record struct FrameProfilerSummary(
        int SampleCount,
        double MedianTotalMs,
        double MedianSurfaceBeginMs,
        double MedianSetupMs,
        double MedianDrawGapMs,
        double MedianEndDrawMs,
        double MedianFlushMs,
        double MedianCleanupMs,
        double MeanSoftHits,
        double MeanSoftMisses,
        double MeanLayerPushes,
        double MeanDeferredFlushes,
        double MeanPushClips,
        double MeanPushOpacities,
        double MeanDrawRectangles,
        double P95TotalMs);
}
