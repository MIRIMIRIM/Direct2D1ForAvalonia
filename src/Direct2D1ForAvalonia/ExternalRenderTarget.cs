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

        public ExternalRenderTarget(
            IExternalDirect2DRenderTargetSurface externalRenderTargetProvider)
        {
            _externalRenderTargetProvider = externalRenderTargetProvider;
        }

        public void Dispose()
        {
            _reusableDrawingContext = null;
            _reusableTarget = null;
            _externalRenderTargetProvider.DestroyRenderTarget();
        }

        internal IDrawingContextImpl CreateDrawingContext(bool useScaledDrawing)
        {
            var target = _externalRenderTargetProvider.GetOrCreateRenderTarget();
            _externalRenderTargetProvider.BeforeDrawing();

            Action finishedCallback = () =>
            {
                try
                {
                    _externalRenderTargetProvider.AfterDrawing();
                }
                catch (SharpGenException ex) when ((uint)ex.HResult == 0x8899000C) // D2DERR_RECREATE_TARGET
                {
                    _reusableDrawingContext = null;
                    _reusableTarget = null;
                    _externalRenderTargetProvider.DestroyRenderTarget();
                }
            };

            // Drop reuse if the surface handed us a different RT (device loss / resize).
            if (_reusableDrawingContext is null || !ReferenceEquals(_reusableTarget, target))
            {
                _reusableTarget = target;
                _reusableDrawingContext = new DrawingContextImpl(
                    this,
                    target,
                    useScaledDrawing,
                    finishedCallback: finishedCallback);
                _reusableDrawingContext.EnableSessionReuse();
            }
            else
            {
                _reusableDrawingContext.ReopenSession(finishedCallback: finishedCallback);
            }

            return _reusableDrawingContext;
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
            return D2DRenderTargetBitmapImpl.CreateCompatible(renderTarget, size);
        }
    }
}
