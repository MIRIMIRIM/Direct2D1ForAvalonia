using Avalonia.Media;
using Vortice.Direct2D1;

namespace MIR.Direct2D1ForAvalonia.Media
{
    internal class LinearGradientBrushImpl : BrushImpl
    {
        public LinearGradientBrushImpl(
            ILinearGradientBrush brush,
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

            var startPoint = brush.StartPoint.ToPixels(destinationRect);
            var endPoint = brush.EndPoint.ToPixels(destinationRect);

            using (var stops = target.CreateGradientStopCollection(
                gradientStops,
                brush.SpreadMethod.ToDirect2D()))
            {
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
}
