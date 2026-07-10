#nullable enable

using System;
using Avalonia;
using Avalonia.Platform;
using MIR.Direct2D1ForAvalonia.Media;
using MIR.Direct2D1ForAvalonia.Media.Imaging;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace MIR.Direct2D1ForAvalonia
{
    /// <summary>
    /// A GPU-backed offscreen Direct2D render target for benchmarking and headless rendering.
    /// <para>
    /// The normal <see cref="Media.Imaging.WicRenderTargetBitmapImpl"/> path (used by Avalonia's
    /// <c>RenderTargetBitmap</c>) is a CPU/WIC software rasteriser. Real window rendering instead
    /// goes through <see cref="D3D11TextureRenderTarget"/>, which draws onto a D3D11 texture with a
    /// GPU-accelerated <see cref="Vortice.Direct2D1.ID2D1DeviceContext"/>. This class reproduces that
    /// GPU path without needing a window/swap chain: it allocates its own render-target texture,
    /// wraps it as a D2D target bitmap, and drives the exact same <see cref="DrawingContextImpl"/>.
    /// </para>
    /// <para>
    /// Requires <see cref="Direct2D1Platform"/> to be initialised (i.e. <c>UseDirect2D1()</c> applied
    /// to the <see cref="AppBuilder"/>). Windows only.
    /// </para>
    /// </summary>
    public sealed class Direct2DGpuOffscreenTarget : IDisposable, ILayerFactory
    {
        private readonly Vortice.Direct2D1.ID2D1DeviceContext _deviceContext;
        private readonly ID3D11Texture2D _texture;
        private readonly IDXGISurface _surface;
        private readonly Vortice.Direct2D1.ID2D1Bitmap1 _targetBitmap;
        private readonly ID3D11Texture2D _staging;
        private readonly Vector _dpi;
        private DrawingContextImpl? _reusableContext;
        private bool _disposed;

        public Direct2DGpuOffscreenTarget(PixelSize pixelSize, Vector dpi)
        {
            if (Direct2D1Platform.Direct2D1Device is null)
                throw new InvalidOperationException(
                    "Direct2D is not initialised. Apply UseDirect2D1() to the AppBuilder first.");

            PixelSize = pixelSize;
            _dpi = dpi;

            var desc = new Texture2DDescription
            {
                Width = (uint)pixelSize.Width,
                Height = (uint)pixelSize.Height,
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
            _deviceContext = Direct2D1Platform.Direct2D1Device.CreateDeviceContext(
                Vortice.Direct2D1.DeviceContextOptions.None);

            _targetBitmap = _deviceContext.CreateBitmapFromDxgiSurface(
                _surface,
                new Vortice.Direct2D1.BitmapProperties1(
                    new Vortice.DCommon.PixelFormat
                    {
                        AlphaMode = Vortice.DCommon.AlphaMode.Premultiplied,
                        Format = Format.B8G8R8A8_UNorm
                    },
                    (float)dpi.X,
                    (float)dpi.Y,
                    Vortice.Direct2D1.BitmapOptions.Target));

            _deviceContext.Target = _targetBitmap;
            _deviceContext.SetDpi((float)dpi.X, (float)dpi.Y);

            // A 1x1 CPU-readable staging texture. Copying into it and Map()-ing forces the GPU to
            // complete all prior render work — a deterministic barrier for benchmark timing.
            _staging = Direct2D1Platform.Direct3D11Device.CreateTexture2D(new Texture2DDescription
            {
                Width = 1,
                Height = 1,
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

        public PixelSize PixelSize { get; }

        /// <summary>
        /// Creates a drawing context bound to the GPU device context. Each open session calls
        /// BeginDraw on entry and EndDraw on Dispose. The underlying <see cref="DrawingContextImpl"/>
        /// is reused across frames to avoid per-frame stack/cache reallocations.
        /// </summary>
        public IDrawingContextImpl CreateDrawingContext()
        {
            EnsureNotDisposed();
            if (_reusableContext is null)
            {
                _reusableContext = new DrawingContextImpl(this, _deviceContext, useScaledDrawing: false);
                _reusableContext.EnableSessionReuse();
            }
            else
            {
                _reusableContext.ReopenSession();
            }

            return _reusableContext;
        }

        /// <summary>
        /// Blocks until the GPU has finished all submitted rendering. EndDraw only submits work to
        /// the D3D command queue; this forces completion so elapsed time reflects real GPU cost.
        /// </summary>
        public void WaitForGpu()
        {
            EnsureNotDisposed();
            var context = Direct2D1Platform.Direct3D11ImmediateContext;
            context.CopySubresourceRegion(
                _staging, 0, 0, 0, 0,
                _texture, 0,
                new Box(0, 0, 0, 1, 1, 1));

            var mapped = context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            context.Unmap(_staging, 0);
            _ = mapped;
        }

        /// <summary>
        /// Reads the rendered texture back to CPU memory as BGRA bytes. For verification only —
        /// copies the whole target through a staging texture, which is slow and not part of timing.
        /// </summary>
        public byte[] ReadBgra()
        {
            EnsureNotDisposed();
            var context = Direct2D1Platform.Direct3D11ImmediateContext;

            using var full = Direct2D1Platform.Direct3D11Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)PixelSize.Width,
                Height = (uint)PixelSize.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            });

            context.CopyResource(full, _texture);

            var mapped = context.Map(full, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var width = PixelSize.Width;
                var height = PixelSize.Height;
                var result = new byte[width * height * 4];
                unsafe
                {
                    var src = (byte*)mapped.DataPointer;
                    for (var y = 0; y < height; y++)
                    {
                        var srcRow = src + (y * mapped.RowPitch);
                        var dstRow = y * width * 4;
                        for (var x = 0; x < width * 4; x++)
                            result[dstRow + x] = srcRow[x];
                    }
                }
                return result;
            }
            finally
            {
                context.Unmap(full, 0);
            }
        }

        public IDrawingContextLayerImpl CreateLayer(Avalonia.Size size)
        {
            EnsureNotDisposed();
            // GPU-compatible intermediate target, matching D3D11TextureRenderTarget's layer needs.
            return D2DRenderTargetBitmapImpl.CreateCompatible(_deviceContext, size);
        }

        private void EnsureNotDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Direct2DGpuOffscreenTarget));
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _deviceContext.Target = null;
            // Drop the reusable context reference; the device context it borrows is disposed below.
            _reusableContext = null;
            _staging.Dispose();
            _targetBitmap.Dispose();
            _surface.Dispose();
            _texture.Dispose();
            _deviceContext.Dispose();
        }
    }
}
