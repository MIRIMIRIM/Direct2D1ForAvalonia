using System;
using System.IO;
using Vortice.WIC;
using APixelFormat = Avalonia.Platform.PixelFormat;
using AlphaFormat = Avalonia.Platform.AlphaFormat;
using Avalonia.Platform;
using Vortice.Direct2D1;
using BitmapInterpolationMode = Vortice.WIC.BitmapInterpolationMode;

namespace MIR.Direct2D1ForAvalonia.Media
{
    /// <summary>
    /// A WIC implementation of a <see cref="Avalonia.Media.Imaging.Bitmap"/>.
    /// </summary>
    internal class WicBitmapImpl : BitmapImpl, IReadableBitmapImpl
    {
        private readonly IWICBitmapDecoder? _decoder;
        // Device-scoped GPU upload of this WIC source. Invalidated on Version++ (pixel write)
        // and when the D2D device changes. Avoids CreateFormatConverter + CreateBitmapFromWicBitmap
        // on every DrawBitmap for static images.
        private readonly object _gpuUploadLock = new();
        private ID2D1Bitmap1? _gpuUpload;
        private IntPtr _gpuUploadDevice;
        private int _gpuUploadVersion;

        private static BitmapInterpolationMode ConvertInterpolationMode(Avalonia.Media.Imaging.BitmapInterpolationMode interpolationMode)
        {
            return interpolationMode switch
            {
                Avalonia.Media.Imaging.BitmapInterpolationMode.Unspecified => BitmapInterpolationMode.Fant,
                Avalonia.Media.Imaging.BitmapInterpolationMode.None => BitmapInterpolationMode.NearestNeighbor,
                Avalonia.Media.Imaging.BitmapInterpolationMode.LowQuality => BitmapInterpolationMode.NearestNeighbor,
                Avalonia.Media.Imaging.BitmapInterpolationMode.MediumQuality => BitmapInterpolationMode.Fant,
                _ => BitmapInterpolationMode.HighQualityCubic,
            };
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="WicBitmapImpl"/> class.
        /// </summary>
        /// <param name="fileName">The filename of the bitmap to load.</param>
        public WicBitmapImpl(string fileName)
        {
            using (var decoder = Direct2D1Platform.ImagingFactory.CreateDecoderFromFileName(fileName, metadataOptions: DecodeOptions.CacheOnDemand))
            using (var frame = decoder.GetFrame(0))
            {
                WicImpl = Direct2D1Platform.ImagingFactory.CreateBitmapFromSource(frame, BitmapCreateCacheOption.CacheOnDemand);
                Dpi = new Vector(96, 96);
                SetFormatFromWic(WicImpl.PixelFormat);
            }
        }

        private WicBitmapImpl(IWICBitmap bmp)
        {
            WicImpl = bmp;
            Dpi = new Vector(96, 96);
            SetFormatFromWic(WicImpl.PixelFormat);
        }

        /// <summary>
        /// Initializes a resized copy of an existing WIC-backed bitmap using a WIC scaler.
        /// </summary>
        /// <param name="source">The source bitmap to scale.</param>
        /// <param name="destinationSize">The desired pixel dimensions.</param>
        /// <param name="interpolationMode">The interpolation mode to apply.</param>
        internal WicBitmapImpl(WicBitmapImpl source, PixelSize destinationSize, Avalonia.Media.Imaging.BitmapInterpolationMode interpolationMode)
        {
            Dpi = source.Dpi;

            if (source.PixelSize == destinationSize)
            {
                WicImpl = Direct2D1Platform.ImagingFactory.CreateBitmapFromSource(source.WicImpl, BitmapCreateCacheOption.CacheOnLoad);
            }
            else
            {
                using (var scaler = Direct2D1Platform.ImagingFactory.CreateBitmapScaler())
                {
                    scaler.Initialize(source.WicImpl, (uint)destinationSize.Width, (uint)destinationSize.Height, ConvertInterpolationMode(interpolationMode));
                    WicImpl = Direct2D1Platform.ImagingFactory.CreateBitmapFromSource(scaler, BitmapCreateCacheOption.CacheOnLoad);
                }
            }

            SetFormatFromWic(WicImpl.PixelFormat);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="WicBitmapImpl"/> class.
        /// </summary>
        /// <param name="stream">The stream to read the bitmap from.</param>
        public WicBitmapImpl(Stream stream)
        {
            // https://stackoverflow.com/questions/48982749/decoding-image-from-stream-using-wic/48982889#48982889
            _decoder = Direct2D1Platform.ImagingFactory.CreateDecoderFromStream(stream, DecodeOptions.CacheOnLoad);

            using var frame = _decoder.GetFrame(0);
            WicImpl = Direct2D1Platform.ImagingFactory.CreateBitmapFromSource(frame, BitmapCreateCacheOption.CacheOnLoad);
            Dpi = new Vector(96, 96);
            SetFormatFromWic(WicImpl.PixelFormat);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="WicBitmapImpl"/> class.
        /// </summary>
        /// <param name="size">The size of the bitmap in device pixels.</param>
        /// <param name="dpi">The DPI of the bitmap.</param>
        /// <param name="pixelFormat">Pixel format</param>
        /// <param name="alphaFormat">Alpha format.</param>
        public WicBitmapImpl(PixelSize size, Vector dpi, APixelFormat? pixelFormat = null, AlphaFormat? alphaFormat = null)
        {
            if (!pixelFormat.HasValue)
            {
                pixelFormat = APixelFormat.Bgra8888;
            }

            if (!alphaFormat.HasValue)
            {
                alphaFormat = Avalonia.Platform.AlphaFormat.Premul;
            }

            PixelFormat = pixelFormat;
            AlphaFormat = alphaFormat;
            WicImpl = Direct2D1Platform.ImagingFactory.CreateBitmap(
                (uint)size.Width,
                (uint)size.Height,
                pixelFormat.Value.ToWic(alphaFormat.Value),
                BitmapCreateCacheOption.CacheOnLoad);

            Dpi = dpi;
        }

        public WicBitmapImpl(APixelFormat format, AlphaFormat alphaFormat, IntPtr data, PixelSize size, Vector dpi, int stride)
        {
            WicImpl = Direct2D1Platform.ImagingFactory.CreateBitmap((uint)size.Width, (uint)size.Height, format.ToWic(alphaFormat), BitmapCreateCacheOption.CacheOnDemand);
            WicImpl.SetResolution(dpi.X, dpi.Y);
            PixelFormat = format;
            AlphaFormat = alphaFormat;
            Dpi = dpi;

            using (var l = WicImpl.Lock(BitmapLockFlags.Write))
            {
                var sourceStride = stride;
                var destinationStride = (int)l.Stride;
                var rowBytes = Math.Min(sourceStride, destinationStride);

                unsafe
                {
                    var sourceBase = (byte*)data;
                    var destinationBase = (byte*)l.Data.DataPointer;

                    if (rowBytes == sourceStride && rowBytes == destinationStride)
                    {
                        var totalBytes = checked((long)rowBytes * size.Height);
                        Buffer.MemoryCopy(sourceBase, destinationBase, totalBytes, totalBytes);
                    }
                    else
                    {
                        for (var row = 0; row < size.Height; row++)
                        {
                            var sourceRow = sourceBase + (row * sourceStride);
                            var destinationRow = destinationBase + (row * destinationStride);
                            Buffer.MemoryCopy(sourceRow, destinationRow, rowBytes, rowBytes);
                        }
                    }
                }
            }
        }

        public WicBitmapImpl(Stream stream, int decodeSize, bool horizontal, Avalonia.Media.Imaging.BitmapInterpolationMode interpolationMode)
        {
            _decoder = Direct2D1Platform.ImagingFactory.CreateDecoderFromStream(stream, DecodeOptions.CacheOnLoad);

            using var frame = _decoder.GetFrame(0);

            // now scale that to the size that we want
            var realScale = horizontal ? ((double)frame.Size.Height / frame.Size.Width) : ((double)frame.Size.Width / frame.Size.Height);

            PixelSize desired;

            if (horizontal)
            {
                desired = new PixelSize(decodeSize, (int)(realScale * decodeSize));
            }
            else
            {
                desired = new PixelSize((int)(realScale * decodeSize), decodeSize);
            }

            if (frame.Size.Width != desired.Width || frame.Size.Height != desired.Height)
            {
                using (var scaler = Direct2D1Platform.ImagingFactory.CreateBitmapScaler())
                {
                    scaler.Initialize(frame, (uint)desired.Width, (uint)desired.Height, ConvertInterpolationMode(interpolationMode));

                    WicImpl = Direct2D1Platform.ImagingFactory.CreateBitmapFromSource(scaler, BitmapCreateCacheOption.CacheOnLoad);
                }
            }
            else
            {
                WicImpl = Direct2D1Platform.ImagingFactory.CreateBitmapFromSource(frame, BitmapCreateCacheOption.CacheOnLoad);
            }

            Dpi = new Vector(96, 96);
        }

        private void SetFormatFromWic(Guid pixelFormat)
        {
            if (pixelFormat == Vortice.WIC.PixelFormat.Format16bppBGR565)
            {
                PixelFormat = APixelFormat.Rgb565;
                AlphaFormat = Avalonia.Platform.AlphaFormat.Premul;
            }
            else if (pixelFormat == Vortice.WIC.PixelFormat.Format32bppRGB)
            {
                PixelFormat = APixelFormat.Rgb32;
                AlphaFormat = Avalonia.Platform.AlphaFormat.Premul;
            }
            else if (pixelFormat == PixelFormats.Rgba8888.ToWic(Avalonia.Platform.AlphaFormat.Premul))
            {
                PixelFormat = APixelFormat.Rgba8888;
                AlphaFormat = Avalonia.Platform.AlphaFormat.Premul;
            }
            else if (pixelFormat == PixelFormats.Rgba8888.ToWic(Avalonia.Platform.AlphaFormat.Opaque))
            {
                PixelFormat = APixelFormat.Rgba8888;
                AlphaFormat = Avalonia.Platform.AlphaFormat.Opaque;
            }
            else if (pixelFormat == PixelFormats.Bgra8888.ToWic(Avalonia.Platform.AlphaFormat.Premul))
            {
                PixelFormat = APixelFormat.Bgra8888;
                AlphaFormat = Avalonia.Platform.AlphaFormat.Premul;
            }
            else if (pixelFormat == PixelFormats.Bgra8888.ToWic(Avalonia.Platform.AlphaFormat.Opaque))
            {
                PixelFormat = APixelFormat.Bgra8888;
                AlphaFormat = Avalonia.Platform.AlphaFormat.Opaque;
            }
        }

        public override Vector Dpi { get; }

        public override PixelSize PixelSize => WicImpl.Size.ToAvalonia();

        public APixelFormat? PixelFormat { get; private set; }

        public AlphaFormat? AlphaFormat { get; private set; }

        public override void Dispose()
        {
            lock (_gpuUploadLock)
            {
                _gpuUpload?.Dispose();
                _gpuUpload = null;
                _gpuUploadDevice = IntPtr.Zero;
            }

            WicImpl.Dispose();
            _decoder?.Dispose();
        }

        /// <summary>
        /// Gets the WIC implementation of the bitmap.
        /// </summary>
        public IWICBitmap WicImpl { get; }

        /// <summary>
        /// Gets a Direct2D bitmap to use on the specified render target.
        /// </summary>
        /// <param name="renderTarget">The render target.</param>
        /// <returns>The Direct2D bitmap.</returns>
        public override OptionalDispose<ID2D1Bitmap1> GetDirect2DBitmap(ID2D1RenderTarget renderTarget)
        {
            var device = TryGetNativeDevicePointer(renderTarget);

            // GPU / device-context path: cache one upload per WIC bitmap × device × Version.
            // Factory WIC RTs (device == 0) still upload each call — rare for image blits.
            if (device != IntPtr.Zero)
            {
                lock (_gpuUploadLock)
                {
                    if (_gpuUpload is not null
                        && _gpuUploadDevice == device
                        && _gpuUploadVersion == Version)
                    {
                        return new OptionalDispose<ID2D1Bitmap1>(_gpuUpload, dispose: false);
                    }

                    _gpuUpload?.Dispose();
                    _gpuUpload = null;

                    var uploaded = CreateGpuBitmap(renderTarget);
                    _gpuUpload = uploaded;
                    _gpuUploadDevice = device;
                    _gpuUploadVersion = Version;
                    return new OptionalDispose<ID2D1Bitmap1>(uploaded, dispose: false);
                }
            }

            return new OptionalDispose<ID2D1Bitmap1>(CreateGpuBitmap(renderTarget), dispose: true);
        }

        private ID2D1Bitmap1 CreateGpuBitmap(ID2D1RenderTarget renderTarget)
        {
            using var converter = Direct2D1Platform.ImagingFactory.CreateFormatConverter();
            converter.Initialize(WicImpl, Vortice.WIC.PixelFormat.Format32bppPBGRA);

            // CreateBitmapFromWicBitmap returns an ID2D1Bitmap RCW; QI to Bitmap1 and dispose the
            // intermediate so each upload does not leave a COM ref for the finalizer.
            using var bitmap = renderTarget.CreateBitmapFromWicBitmap(converter);
            return bitmap.QueryInterface<ID2D1Bitmap1>();
        }

        private static IntPtr TryGetNativeDevicePointer(ID2D1RenderTarget renderTarget)
        {
            if (renderTarget is ID2D1DeviceContext dc)
            {
                try
                {
                    using var device = dc.Device;
                    return device?.NativePointer ?? IntPtr.Zero;
                }
                catch
                {
                    return IntPtr.Zero;
                }
            }

            try
            {
                using var asDc = renderTarget.QueryInterface<ID2D1DeviceContext>();
                using var device = asDc.Device;
                return device?.NativePointer ?? IntPtr.Zero;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        protected internal override void Save(Stream stream, ContainerFormat containerFormat, int? quality)
        {
            using (var encoder = Direct2D1Platform.ImagingFactory.CreateEncoder(containerFormat, stream))
            using (var frame = encoder.CreateNewFrame(out var props))
            {
                try
                {
                    ConfigureEncoderOptions(props, containerFormat, quality);
                    frame.Initialize(props);
                    frame.WriteSource(WicImpl);
                    frame.Commit();
                    encoder.Commit();
                }
                finally
                {
                    props.Dispose();
                }
            }
        }

        class LockedBitmap(WicBitmapImpl parent, IWICBitmapLock l, APixelFormat format) : ILockedFramebuffer
        {
            private readonly WicBitmapImpl _parent = parent;
            private readonly IWICBitmapLock _lock = l;
            private readonly APixelFormat _format = format;

            public void Dispose()
            {
                _lock.Dispose();
                _parent.Version++;
            }

            public IntPtr Address => _lock.Data.DataPointer;
            public PixelSize Size => _lock.Size.ToAvalonia();
            public int RowBytes => (int)_lock.Stride;
            public Vector Dpi => _parent.Dpi;
            public APixelFormat Format => _format;
            public AlphaFormat AlphaFormat => _parent.AlphaFormat ?? Avalonia.Platform.AlphaFormat.Premul;

        }

        APixelFormat? IReadableBitmapImpl.Format => PixelFormat;
        AlphaFormat? IReadableBitmapImpl.AlphaFormat => AlphaFormat;

        public ILockedFramebuffer Lock()
        {
            if (PixelFormat is not APixelFormat pixelFormat)
                throw new InvalidOperationException("Bitmap pixel format is unknown.");

            return new LockedBitmap(this, WicImpl.Lock(BitmapLockFlags.Write), pixelFormat);
        }
    }
}
