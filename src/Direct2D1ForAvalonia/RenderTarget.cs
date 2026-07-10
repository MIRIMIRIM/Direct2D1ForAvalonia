using MIR.Direct2D1ForAvalonia.Media;
using MIR.Direct2D1ForAvalonia.Media.Imaging;
using Avalonia.Platform;
using Vortice.Direct2D1;

namespace MIR.Direct2D1ForAvalonia
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RenderTarget"/> class.
    /// </summary>
    /// <param name="renderTarget">The render target.</param>
    internal class RenderTarget(ID2D1RenderTarget renderTarget) : IRenderTarget, ILayerFactory
    {
        /// <summary>
        /// The render target.
        /// </summary>
        private readonly ID2D1RenderTarget _renderTarget = renderTarget;
        private DrawingContextImpl? _reusableDrawingContext;
        private bool? _reusableUseScaledDrawing;

        /// <summary>
        /// Creates a drawing context for a rendering session.
        /// </summary>
        /// <returns>An <see cref="Avalonia.Platform.IDrawingContextImpl"/>.</returns>
        internal IDrawingContextImpl CreateDrawingContext(bool useScaledDrawing)
        {
            if (_reusableDrawingContext is null || _reusableUseScaledDrawing != useScaledDrawing)
            {
                _reusableUseScaledDrawing = useScaledDrawing;
                _reusableDrawingContext = new DrawingContextImpl(this, _renderTarget, useScaledDrawing);
                _reusableDrawingContext.EnableSessionReuse();
            }
            else
            {
                _reusableDrawingContext.ReopenSession();
            }

            return _reusableDrawingContext;
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
            return D2DRenderTargetBitmapImpl.CreateCompatible(_renderTarget, size);
        }

        public void Dispose()
        {
            _reusableDrawingContext = null;
            _renderTarget.Dispose();
        }
    }
}
