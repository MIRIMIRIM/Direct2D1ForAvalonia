#nullable enable

using System;
using Avalonia;
using Avalonia.Platform;
using MIR.Direct2D1ForAvalonia.Media;
using MIR.Direct2D1ForAvalonia.Media.Imaging;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace MIR.Direct2D1ForAvalonia
{
    /// <summary>
    /// A GPU-backed offscreen Direct2D render target for benchmarking and headless rendering.
    /// <para>
    /// Avalonia <c>RenderTargetBitmap</c> uses <see cref="Media.Imaging.D2DGpuRenderTargetBitmapImpl"/>
    /// (GPU) when the D2D device is available, with WIC as fallback. This class is a standalone
    /// GPU offscreen surface for benchmarks: D3D11 texture + D2D device context, same draw path
    /// as <see cref="D3D11TextureRenderTarget"/> without a window/swap chain.
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
        /// <param name="forceNewInstance">
        /// When true, replaces only the managed <see cref="DrawingContextImpl"/> wrapper around
        /// the same native device context. Use <see cref="CreateCompatibleDrawingContext"/> when
        /// a fresh compatible render target/native context is required.
        /// </param>
        public IDrawingContextImpl CreateDrawingContext(bool forceNewInstance = false)
        {
            EnsureNotDisposed();
            if (forceNewInstance && _reusableContext is not null)
            {
                // Borrowed DC is not owned by DrawingContextImpl; still clear the managed pool.
                _reusableContext.ReleaseRetainedNativeResources();
                _reusableContext = null;
            }

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
        /// Creates a one-shot drawing context on a fresh compatible bitmap render target.
        /// The compatible target QIs to its own <see cref="ID2D1DeviceContext"/>, while sharing
        /// the parent native D2D device and therefore the device resource cache.
        /// </summary>
        public IDrawingContextImpl CreateCompatibleDrawingContext()
        {
            EnsureNotDisposed();

            var dipSize = PixelSize.ToSizeWithDpi(_dpi);
            var compatible = _deviceContext.CreateCompatibleRenderTarget(
                new Vortice.Mathematics.Size((float)dipSize.Width, (float)dipSize.Height),
                null,
                null,
                CompatibleRenderTargetOptions.None);

            try
            {
                return new DrawingContextImpl(
                    this,
                    compatible,
                    useScaledDrawing: false,
                    cleanupCallback: compatible.Dispose);
            }
            catch
            {
                compatible.Dispose();
                throw;
            }
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
        /// Device-scoped simple-session command-list stats (hits across DC instances).
        /// </summary>
        public (int Hits, int Stores) GetDeviceCommandListStats()
        {
            EnsureNotDisposed();
            var cache = D2DDeviceResourceCache.For(_deviceContext);
            try
            {
                return (cache.CommandListHits, cache.CommandListStores);
            }
            finally
            {
                cache.ReleaseLease();
            }
        }

        public void ResetDeviceCommandListStats()
        {
            EnsureNotDisposed();
            var cache = D2DDeviceResourceCache.For(_deviceContext);
            try
            {
                cache.ResetCommandListStats();
            }
            finally
            {
                cache.ReleaseLease();
            }
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
            return D2DRenderTargetBitmapImpl.CreateCompatiblePooled(_deviceContext, size);
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
            if (_reusableContext is not null)
            {
                _reusableContext.ReleaseRetainedNativeResources();
                _reusableContext = null;
            }

            _staging.Dispose();
            _targetBitmap.Dispose();
            _surface.Dispose();
            _texture.Dispose();
            _deviceContext.Dispose();
        }
    }
}
