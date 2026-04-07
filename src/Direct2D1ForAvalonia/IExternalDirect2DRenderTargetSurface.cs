using Avalonia.Platform.Surfaces;
using Vortice.Direct2D1;

namespace MIR.Direct2D1ForAvalonia
{
    public interface IExternalDirect2DRenderTargetSurface : IPlatformRenderSurface
    {
        ID2D1RenderTarget GetOrCreateRenderTarget();
        void DestroyRenderTarget();
        void BeforeDrawing();
        void AfterDrawing();
    }
}