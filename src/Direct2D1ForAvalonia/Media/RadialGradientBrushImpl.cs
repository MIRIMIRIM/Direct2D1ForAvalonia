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

            var centerPoint = brush.Center.ToPixels(destinationRect);
            var gradientOrigin = brush.GradientOrigin.ToPixels(destinationRect) - centerPoint;

            var radiusX = brush.RadiusX.ToValue(destinationRect.Width);
            var radiusY = brush.RadiusY.ToValue(destinationRect.Height);

            // Stop collection cached across frames; owned by the cache, not disposed here.
            var stops = deviceResources.GetOrCreateGradientStops(target, brush.GradientStops, brush.SpreadMethod);

            PlatformBrush = target.CreateRadialGradientBrush(
                new RadialGradientBrushProperties
                {
                    Center = centerPoint.ToVortice(),
                    GradientOriginOffset = gradientOrigin.ToVortice(),
                    RadiusX = (float)radiusX,
                    RadiusY = (float)radiusY
                },
                new BrushProperties
                {
                    Opacity = (float)brush.Opacity,
                    Transform = BrushTransform.Apply(brush, destinationRect, Matrix.Identity).ToDirect2D(),
                },
                stops);
        }
    }
}
