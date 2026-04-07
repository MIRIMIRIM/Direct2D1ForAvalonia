using System;
using Vortice.Direct2D1;

namespace MIR.Direct2D1ForAvalonia.Media
{
    internal abstract class BrushImpl : IDisposable
    {
        public ID2D1Brush? PlatformBrush { get; set; }

        public virtual void Dispose()
        {
            PlatformBrush?.Dispose();
        }
    }
}