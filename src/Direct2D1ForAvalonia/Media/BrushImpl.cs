using System;
using Vortice.Direct2D1;

namespace MIR.Direct2D1ForAvalonia.Media
{
    internal abstract class BrushImpl : IDisposable
    {
        public ID2D1Brush? PlatformBrush { get; set; }

        /// <summary>
        /// When true, the brush is owned by a cache and its <see cref="PlatformBrush"/>
        /// must not be disposed by the consumer. Dispose becomes a no-op.
        /// </summary>
        internal bool IsCached { get; set; }

        public virtual void Dispose()
        {
            if (IsCached)
                return;

            PlatformBrush?.Dispose();
        }

        /// <summary>
        /// Disposes the underlying native brush regardless of <see cref="IsCached"/>.
        /// Used by the resource cache's LRU eviction to release the COM object when a cached
        /// brush is evicted — <see cref="Dispose"/> is a no-op for cached brushes.
        /// </summary>
        internal void ForceReleaseNative()
        {
            PlatformBrush?.Dispose();
            PlatformBrush = null;
        }
    }
}