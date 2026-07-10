using Avalonia.Media;
using Vortice.Direct2D1;

namespace MIR.Direct2D1ForAvalonia.Media
{
    internal class LinearGradientBrushImpl : BrushImpl
    {
        public LinearGradientBrushImpl(
            ILinearGradientBrush brush,
            ID2D1RenderTarget target,
            Rect destinationRect,
            D2DDeviceResourceCache deviceResources)
        {
            if (brush.GradientStops.Count == 0)
            {
                return;
            }

            // Full brush (stops + pixel start/end + opacity + transform) is device-cached so
            // template UI and GradientFill do not CreateLinearGradientBrush every draw.
            PlatformBrush = deviceResources.GetOrCreateLinearGradientBrush(target, brush, destinationRect);
            IsCached = true;
        }
    }
}
