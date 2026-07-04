using System;
using System.IO;
using Avalonia.Platform;
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

        public void Save(string fileName, int? quality = null)
        {
            var containerFormat = GetContainerFormat(fileName);

            using (FileStream s = new FileStream(fileName, FileMode.Create))
            {
                Save(s, containerFormat, quality);
            }
        }

        public void Save(Stream stream, int? quality = null) => Save(stream, ContainerFormat.Png, quality);

        /// <summary>
        /// Encodes the bitmap to <paramref name="stream"/> in the requested WIC container format.
        /// The optional <paramref name="quality"/> is honored where the format and binding support it.
        /// </summary>
        protected internal abstract void Save(Stream stream, ContainerFormat containerFormat, int? quality);

        /// <summary>
        /// Maps a file extension to a WIC container format, defaulting to PNG.
        /// </summary>
        private static ContainerFormat GetContainerFormat(string fileName)
        {
            var ext = Path.GetExtension(fileName.AsSpan());
            if (ext.IsEmpty)
                return ContainerFormat.Png;

            // Compare case-insensitively without the leading dot.
            if (ext.Length > 1)
                ext = ext.Slice(1);

            if (ext.Equals("jpg", StringComparison.OrdinalIgnoreCase) || ext.Equals("jpeg", StringComparison.OrdinalIgnoreCase))
                return ContainerFormat.Jpeg;
            if (ext.Equals("bmp", StringComparison.OrdinalIgnoreCase))
                return ContainerFormat.Bmp;
            if (ext.Equals("tif", StringComparison.OrdinalIgnoreCase) || ext.Equals("tiff", StringComparison.OrdinalIgnoreCase))
                return ContainerFormat.Tiff;
            if (ext.Equals("gif", StringComparison.OrdinalIgnoreCase))
                return ContainerFormat.Gif;
            if (ext.Equals("webp", StringComparison.OrdinalIgnoreCase))
                return ContainerFormat.Webp;

            return ContainerFormat.Png;
        }

        public virtual void Dispose()
        {
        }
    }
}