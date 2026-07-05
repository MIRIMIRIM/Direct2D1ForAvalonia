using Avalonia.Media;

namespace MIR.Direct2D1ForAvalonia.Media
{
    internal static class BrushTransform
    {
        public static Matrix Apply(IBrush brush, Rect destinationRect, Matrix baseTransform)
        {
            if (brush.Transform is not { } transform)
                return baseTransform;

            var transformOrigin = brush.TransformOrigin.ToPixels(destinationRect);
            var offset = Matrix.CreateTranslation(transformOrigin);

            return -offset * transform.Value * offset * baseTransform;
        }
    }
}
