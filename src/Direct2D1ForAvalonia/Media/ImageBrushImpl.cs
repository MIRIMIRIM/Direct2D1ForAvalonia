using Avalonia.Media;
using Vortice.Direct2D1;

namespace MIR.Direct2D1ForAvalonia.Media
{
    internal sealed class ImageBrushImpl : BrushImpl
    {
        private readonly OptionalDispose<ID2D1Bitmap1> _bitmap;
        private readonly BitmapImpl? _ownedBitmap;

        public ImageBrushImpl(
            ITileBrush brush,
            ID2D1RenderTarget target,
            BitmapImpl bitmap,
            Rect destinationRect,
            bool ownsBitmap = false)
        {
            _ownedBitmap = ownsBitmap ? bitmap : null;
            var dpi = new Vector(target.Dpi.Width, target.Dpi.Height);
            var calc = new TileBrushCalculator(brush, bitmap.PixelSize.ToSizeWithDpi(dpi), destinationRect.Size);

            Vector brushOffset = default;
            if (brush.DestinationRect.Unit == RelativeUnit.Relative)
                brushOffset = new Vector(destinationRect.X, destinationRect.Y);
            
            if (!calc.NeedsIntermediate)
            {
                _bitmap = bitmap.GetDirect2DBitmap(target);
                PlatformBrush = target.CreateBitmapBrush(
                    _bitmap.Value,
                    GetBitmapBrushProperties(brush),
                    GetBrushProperties(brush, calc.DestinationRect, destinationRect, brushOffset));
            }
            else
            {
                PlatformBrush = CreateIntermediateBrush(
                    target,
                    bitmap,
                    calc,
                    GetBitmapBrushProperties(brush),
                    GetBrushProperties(brush, calc.DestinationRect, destinationRect, brushOffset));
            }
        }

        public override void Dispose()
        {
            _bitmap.Dispose();
            base.Dispose();
            _ownedBitmap?.Dispose();
        }

        private static BitmapBrushProperties GetBitmapBrushProperties(ITileBrush brush)
        {
            var tileMode = brush.TileMode;

            return new BitmapBrushProperties
            {
                ExtendModeX = GetExtendModeX(tileMode),
                ExtendModeY = GetExtendModeY(tileMode),
            };
        }

        private static BrushProperties GetBrushProperties(
            ITileBrush brush,
            Rect tileRect,
            Rect targetBox,
            Vector offset)
        {
            var tileTransform =
                brush.TileMode != TileMode.None ?
                Matrix.CreateTranslation(tileRect.X, tileRect.Y) :
                Matrix.Identity;

            if (offset != default)
                tileTransform = Matrix.CreateTranslation(offset);

            tileTransform = BrushTransform.Apply(brush, targetBox, tileTransform);

            return new BrushProperties
            {
                Opacity = (float)brush.Opacity,
                Transform = tileTransform.ToDirect2D(),
            };
        }

        private static ExtendMode GetExtendModeX(TileMode tileMode)
        {
            return (tileMode & TileMode.FlipX) != 0 ? ExtendMode.Mirror : ExtendMode.Wrap;
        }

        private static ExtendMode GetExtendModeY(TileMode tileMode)
        {
            return (tileMode & TileMode.FlipY) != 0 ? ExtendMode.Mirror : ExtendMode.Wrap;
        }

        private static ID2D1BitmapBrush CreateIntermediateBrush(
            ID2D1RenderTarget target,
            BitmapImpl bitmap,
            TileBrushCalculator calc,
            BitmapBrushProperties bitmapBrushProperties,
            BrushProperties brushProperties)
        {
            using var intermediate = target.CreateCompatibleRenderTarget(
                calc.IntermediateSize.ToSharpDX(),
                null,
                null,
                CompatibleRenderTargetOptions.None);

            using (var context = new DrawingContextImpl(
                       layerFactory: null,
                       renderTarget: intermediate,
                       useScaledDrawing: true))
            {
                var dpi = new Vector(target.Dpi.Width, target.Dpi.Height);
                var rect = new Rect(bitmap.PixelSize.ToSizeWithDpi(dpi));

                context.Clear(Colors.Transparent);
                context.PushClip(calc.IntermediateClip);
                context.Transform = calc.IntermediateTransform;
                context.DrawBitmap(bitmap, 1, rect, rect);
                context.PopClip();
            }

            using var intermediateBitmap = intermediate.Bitmap;
            // CreateBitmapBrush retains its own COM reference to the bitmap. The getter RCW and
            // compatible target can therefore be released after the brush has been created.
            return target.CreateBitmapBrush(
                intermediateBitmap,
                bitmapBrushProperties,
                brushProperties);
        }
    }
}
