#nullable enable

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Platform;
using Vortice.Direct2D1;

namespace MIR.Direct2D1ForAvalonia.Media.Imaging
{
    /// <summary>
    /// Process-wide pool of GPU composition intermediates (<see cref="D2DRenderTargetBitmapImpl"/>).
    /// Avalonia's compositor typically CreateLayer + Dispose every dirty paint; without pooling that
    /// means CreateCompatibleRenderTarget + DC setup every frame and ~2–4× session cost vs
    /// offscreen draw-only benches.
    /// </summary>
    internal static class D2DCompatibleLayerPool
    {
        private const int MaxPerKey = 6;
        private const int MaxTotal = 48;

        private static readonly object s_gate = new();
        private static readonly Dictionary<LayerKey, Stack<D2DRenderTargetBitmapImpl>> s_pools = new();
        private static int s_total;
        private static long s_hits;
        private static long s_misses;
        private static long s_returns;
        private static long s_discards;

        public static long Hits { get { lock (s_gate) return s_hits; } }
        public static long Misses { get { lock (s_gate) return s_misses; } }
        public static long Returns { get { lock (s_gate) return s_returns; } }
        public static long Discards { get { lock (s_gate) return s_discards; } }
        public static int PooledCount { get { lock (s_gate) return s_total; } }

        public static void ResetStats()
        {
            lock (s_gate)
            {
                s_hits = 0;
                s_misses = 0;
                s_returns = 0;
                s_discards = 0;
            }
        }

        /// <summary>
        /// Rents a layer of the requested DIP size, creating one via
        /// <see cref="D2DRenderTargetBitmapImpl.CreateCompatible"/> on miss.
        /// </summary>
        public static D2DRenderTargetBitmapImpl Rent(ID2D1RenderTarget parent, Size dipSize)
        {
            var key = LayerKey.From(dipSize, parent.Dpi);
            lock (s_gate)
            {
                if (s_pools.TryGetValue(key, out var stack) && stack.Count > 0)
                {
                    var rented = stack.Pop();
                    s_total--;
                    s_hits++;
                    rented.PrepareForPoolReuse();
                    rented.AttachToPool(key);
                    return rented;
                }

                s_misses++;
            }

            var created = D2DRenderTargetBitmapImpl.CreateCompatible(parent, dipSize);
            created.AttachToPool(key);
            return created;
        }

        /// <summary>
        /// Returns a layer to the pool, or disposes it when the pool is full / size mismatch.
        /// </summary>
        public static void Return(D2DRenderTargetBitmapImpl layer, LayerKey key)
        {
            lock (s_gate)
            {
                if (s_total >= MaxTotal)
                {
                    s_discards++;
                    layer.ForceDisposeNative();
                    return;
                }

                if (!s_pools.TryGetValue(key, out var stack))
                {
                    stack = new Stack<D2DRenderTargetBitmapImpl>(MaxPerKey);
                    s_pools[key] = stack;
                }

                if (stack.Count >= MaxPerKey)
                {
                    s_discards++;
                    layer.ForceDisposeNative();
                    return;
                }

                stack.Push(layer);
                s_total++;
                s_returns++;
            }
        }

        /// <summary>
        /// Drops every pooled intermediate (device loss / process teardown).
        /// </summary>
        public static void Clear()
        {
            lock (s_gate)
            {
                foreach (var stack in s_pools.Values)
                {
                    while (stack.Count > 0)
                        stack.Pop().ForceDisposeNative();
                }

                s_pools.Clear();
                s_total = 0;
            }
        }

        internal readonly struct LayerKey : IEquatable<LayerKey>
        {
            private readonly int _w;
            private readonly int _h;
            private readonly int _dpiX;
            private readonly int _dpiY;

            private LayerKey(int w, int h, int dpiX, int dpiY)
            {
                _w = w;
                _h = h;
                _dpiX = dpiX;
                _dpiY = dpiY;
            }

            public static LayerKey From(Size dipSize, Vortice.Mathematics.Size dpi)
            {
                // Quantise to 1/100 DIP and integer DPI so float noise does not fragment the pool.
                var w = Math.Max(1, (int)Math.Round(dipSize.Width * 100.0));
                var h = Math.Max(1, (int)Math.Round(dipSize.Height * 100.0));
                var dx = Math.Max(1, (int)Math.Round(dpi.Width));
                var dy = Math.Max(1, (int)Math.Round(dpi.Height));
                return new LayerKey(w, h, dx, dy);
            }

            public bool Equals(LayerKey other)
                => _w == other._w && _h == other._h && _dpiX == other._dpiX && _dpiY == other._dpiY;

            public override bool Equals(object? obj) => obj is LayerKey other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(_w, _h, _dpiX, _dpiY);
        }
    }
}
