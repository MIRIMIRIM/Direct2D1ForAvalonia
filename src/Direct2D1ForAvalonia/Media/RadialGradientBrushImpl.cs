using System.Numerics;
using Avalonia.Media;
using Vortice.Direct2D1;

namespace MIR.Direct2D1ForAvalonia.Media
{
    internal class RadialGradientBrushImpl : BrushImpl
    {
        public RadialGradientBrushImpl(
            IRadialGradientBrush brush,
            ID2D1RenderTarget target,
            Rect destinationRect)
        {
            if (brush.GradientStops.Count == 0)
            {
                return;
            }

            var gradientStops = new Vortice.Direct2D1.GradientStop[brush.GradientStops.Count];
            var index = 0;
            foreach (var stop in brush.GradientStops)
            {
                gradientStops[index++] = new Vortice.Direct2D1.GradientStop
                {
                    Color = stop.Color.ToDirect2D(),
                    Position = (float)stop.Offset
                };
            }

            var centerPoint = brush.Center.ToPixels(destinationRect);
            var gradientOrigin = brush.GradientOrigin.ToPixels(destinationRect) - centerPoint;
            
            var radiusX = brush.RadiusX.ToValue(destinationRect.Width);
            var radiusY = brush.RadiusY.ToValue(destinationRect.Height);

            using (var stops = target.CreateGradientStopCollection(
                gradientStops,
                brush.SpreadMethod.ToDirect2D()))
            {
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
                        Transform = Matrix3x2.Identity,
                    },
                    stops);
            }
        }
    }
}
