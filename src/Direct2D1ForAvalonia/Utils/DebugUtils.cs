using System.IO;
using Avalonia.Media.Imaging;
using MIR.Direct2D1ForAvalonia.Media.Imaging;
using Vortice.Direct2D1;

namespace MIR.Direct2D1ForAvalonia.Utils
{
    internal static class DebugUtils
    {
        public static void Save(ID2D1BitmapRenderTarget bitmap, string filename)
        {
            var rtb = new D2DRenderTargetBitmapImpl(bitmap);
            using var stream = new FileStream(filename, FileMode.Create);
            rtb.Save(stream, PngBitmapEncoderOptions.Default);
        }
    }
}