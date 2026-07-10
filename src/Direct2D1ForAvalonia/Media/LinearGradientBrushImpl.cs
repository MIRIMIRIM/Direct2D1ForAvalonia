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

            var startPoint = brush.StartPoint.ToPixels(destinationRect);
            var endPoint = brush.EndPoint.ToPixels(destinationRect);

            // The stop collection depends only on stops+spread (not the destination rect), so it is
            // cached across frames. It is owned by the cache and must not be disposed here — the D2D
            // brush AddRefs it internally.
            var stops = deviceResources.GetOrCreateGradientStops(target, brush.GradientStops, brush.SpreadMethod);

            PlatformBrush = target.CreateLinearGradientBrush(
                new LinearGradientBrushProperties
                {
                    StartPoint = startPoint.ToVortice(),
                    EndPoint = endPoint.ToVortice()
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
