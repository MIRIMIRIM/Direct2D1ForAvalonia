using System;
using MIR.Direct2D1ForAvalonia.Media;
using MIR.Direct2D1ForAvalonia.Media.Imaging;
using Avalonia.Platform;
using Vortice.Direct2D1;
using Vortice.DXGI;

namespace MIR.Direct2D1ForAvalonia
{   
    internal abstract class SwapChainRenderTarget : IRenderTarget, ILayerFactory
    {
        private Vortice.Mathematics.SizeI _savedSize;
        private Vortice.Mathematics.Size _savedDpi;
        private ID2D1DeviceContext? _deviceContext;
        private IDXGISwapChain1? _swapChain;

        /// <summary>
        /// Creates a drawing context for a rendering session.
        /// </summary>
        /// <returns>An <see cref="Avalonia.Platform.IDrawingContextImpl"/>.</returns>
        internal IDrawingContextImpl CreateDrawingContext(bool useScaledDrawing)
        {
            var size = GetWindowSize();
            var dpi = GetWindowDpi();

            if (size != _savedSize || dpi != _savedDpi)
            {
                _savedSize = size;
                _savedDpi = dpi;

                Resize();
            }

            var deviceContext = _deviceContext ?? throw new InvalidOperationException("Device context is not available.");
            return new DrawingContextImpl(this, deviceContext, useScaledDrawing, _swapChain);
        }

        public Avalonia.Platform.RenderTargetProperties Properties => new()
        {
            IsSuitableForDirectRendering = true
        };

        public IDrawingContextImpl CreateDrawingContext(IRenderTarget.RenderTargetSceneInfo sceneInfo, out RenderTargetDrawingContextProperties properties)
        {
            properties = default;
            return CreateDrawingContext(useScaledDrawing: false);
        }

        public IDrawingContextLayerImpl CreateLayer(Size size)
        {
            if (_deviceContext == null)
            {
                CreateDeviceContext();
            }

            var deviceContext = _deviceContext ?? throw new InvalidOperationException("Device context is not available.");
            return D2DRenderTargetBitmapImpl.CreateCompatible(deviceContext, size);
        }

        public void Dispose()
        {
            _deviceContext?.Dispose();
            _swapChain?.Dispose();
        }

        private void Resize()
        {
            _deviceContext?.Dispose();
            _deviceContext = null;

            _swapChain?.ResizeBuffers(0, 0, 0, Format.Unknown, SwapChainFlags.None);

            CreateDeviceContext();
        }

        private void CreateSwapChain()
        {
            var swapChainDescription = new SwapChainDescription1
            {
                Width = (uint)_savedSize.Width,
                Height = (uint)_savedSize.Height,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription
                {
                    Count = 1,
                    Quality = 0,
                },
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 1,
                SwapEffect = SwapEffect.Discard,
            };

            using (var dxgiAdapter = Direct2D1Platform.DxgiDevice.GetAdapter())
            using (var dxgiFactory = dxgiAdapter.GetParent<IDXGIFactory2>())
            {
                _swapChain = CreateSwapChain(dxgiFactory, swapChainDescription);
            }
        }

        private void CreateDeviceContext()
        {
            _deviceContext = Direct2D1Platform.Direct2D1Device.CreateDeviceContext(DeviceContextOptions.None);
            _deviceContext.SetDpi(_savedDpi.Width, _savedDpi.Height);

            if (_swapChain == null)
            {
                CreateSwapChain();
            }

            var swapChain = _swapChain ?? throw new InvalidOperationException("Swap chain is not available.");

            using (var dxgiBackBuffer = swapChain.GetBuffer<IDXGISurface>(0))
            using (var d2dBackBuffer = _deviceContext.CreateBitmapFromDxgiSurface(
                dxgiBackBuffer,
                    new BitmapProperties1(
                    new Vortice.DCommon.PixelFormat
                    {
                        AlphaMode = Vortice.DCommon.AlphaMode.Premultiplied,
                        Format = Format.B8G8R8A8_UNorm
                    },
                    _savedDpi.Width,
                    _savedDpi.Height,
                    BitmapOptions.Target | BitmapOptions.CannotDraw)))
            {
                _deviceContext.Target = d2dBackBuffer;
            }
        }

        protected abstract IDXGISwapChain1 CreateSwapChain(IDXGIFactory2 dxgiFactory, SwapChainDescription1 swapChainDesc);

        protected abstract Vortice.Mathematics.Size GetWindowDpi();

        protected abstract Vortice.Mathematics.SizeI GetWindowSize();
    }
}