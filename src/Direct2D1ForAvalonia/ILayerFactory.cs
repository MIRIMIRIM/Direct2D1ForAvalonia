using Avalonia.Platform;

namespace MIR.Direct2D1ForAvalonia
{
    internal interface ILayerFactory
    {
        IDrawingContextLayerImpl CreateLayer(Size size);
    }
}