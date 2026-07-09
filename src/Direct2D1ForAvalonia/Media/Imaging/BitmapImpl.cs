using System;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SharpGen.Runtime.Win32;
using Vortice.Direct2D1;
using Vortice.WIC;

namespace MIR.Direct2D1ForAvalonia.Media
{
    internal abstract class BitmapImpl : IBitmapImpl, IDisposable
    {
        public abstract Vector Dpi { get; }
        public abstract PixelSize PixelSize { get; }
        public int Version { get; protected set; } = 1;

        public abstract OptionalDispose<ID2D1Bitmap1> GetDirect2DBitmap(ID2D1RenderTarget target);

        public void Save(Stream stream, BitmapEncoderOptions options)
        {
            var (containerFormat, quality) = MapEncoderOptions(options);
            Save(stream, containerFormat, quality);
        }

        /// <summary>
        /// Maps a <see cref="BitmapEncoderOptions"/> instance to a WIC container format and optional quality value.
        /// </summary>
        private static (ContainerFormat containerFormat, int? quality) MapEncoderOptions(BitmapEncoderOptions options)
        {
            if (options is JpegBitmapEncoderOptions jpeg)
                return (ContainerFormat.Jpeg, jpeg.Quality);

            // PngBitmapEncoderOptions and any future format default to PNG with no quality setting.
            return (ContainerFormat.Png, null);
        }

        /// <summary>
        /// Encodes the bitmap to <paramref name="stream"/> in the requested WIC container format.
        /// The optional <paramref name="quality"/> is honored where the format and binding support it.
        /// </summary>
        protected internal abstract void Save(Stream stream, ContainerFormat containerFormat, int? quality);

        protected static void ConfigureEncoderOptions(
            IPropertyBag2 encoderOptions,
            ContainerFormat containerFormat,
            int? quality)
        {
            if (containerFormat != ContainerFormat.Jpeg || quality is null)
                return;

            var normalizedQuality = Math.Clamp(quality.Value, 0, 100) / 100.0f;
            encoderOptions.Set("ImageQuality", normalizedQuality);
        }

        public virtual void Dispose()
        {
        }
    }
}
