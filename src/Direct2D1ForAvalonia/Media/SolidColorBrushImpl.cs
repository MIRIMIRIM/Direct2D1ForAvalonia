using Avalonia.Media;
using Vortice.Direct2D1;

namespace MIR.Direct2D1ForAvalonia.Media
{
    internal class SolidColorBrushImpl : BrushImpl
    {
        public SolidColorBrushImpl(ISolidColorBrush? brush, ID2D1RenderTarget target)
        {
            PlatformBrush = target.CreateSolidColorBrush(
                brush?.Color.ToDirect2D() ?? new Vortice.Mathematics.Color(),
                new BrushProperties
                {
                    Opacity = brush != null ? (float)brush.Opacity : 1.0f,
                    Transform = target.Transform
                }
            );
        }
    }
}
