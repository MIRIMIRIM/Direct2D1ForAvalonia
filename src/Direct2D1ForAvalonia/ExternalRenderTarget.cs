using System;
using MIR.Direct2D1ForAvalonia.Media;
using MIR.Direct2D1ForAvalonia.Media.Imaging;
using Avalonia.Platform;
using SharpGen.Runtime;
using Vortice.Direct2D1;
using RenderTargetProperties = Avalonia.Platform.RenderTargetProperties;

namespace MIR.Direct2D1ForAvalonia
{
    class ExternalRenderTarget : IRenderTarget, ILayerFactory
    {
        private readonly IExternalDirect2DRenderTargetSurface _externalRenderTargetProvider;
        private DrawingContextImpl? _reusableDrawingContext;
        private ID2D1RenderTarget? _reusableTarget;
        private bool? _reusableUseScaledDrawing;
        /// <summary>
        /// Context deferred for release after the current session closes. Must not call
        /// <see cref="DrawingContextImpl.ReleaseRetainedNativeResources"/> from finishedCallback
        /// while Dispose still has <c>_sessionOpen == true</c>.
        /// </summary>
        private DrawingContextImpl? _pendingReleaseContext;

        public ExternalRenderTarget(
            IExternalDirect2DRenderTargetSurface externalRenderTargetProvider)
        {
            _externalRenderTargetProvider = externalRenderTargetProvider;
        }

        public void Dispose()
        {
            FlushPendingRelease();
            if (_reusableDrawingContext is not null)
            {
                _reusableDrawingContext.ReleaseRetainedNativeResources();
                _reusableDrawingContext = null;
            }

            _reusableTarget = null;
            _reusableUseScaledDrawing = null;
            _externalRenderTargetProvider.DestroyRenderTarget();
        }

        internal IDrawingContextImpl CreateDrawingContext(bool useScaledDrawing)
        {
            FlushPendingRelease();

            var target = _externalRenderTargetProvider.GetOrCreateRenderTarget();
            _externalRenderTargetProvider.BeforeDrawing();

            // finishedCallback runs while the session is still open. On recreate it only stashes
            // the context and destroys the surface; cleanupCallback flushes its native resources
            // after DrawingContextImpl marks the session closed.
            Action finishedCallback = () =>
            {
                try
                {
                    _externalRenderTargetProvider.AfterDrawing();
                }
                catch (SharpGenException ex) when ((uint)ex.HResult == 0x8899000C) // D2DERR_RECREATE_TARGET
                {
                    if (_reusableTarget is ID2D1DeviceContext dc && dc.Device is { } lostDevice)
                    {
                        D2DDeviceResourceCache.InvalidateForDevice(lostDevice);
                        lostDevice.Dispose();
                    }

                    // Defer ReleaseRetainedNativeResources — session is still open here.
                    if (_reusableDrawingContext is not null)
                    {
                        _pendingReleaseContext = _reusableDrawingContext;
                        _reusableDrawingContext = null;
                    }

                    _reusableTarget = null;
                    _reusableUseScaledDrawing = null;
                    _externalRenderTargetProvider.DestroyRenderTarget();
                }
            };

            // Drop reuse if the surface handed us a different RT or DPI scaling mode changed.
            if (_reusableDrawingContext is null
                || !ReferenceEquals(_reusableTarget, target)
                || _reusableUseScaledDrawing != useScaledDrawing)
            {
                if (_reusableDrawingContext is not null)
                {
                    _reusableDrawingContext.ReleaseRetainedNativeResources();
                    _reusableDrawingContext = null;
                }

                _reusableTarget = target;
                _reusableUseScaledDrawing = useScaledDrawing;
                _reusableDrawingContext = new DrawingContextImpl(
                    this,
                    target,
                    useScaledDrawing,
                    finishedCallback: finishedCallback,
                    cleanupCallback: FlushPendingRelease);
                _reusableDrawingContext.EnableSessionReuse();
            }
            else
            {
                _reusableDrawingContext.ReopenSession(
                    finishedCallback: finishedCallback,
                    cleanupCallback: FlushPendingRelease);
            }

            return _reusableDrawingContext;
        }

        private void FlushPendingRelease()
        {
            if (_pendingReleaseContext is null)
                return;

            var doomed = _pendingReleaseContext;
            doomed.ReleaseRetainedNativeResources();
            if (ReferenceEquals(_pendingReleaseContext, doomed))
                _pendingReleaseContext = null;
        }

        public RenderTargetProperties Properties => new()
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
            var renderTarget = _externalRenderTargetProvider.GetOrCreateRenderTarget();
            return D2DRenderTargetBitmapImpl.CreateCompatiblePooled(renderTarget, size);
        }
    }
}
