using System;
using Avalonia.Platform;
using Vortice.Direct2D1;
using RenderTargetProperties = Vortice.Direct2D1.RenderTargetProperties;

namespace Avalonia.Direct2D1.Media
{
    internal class WicRenderTargetBitmapImpl : WicBitmapImpl, IDrawingContextLayerImpl, IRenderTargetBitmapImpl
    {
        private readonly ID2D1RenderTarget _renderTarget;

        public WicRenderTargetBitmapImpl(
            PixelSize size,
            Vector dpi,
            PixelFormat? pixelFormat = null,
            AlphaFormat? alphaFormat = null)
            : base(size, dpi, pixelFormat, alphaFormat)
        {
            var props = new RenderTargetProperties
            {
                DpiX = (float)dpi.X,
                DpiY = (float)dpi.Y,
            };

            _renderTarget = Direct2D1Platform.Direct2D1Factory.CreateWicBitmapRenderTarget(
                WicImpl,
                props);
        }

        public override void Dispose()
        {
            _renderTarget.Dispose();

            base.Dispose();
        }

        public IDrawingContextImpl CreateDrawingContext() => CreateDrawingContext(useScaledDrawing: false);

        public virtual IDrawingContextImpl CreateDrawingContext(bool useScaledDrawing)
            => CreateDrawingContext(useScaledDrawing, null);

        public bool IsCorrupted => false;

        public IDrawingContextImpl CreateDrawingContext(bool useScaledDrawing, Action? finishedCallback)
        {
            return new DrawingContextImpl(null, _renderTarget, useScaledDrawing, finishedCallback: () =>
                {
                    Version++;
                    finishedCallback?.Invoke();
                });
        }

        public void Blit(IDrawingContextImpl context) => throw new NotSupportedException();
        public bool CanBlit => false;
    }
}
