using System;
using System.IO;
using Avalonia.Platform;
using PixelFormat = Avalonia.Platform.PixelFormat;

namespace MIR.Direct2D1ForAvalonia.Media.Imaging
{
    internal class WriteableWicBitmapImpl : WicBitmapImpl, IWriteableBitmapImpl
    {
        public WriteableWicBitmapImpl(Stream stream, int decodeSize, bool horizontal,
            Avalonia.Media.Imaging.BitmapInterpolationMode interpolationMode)
        : base(stream, decodeSize, horizontal, interpolationMode)
        {
        }
        
        public WriteableWicBitmapImpl(PixelSize size, Vector dpi, PixelFormat? pixelFormat, AlphaFormat? alphaFormat) 
            : base(size, dpi, pixelFormat, alphaFormat)
        {
        }

        public WriteableWicBitmapImpl(Stream stream)
            : base(stream)
        {
        }

        public WriteableWicBitmapImpl(string fileName)
            : base(fileName)
        {
        }

        public PixelFormat? Format => PixelFormat;
    }
}