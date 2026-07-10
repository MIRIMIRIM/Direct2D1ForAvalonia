using Avalonia.Media;
using Vortice.Direct2D1;

namespace MIR.Direct2D1ForAvalonia.Media
{
    internal class RadialGradientBrushImpl : BrushImpl
    {
        public RadialGradientBrushImpl(
            IRadialGradientBrush brush,
            ID2D1RenderTarget target,
            Rect destinationRect,
            D2DDeviceResourceCache deviceResources)
        {
            if (brush.GradientStops.Count == 0)
            {
                return;
            }

            // Full brush (stops + ellipse geometry + opacity + transform) is device-cached so
            // GradientFill radial cells do not CreateRadialGradientBrush every draw.
            PlatformBrush = deviceResources.GetOrCreateRadialGradientBrush(target, brush, destinationRect);
            IsCached = true;
        }
    }
}
