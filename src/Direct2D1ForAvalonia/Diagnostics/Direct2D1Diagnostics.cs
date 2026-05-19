using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace MIR.Direct2D1ForAvalonia.Diagnostics
{
    internal static class Direct2D1Diagnostics
    {
        private static readonly object s_lock = new();
        private static readonly StreamWriter? s_writer;
        private static int s_messageCount;

        static Direct2D1Diagnostics()
        {
            var raw = Environment.GetEnvironmentVariable("MIR_DIRECT2D_TRACE");
            if (string.IsNullOrWhiteSpace(raw)
                || string.Equals(raw, "0", StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            IsEnabled = true;
            Path = ResolvePath(raw);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            s_writer = new StreamWriter(
                new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                Encoding.UTF8)
            {
                AutoFlush = true
            };

            Write("trace-start " +
                  $"pid={Environment.ProcessId} " +
                  $"process={Process.GetCurrentProcess().ProcessName} " +
                  $"time={DateTimeOffset.Now:O}");
        }

        public static bool IsEnabled { get; }

        public static string? Path { get; }

        public static bool ShouldLogFrame(int frameId, bool important = false)
        {
            return IsEnabled && (important || frameId <= 120 || frameId % 60 == 0);
        }

        public static void Write(string message)
        {
            var writer = s_writer;
            if (!IsEnabled || writer is null)
                return;

            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"{DateTimeOffset.Now:O} [{Interlocked.Increment(ref s_messageCount):000000}] {message}{Environment.NewLine}");

            lock (s_lock)
            {
                writer.Write(line);
            }
        }

        private static string ResolvePath(string raw)
        {
            if (!string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(raw, "default", StringComparison.OrdinalIgnoreCase))
            {
                return System.IO.Path.GetFullPath(raw);
            }

            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MIR.Direct2D1");
            var fileName = $"trace-{Environment.ProcessId}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log";
            return System.IO.Path.Combine(directory, fileName);
        }
    }
}
