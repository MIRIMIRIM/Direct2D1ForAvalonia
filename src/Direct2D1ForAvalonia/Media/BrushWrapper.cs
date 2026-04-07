using System.Runtime.InteropServices;
using Avalonia.Media;
using SharpGen.Runtime;

namespace MIR.Direct2D1ForAvalonia.Media
{
    internal class BrushWrapper : ComObject
    {
        public BrushWrapper(IBrush brush)
        {
            NativePointer = Marshal.GetIUnknownForObject(this);
            Brush = brush;
        }

        public IBrush Brush { get; private set; }
    }
}