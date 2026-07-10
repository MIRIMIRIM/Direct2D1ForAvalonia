using System;
using Avalonia.Platform;
using Vortice.Direct2D1;
using RenderTargetProperties = Vortice.Direct2D1.RenderTargetProperties;

namespace MIR.Direct2D1ForAvalonia.Media
{
    internal class WicRenderTargetBitmapImpl : WicBitmapImpl, IDrawingContextLayerImpl, IRenderTargetBitmapImpl
    {
        private readonly ID2D1RenderTarget _renderTarget;
        private DrawingContextImpl? _reusableDrawingContext;
        private bool? _reusableUseScaledDrawing;

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
            if (_reusableDrawingContext is not null)
            {
                _reusableDrawingContext.ReleaseRetainedNativeResources();
                _reusableDrawingContext = null;
            }

            _renderTarget.Dispose();

            base.Dispose();
        }

        public IDrawingContextImpl CreateDrawingContext() => CreateDrawingContext(useScaledDrawing: false);

        public virtual IDrawingContextImpl CreateDrawingContext(bool useScaledDrawing)
            => CreateDrawingContext(useScaledDrawing, null);

        public bool IsCorrupted => false;

        public IDrawingContextImpl CreateDrawingContext(bool useScaledDrawing, Action? finishedCallback)
        {
            Action combined = () =>
            {
                Version++;
                finishedCallback?.Invoke();
            };

            // Only reuse when no outer finishedCallback is required (common bitmap path).
            // Callers that pass a custom callback get a one-shot session to preserve semantics.
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
                        null,
                        _renderTarget,
                        useScaledDrawing,
                        finishedCallback: combined);
                    // WIC RTs QI to a device context we own — keep it across sessions.
                    _reusableDrawingContext.EnableSessionReuse();
                }
                else
                {
                    _reusableDrawingContext.ReopenSession(finishedCallback: combined);
                }

                return _reusableDrawingContext;
            }

            return new DrawingContextImpl(null, _renderTarget, useScaledDrawing, finishedCallback: combined);
        }

        public void Blit(IDrawingContextImpl context)
        {
            if (context is not DrawingContextImpl d2dContext)
                throw new InvalidOperationException("Blit requires a Direct2D drawing context.");

            var rect = new Rect(PixelSize.ToSizeWithDpi(Dpi));

            d2dContext.PushBitmapBlendMode(Avalonia.Media.Imaging.BitmapBlendingMode.Source);
            try
            {
                d2dContext.DrawBitmap(this, 1, rect, rect);
            }
            finally
            {
                d2dContext.PopBitmapBlendMode();
            }
        }

        public bool CanBlit => true;
    }
}
