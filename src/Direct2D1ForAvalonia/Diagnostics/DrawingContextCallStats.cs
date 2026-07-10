#nullable enable

using System.Collections.Concurrent;
using System.Text;
using System.Threading;

namespace MIR.Direct2D1ForAvalonia.Diagnostics
{
    /// <summary>
    /// Process-wide DrawingContextImpl call counters. Unlike
    /// <see cref="Direct2D1FrameProfiler"/> (window surface session only), these accumulate
    /// across every D2D context — including composition intermediate targets and WIC layers —
    /// so we can see where Avalonia actually issues push/draw calls.
    /// </summary>
    internal static class DrawingContextCallStats
    {
        private static int s_enabled;
        private static long s_drawRectangles;
        private static long s_pushClips;
        private static long s_pushOpacities;
        private static long s_softHits;
        private static long s_softMisses;
        private static long s_layerPushes;
        private static long s_sessions;
        private static readonly ConcurrentDictionary<string, long> s_drawByTarget = new();
        private static readonly ConcurrentDictionary<string, long> s_softByTarget = new();
        private static readonly ConcurrentDictionary<string, long> s_sessionsByTarget = new();

        public static bool IsEnabled => Volatile.Read(ref s_enabled) != 0;

        public static void Enable()
        {
            Volatile.Write(ref s_enabled, 1);
            Reset();
        }

        public static void Disable() => Volatile.Write(ref s_enabled, 0);

        public static void Reset()
        {
            Interlocked.Exchange(ref s_drawRectangles, 0);
            Interlocked.Exchange(ref s_pushClips, 0);
            Interlocked.Exchange(ref s_pushOpacities, 0);
            Interlocked.Exchange(ref s_softHits, 0);
            Interlocked.Exchange(ref s_softMisses, 0);
            Interlocked.Exchange(ref s_layerPushes, 0);
            Interlocked.Exchange(ref s_sessions, 0);
            s_drawByTarget.Clear();
            s_softByTarget.Clear();
            s_sessionsByTarget.Clear();
        }

        public static void OnSessionOpen(string targetName)
        {
            if (!IsEnabled)
                return;
            Interlocked.Increment(ref s_sessions);
            s_sessionsByTarget.AddOrUpdate(targetName, 1, static (_, v) => v + 1);
        }

        public static void OnDrawRectangle(string targetName)
        {
            if (!IsEnabled)
                return;
            Interlocked.Increment(ref s_drawRectangles);
            s_drawByTarget.AddOrUpdate(targetName, 1, static (_, v) => v + 1);
        }

        public static void OnPushClip()
        {
            if (!IsEnabled)
                return;
            Interlocked.Increment(ref s_pushClips);
        }

        public static void OnPushOpacity()
        {
            if (!IsEnabled)
                return;
            Interlocked.Increment(ref s_pushOpacities);
        }

        public static void OnSoftHit(string targetName)
        {
            if (!IsEnabled)
                return;
            Interlocked.Increment(ref s_softHits);
            s_softByTarget.AddOrUpdate(targetName, 1, static (_, v) => v + 1);
        }

        public static void OnSoftMiss()
        {
            if (!IsEnabled)
                return;
            Interlocked.Increment(ref s_softMisses);
        }

        public static void OnLayerPush()
        {
            if (!IsEnabled)
                return;
            Interlocked.Increment(ref s_layerPushes);
        }

        public static DrawingContextCallStatsSnapshot Snapshot()
            => new(
                Interlocked.Read(ref s_sessions),
                Interlocked.Read(ref s_drawRectangles),
                Interlocked.Read(ref s_pushClips),
                Interlocked.Read(ref s_pushOpacities),
                Interlocked.Read(ref s_softHits),
                Interlocked.Read(ref s_softMisses),
                Interlocked.Read(ref s_layerPushes),
                s_sessionsByTarget.ToArray(),
                s_drawByTarget.ToArray(),
                s_softByTarget.ToArray());

        public static string Format(DrawingContextCallStatsSnapshot s)
        {
            var sb = new StringBuilder();
            sb.AppendLine(
                $"  global: sessions={s.Sessions}  drawRect={s.DrawRectangles}  " +
                $"pushClip={s.PushClips}  pushOpacity={s.PushOpacities}  " +
                $"softHits={s.SoftHits}  softMisses={s.SoftMisses}  layers={s.LayerPushes}");
            if (s.SessionsByTarget.Length > 0)
            {
                sb.Append("  sessionsByTarget:");
                foreach (var kv in s.SessionsByTarget)
                    sb.Append($"  {kv.Key}={kv.Value}");
                sb.AppendLine();
            }
            if (s.DrawByTarget.Length > 0)
            {
                sb.Append("  drawRectByTarget:");
                foreach (var kv in s.DrawByTarget)
                    sb.Append($"  {kv.Key}={kv.Value}");
                sb.AppendLine();
            }
            if (s.SoftByTarget.Length > 0)
            {
                sb.Append("  softHitsByTarget:");
                foreach (var kv in s.SoftByTarget)
                    sb.Append($"  {kv.Key}={kv.Value}");
            }
            return sb.ToString().TrimEnd();
        }
    }

    internal readonly record struct DrawingContextCallStatsSnapshot(
        long Sessions,
        long DrawRectangles,
        long PushClips,
        long PushOpacities,
        long SoftHits,
        long SoftMisses,
        long LayerPushes,
        System.Collections.Generic.KeyValuePair<string, long>[] SessionsByTarget,
        System.Collections.Generic.KeyValuePair<string, long>[] DrawByTarget,
        System.Collections.Generic.KeyValuePair<string, long>[] SoftByTarget);
}
