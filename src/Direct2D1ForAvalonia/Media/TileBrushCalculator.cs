using Avalonia.Media;

namespace MIR.Direct2D1ForAvalonia.Media
{
    internal class TileBrushCalculator
    {
        private readonly Size _imageSize;
        private readonly Rect _drawRect;

        public TileBrushCalculator(ITileBrush brush, Size contentSize, Size targetSize)
            : this(
                brush.TileMode,
                brush.Stretch,
                brush.AlignmentX,
                brush.AlignmentY,
                brush.SourceRect,
                brush.DestinationRect,
                contentSize,
                targetSize)
        {
        }

        public TileBrushCalculator(
            TileMode tileMode,
            Stretch stretch,
            AlignmentX alignmentX,
            AlignmentY alignmentY,
            RelativeRect sourceRect,
            RelativeRect destinationRect,
            Size contentSize,
            Size targetSize)
        {
            _imageSize = contentSize;

            SourceRect = sourceRect.ToPixels(_imageSize);
            DestinationRect = destinationRect.ToPixels(targetSize);

            var scale = stretch.CalculateScaling(DestinationRect.Size, SourceRect.Size);
            var translate = CalculateTranslate(alignmentX, alignmentY, SourceRect, DestinationRect, scale);

            IntermediateSize = tileMode == TileMode.None ? targetSize : DestinationRect.Size;
            IntermediateTransform = CalculateIntermediateTransform(
                tileMode,
                SourceRect,
                DestinationRect,
                scale,
                translate,
                out _drawRect);
        }

        public Rect DestinationRect { get; }
        public Rect IntermediateClip => _drawRect;
        public Size IntermediateSize { get; }
        public Matrix IntermediateTransform { get; }
        public Rect SourceRect { get; }

        public bool NeedsIntermediate
        {
            get
            {
                if (IntermediateTransform != Matrix.Identity)
                    return true;
                if (SourceRect.Position != default)
                    return true;
                if (SourceRect.Size.AspectRatio == _imageSize.AspectRatio)
                    return false;
                if (SourceRect.Width != _imageSize.Width || SourceRect.Height != _imageSize.Height)
                    return true;
                return false;
            }
        }

        public static Vector CalculateTranslate(
            AlignmentX alignmentX,
            AlignmentY alignmentY,
            Rect sourceRect,
            Rect destinationRect,
            Vector scale) => CalculateTranslate(alignmentX, alignmentY, sourceRect.Size * scale, destinationRect.Size);

        public static Vector CalculateTranslate(
            AlignmentX alignmentX,
            AlignmentY alignmentY,
            Size sourceSize,
            Size destinationSize)
        {
            var x = 0.0;
            var y = 0.0;

            switch (alignmentX)
            {
                case AlignmentX.Center:
                    x += (destinationSize.Width - sourceSize.Width) / 2;
                    break;
                case AlignmentX.Right:
                    x += destinationSize.Width - sourceSize.Width;
                    break;
            }

            switch (alignmentY)
            {
                case AlignmentY.Center:
                    y += (destinationSize.Height - sourceSize.Height) / 2;
                    break;
                case AlignmentY.Bottom:
                    y += destinationSize.Height - sourceSize.Height;
                    break;
            }

            return new Vector(x, y);
        }

        public static Matrix CalculateIntermediateTransform(
            TileMode tileMode,
            Rect sourceRect,
            Rect destinationRect,
            Vector scale,
            Vector translate,
            out Rect drawRect)
        {
            var transform = Matrix.CreateTranslation(-sourceRect.Position) *
                            Matrix.CreateScale(scale) *
                            Matrix.CreateTranslation(translate);
            Rect dr;

            if (tileMode == TileMode.None)
            {
                dr = destinationRect;
                transform *= Matrix.CreateTranslation(destinationRect.Position);
            }
            else
            {
                dr = new Rect(destinationRect.Size);
            }

            drawRect = dr;
            return transform;
        }
    }
}