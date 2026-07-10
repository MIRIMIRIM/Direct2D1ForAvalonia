#nullable enable

using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Platform;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WIC;
using APixelFormat = Avalonia.Platform.PixelFormat;

namespace MIR.Direct2D1ForAvalonia.Media.Imaging
{
    /// <summary>
    /// GPU-backed <see cref="IRenderTargetBitmapImpl"/> for Avalonia <c>RenderTargetBitmap</c>.
    /// <para>
    /// Drawing goes through a D3D11 texture + <see cref="ID2D1DeviceContext"/> (same path as
    /// window surfaces). CPU access (<see cref="Save"/>, <see cref="Lock"/>) uses a staging
    /// readback — pay once when the app actually needs pixels, not on every draw.
    /// </para>
    /// <para>
    /// Uses <c>useScaledDrawing: true</c> by default so DIP coordinates match Avalonia.Skia's
    /// <c>RenderTargetBitmapImpl</c> (which always scales to DPI).
    /// </para>
    /// </summary>
    internal sealed class D2DGpuRenderTargetBitmapImpl : BitmapImpl, IRenderTargetBitmapImpl, IReadableBitmapImpl, ILayerFactory
    {
        private readonly ID2D1DeviceContext _deviceContext;
        private readonly ID3D11Texture2D _texture;
        private readonly IDXGISurface _surface;
        private readonly ID2D1Bitmap1 _targetBitmap;
        private readonly ID3D11Texture2D _staging;
        private readonly Vector _dpi;
        private DrawingContextImpl? _reusableDrawingContext;
        private bool? _reusableUseScaledDrawing;
        private byte[]? _cpuPixels;
        private bool _cpuPixelsValid;
        private bool _disposed;

        public D2DGpuRenderTargetBitmapImpl(PixelSize size, Vector dpi)
        {
            if (Direct2D1Platform.Direct2D1Device is null || Direct2D1Platform.Direct3D11Device is null)
            {
                throw new InvalidOperationException(
                    "Direct2D GPU device is not initialised. Call UseDirect2D1() before creating RenderTargetBitmap.");
            }

            if (size.Width <= 0 || size.Height <= 0)
                throw new ArgumentOutOfRangeException(nameof(size));

            PixelSize = size;
            _dpi = dpi;

            var desc = new Texture2DDescription
            {
                Width = (uint)size.Width,
                Height = (uint)size.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            };

            _texture = Direct2D1Platform.Direct3D11Device.CreateTexture2D(desc);
            _surface = _texture.QueryInterface<IDXGISurface>();
            _deviceContext = Direct2D1Platform.Direct2D1Device.CreateDeviceContext(DeviceContextOptions.None);

            _targetBitmap = _deviceContext.CreateBitmapFromDxgiSurface(
                _surface,
                new BitmapProperties1(
                    new Vortice.DCommon.PixelFormat
                    {
                        AlphaMode = Vortice.DCommon.AlphaMode.Premultiplied,
                        Format = Format.B8G8R8A8_UNorm
                    },
                    (float)dpi.X,
                    (float)dpi.Y,
                    BitmapOptions.Target));

            _deviceContext.Target = _targetBitmap;
            _deviceContext.SetDpi((float)dpi.X, (float)dpi.Y);

            _staging = Direct2D1Platform.Direct3D11Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)size.Width,
                Height = (uint)size.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            });
        }

        public override Vector Dpi => _dpi;

        public override PixelSize PixelSize { get; }

        public APixelFormat? PixelFormat => APixelFormat.Bgra8888;

        public AlphaFormat? AlphaFormat => Avalonia.Platform.AlphaFormat.Premul;

        APixelFormat? IReadableBitmapImpl.Format => PixelFormat;

        AlphaFormat? IReadableBitmapImpl.AlphaFormat => AlphaFormat;

        public bool IsCorrupted => false;

        public IDrawingContextImpl CreateDrawingContext()
            // Match Avalonia.Skia.RenderTargetBitmapImpl: always scale drawing to DPI (DIP coords).
            => CreateDrawingContext(useScaledDrawing: true);

        public IDrawingContextImpl CreateDrawingContext(bool useScaledDrawing)
            => CreateDrawingContext(useScaledDrawing, null);

        public IDrawingContextImpl CreateDrawingContext(bool useScaledDrawing, Action? finishedCallback)
        {
            EnsureNotDisposed();

            Action combined = () =>
            {
                // GPU content changed — CPU mirror is stale until next Lock/Save.
                _cpuPixelsValid = false;
                Version++;
                finishedCallback?.Invoke();
            };

            if (finishedCallback is null)
            {
                if (_reusableDrawingContext is null || _reusableUseScaledDrawing != useScaledDrawing)
                {
                    if (_reusableDrawingContext is not null)
                    {
                        _reusableDrawingContext.ReleaseRetainedNativeResources();
                        _reusableDrawingContext = null;
                    }

                    _reusableUseScaledDrawing = useScaledDrawing;
                    _reusableDrawingContext = new DrawingContextImpl(
                        this,
                        _deviceContext,
                        useScaledDrawing,
                        finishedCallback: combined);
                    _reusableDrawingContext.EnableSessionReuse();
                }
                else
                {
                    _reusableDrawingContext.ReopenSession(finishedCallback: combined);
                }

                return _reusableDrawingContext;
            }

            return new DrawingContextImpl(
                this,
                _deviceContext,
                useScaledDrawing,
                finishedCallback: combined);
        }

        public IDrawingContextLayerImpl CreateLayer(Avalonia.Size size)
            => D2DRenderTargetBitmapImpl.CreateCompatiblePooled(_deviceContext, size);

        public override OptionalDispose<ID2D1Bitmap1> GetDirect2DBitmap(ID2D1RenderTarget target)
        {
            // Same-device path: no WIC round-trip — draw the GPU bitmap directly.
            return new OptionalDispose<ID2D1Bitmap1>(_targetBitmap, dispose: false);
        }

        public ILockedFramebuffer Lock()
        {
            EnsureNotDisposed();
            EnsureCpuPixels();
            var handle = GCHandle.Alloc(_cpuPixels!, GCHandleType.Pinned);
            return new GpuLockedFramebuffer(handle, PixelSize, PixelSize.Width * 4, Dpi);
        }

        protected internal override void Save(Stream stream, ContainerFormat containerFormat, int? quality)
        {
            EnsureNotDisposed();
            EnsureCpuPixels();

            // Encode from the CPU snapshot via a temporary WIC bitmap.
            using var wic = Direct2D1Platform.ImagingFactory.CreateBitmap(
                (uint)PixelSize.Width,
                (uint)PixelSize.Height,
                Vortice.WIC.PixelFormat.Format32bppPBGRA,
                BitmapCreateCacheOption.CacheOnDemand);

            using (var locked = wic.Lock(BitmapLockFlags.Write))
            {
                var dstStride = (int)locked.Stride;
                var srcStride = PixelSize.Width * 4;
                var height = PixelSize.Height;
                unsafe
                {
                    fixed (byte* srcBase = _cpuPixels!)
                    {
                        var dstBase = (byte*)locked.Data.DataPointer;
                        for (var y = 0; y < height; y++)
                        {
                            Buffer.MemoryCopy(
                                srcBase + y * srcStride,
                                dstBase + y * dstStride,
                                dstStride,
                                Math.Min(srcStride, dstStride));
                        }
                    }
                }
            }

            using var encoder = Direct2D1Platform.ImagingFactory.CreateEncoder(containerFormat, stream);
            using var frame = encoder.CreateNewFrame(out var props);
            try
            {
                ConfigureEncoderOptions(props, containerFormat, quality);
                frame.Initialize(props);
                frame.WriteSource(wic);
                frame.Commit();
                encoder.Commit();
            }
            finally
            {
                props.Dispose();
            }
        }

        public override void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            if (_reusableDrawingContext is not null)
            {
                _reusableDrawingContext.ReleaseRetainedNativeResources();
                _reusableDrawingContext = null;
            }

            _deviceContext.Target = null;
            _staging.Dispose();
            _targetBitmap.Dispose();
            _surface.Dispose();
            _texture.Dispose();
            _deviceContext.Dispose();
            base.Dispose();
        }

        private void EnsureCpuPixels()
        {
            if (_cpuPixelsValid && _cpuPixels is not null)
                return;

            var w = PixelSize.Width;
            var h = PixelSize.Height;
            var needed = checked(w * h * 4);
            if (_cpuPixels is null || _cpuPixels.Length < needed)
                _cpuPixels = new byte[needed];

            var context = Direct2D1Platform.Direct3D11ImmediateContext;
            context.CopyResource(_staging, _texture);
            var mapped = context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                unsafe
                {
                    var src = (byte*)mapped.DataPointer;
                    var srcStride = (int)mapped.RowPitch;
                    var dstStride = w * 4;
                    fixed (byte* dstBase = _cpuPixels)
                    {
                        for (var y = 0; y < h; y++)
                        {
                            Buffer.MemoryCopy(
                                src + y * srcStride,
                                dstBase + y * dstStride,
                                dstStride,
                                dstStride);
                        }
                    }
                }
            }
            finally
            {
                context.Unmap(_staging, 0);
            }

            _cpuPixelsValid = true;
        }

        private void EnsureNotDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(D2DGpuRenderTargetBitmapImpl));
        }

        private sealed class GpuLockedFramebuffer : ILockedFramebuffer
        {
            private GCHandle _handle;
            private bool _disposed;

            public GpuLockedFramebuffer(GCHandle handle, PixelSize size, int rowBytes, Vector dpi)
            {
                _handle = handle;
                Size = size;
                RowBytes = rowBytes;
                Dpi = dpi;
            }

            public IntPtr Address => _handle.AddrOfPinnedObject();
            public PixelSize Size { get; }
            public int RowBytes { get; }
            public Vector Dpi { get; }
            public APixelFormat Format => APixelFormat.Bgra8888;
            public AlphaFormat AlphaFormat => Avalonia.Platform.AlphaFormat.Premul;

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                // Read-only snapshot; Avalonia uses Lock mainly for CopyPixels/export.
                if (_handle.IsAllocated)
                    _handle.Free();
            }
        }
    }
}
