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
    }
}