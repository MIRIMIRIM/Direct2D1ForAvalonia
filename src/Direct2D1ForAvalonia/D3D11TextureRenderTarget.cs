#nullable enable

using System;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using Avalonia.Win32.DirectX;
using MIR.Direct2D1ForAvalonia.Media;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using RenderTargetProperties = Avalonia.Platform.RenderTargetProperties;

namespace MIR.Direct2D1ForAvalonia
{
    internal sealed class D3D11TextureRenderTarget : IRenderTarget, ILayerFactory
    {
        private readonly IDirect3D11TextureRenderTarget _target;
        private readonly ID2D1DeviceContext _deviceContext;
        private Vector _lastDpi = new Vector(96, 96);
        private bool _disposed;

        public D3D11TextureRenderTarget(
            IDirect3D11TexturePlatformSurface surface,
            IPlatformGraphicsContext graphicsContext)
        {
            _target = surface.CreateRenderTarget(
                graphicsContext,
                Direct2D1Platform.Direct3D11Device.NativePointer);
            _deviceContext = Direct2D1Platform.Direct2D1Device.CreateDeviceContext(DeviceContextOptions.None);
        }

        public RenderTargetProperties Properties => new()
        {
            IsSuitableForDirectRendering = true,
            RetainsPreviousFrameContents = false
        };

        public PlatformRenderTargetState PlatformRenderTargetState =>
            _disposed ? PlatformRenderTargetState.Disposed : _target.State;

        public IDrawingContextImpl CreateDrawingContext(
            IRenderTarget.RenderTargetSceneInfo sceneInfo,
            out RenderTargetDrawingContextProperties properties)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(D3D11TextureRenderTarget));
            }

            properties = default;

            var session = _target.BeginDraw();
            ID2D1Bitmap1? d2dBitmap = null;
            IDXGISurface? dxgiSurface = null;
            ID3D11Texture2D? texture = null;

            try
            {
                if (session.Size.Width <= 0 || session.Size.Height <= 0)
                {
                    throw new InvalidOperationException("The D3D11 texture render target has an invalid size.");
                }

                if (session.Scaling <= 0)
                {
                    throw new InvalidOperationException("The D3D11 texture render target has an invalid scaling factor.");
                }

                var dpi = new Vector(session.Scaling * 96.0, session.Scaling * 96.0);
                _lastDpi = dpi;

                _deviceContext.SetDpi((float)dpi.X, (float)dpi.Y);

                var texturePointer = session.D3D11Texture2D;
                if (texturePointer == IntPtr.Zero)
                {
                    throw new InvalidOperationException("The D3D11 texture render target returned a null texture.");
                }

                Marshal.AddRef(texturePointer);
                texture = new ID3D11Texture2D(texturePointer);
                dxgiSurface = texture.QueryInterface<IDXGISurface>();
                d2dBitmap = _deviceContext.CreateBitmapFromDxgiSurface(
                    dxgiSurface,
                    new BitmapProperties1(
                        new Vortice.DCommon.PixelFormat
                        {
                            AlphaMode = Vortice.DCommon.AlphaMode.Premultiplied,
                            Format = Format.B8G8R8A8_UNorm
                        },
                        (float)dpi.X,
                        (float)dpi.Y,
                        BitmapOptions.Target | BitmapOptions.CannotDraw));

                _deviceContext.Target = d2dBitmap;

                var targetTransform = session.Offset == default
                    ? (Matrix?)null
                    : Matrix.CreateTranslation(
                        session.Offset.X / session.Scaling,
                        session.Offset.Y / session.Scaling);

                return new DrawingContextImpl(
                    this,
                    _deviceContext,
                    useScaledDrawing: false,
                    finishedCallback: FlushDirect3DDevice,
                    targetTransform: targetTransform,
                    cleanupCallback: () =>
                    {
                        try
                        {
                            _deviceContext.Target = null;
                            d2dBitmap.Dispose();
                            dxgiSurface.Dispose();
                            texture.Dispose();
                        }
                        finally
                        {
                            session.Dispose();
                        }
                    });
            }
            catch
            {
                _deviceContext.Target = null;
                d2dBitmap?.Dispose();
                dxgiSurface?.Dispose();
                texture?.Dispose();
                session.Dispose();
                throw;
            }
        }

        public IDrawingContextLayerImpl CreateLayer(Size size)
        {
            var dpi = _lastDpi;
            var pixelSize = PixelSize.FromSizeWithDpi(size, dpi);
            return new WicRenderTargetBitmapImpl(pixelSize, dpi);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _deviceContext.Target = null;
            _deviceContext.Dispose();
            _target.Dispose();
        }

        private static void FlushDirect3DDevice()
        {
            Direct2D1Platform.Direct3D11ImmediateContext.Flush();
        }
    }
}
