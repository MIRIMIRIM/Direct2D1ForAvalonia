using Avalonia.Media;
using Vortice.Direct2D1;

namespace MIR.Direct2D1ForAvalonia.Media
{
    internal class SolidColorBrushImpl : BrushImpl
    {
        public SolidColorBrushImpl(ISolidColorBrush? brush, ID2D1RenderTarget target)
            : this(
                brush?.Color ?? default,
                brush?.Opacity ?? 1.0,
                target)
        {
        }

        public SolidColorBrushImpl(Color color, double opacity, ID2D1RenderTarget target)
        {
            // Identity brush transform: solid colors have no pattern, and world transform is applied
            // by the device context. Avoid baking target.Transform so cached brushes stay valid under
            // any subsequent world transform.
            PlatformBrush = target.CreateSolidColorBrush(
                color.ToDirect2D(),
                new BrushProperties
                {
                    Opacity = (float)opacity,
                    Transform = System.Numerics.Matrix3x2.Identity
                });
        }
    }
}
