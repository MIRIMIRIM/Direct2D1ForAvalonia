using System;
using System.IO;
using Avalonia.Platform;
using Vortice.Direct2D1;

namespace MIR.Direct2D1ForAvalonia.Media.Imaging
{
    internal class D2DRenderTargetBitmapImpl(ID2D1BitmapRenderTarget renderTarget) : D2DBitmapImpl(GetBitmap(renderTarget)), IDrawingContextLayerImpl, ILayerFactory
    {
        private readonly ID2D1BitmapRenderTarget _renderTarget = renderTarget;

        public static D2DRenderTargetBitmapImpl CreateCompatible(
            ID2D1RenderTarget renderTarget,
            Size size)
        {
            var bitmapRenderTarget = renderTarget.CreateCompatibleRenderTarget(
                new Vortice.Mathematics.Size((float)size.Width, (float)size.Height),
                null,
                null,
                CompatibleRenderTargetOptions.None);
            ;
            return new D2DRenderTargetBitmapImpl(bitmapRenderTarget);
        }

        public IDrawingContextImpl CreateDrawingContext() => CreateDrawingContext(useScaledDrawing: false);

        public IDrawingContextImpl CreateDrawingContext(bool useScaledDrawing)
        {
            return new DrawingContextImpl( this, _renderTarget, useScaledDrawing, 
                null, () => Version++);
        }

        public bool IsCorrupted => false;

        public void Blit(IDrawingContextImpl context) => throw new NotSupportedException();

        public bool CanBlit => false;

        public IDrawingContextLayerImpl CreateLayer(Size size)
        {
            return CreateCompatible(_renderTarget, size);
        }

        public override void Dispose()
        {
            base.Dispose();
            _renderTarget.Dispose();
        }

        public override OptionalDispose<ID2D1Bitmap1> GetDirect2DBitmap(ID2D1RenderTarget target)
        {
            return new OptionalDispose<ID2D1Bitmap1>(GetBitmap(_renderTarget), true);
        }

        public override void Save(Stream stream, int? quality = null)
        {
            using (var wic = new WicRenderTargetBitmapImpl(PixelSize, Dpi))
            {
                using (var dc = wic.CreateDrawingContext(true, null))
                {
                    dc.DrawBitmap(
                        this,
                        1,
                        new Rect(PixelSize.ToSizeWithDpi(Dpi.X)),
                        new Rect(PixelSize.ToSizeWithDpi(Dpi.X)));
                }

                wic.Save(stream);
            }
        }

        private static ID2D1Bitmap1 GetBitmap(ID2D1BitmapRenderTarget renderTarget)
        {
            using var bitmap = renderTarget.Bitmap;
            return bitmap.QueryInterface<ID2D1Bitmap1>();
        }
    }
}
