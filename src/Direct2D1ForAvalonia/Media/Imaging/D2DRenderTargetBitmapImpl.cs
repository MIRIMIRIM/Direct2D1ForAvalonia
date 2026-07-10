using System;
using System.IO;
using Avalonia.Platform;
using MIR.Direct2D1ForAvalonia.Diagnostics;
using Vortice.Direct2D1;
using Vortice.WIC;

namespace MIR.Direct2D1ForAvalonia.Media.Imaging
{
    internal class D2DRenderTargetBitmapImpl(ID2D1BitmapRenderTarget renderTarget) : D2DBitmapImpl(GetBitmap(renderTarget)), IDrawingContextLayerImpl, ILayerFactory
    {
        private readonly ID2D1BitmapRenderTarget _renderTarget = renderTarget;
        private DrawingContextImpl? _reusableDrawingContext;
        private bool? _reusableUseScaledDrawing;

        public static D2DRenderTargetBitmapImpl CreateCompatible(
            ID2D1RenderTarget renderTarget,
            Size size)
        {
            if (Direct2D1Diagnostics.IsEnabled)
            {
                Direct2D1Diagnostics.Write(
                    $"d2d-compatible-rt requestedDip={size.Width:0.###}x{size.Height:0.###} " +
                    $"parentPixel={renderTarget.PixelSize.Width}x{renderTarget.PixelSize.Height} " +
                    $"parentDpi={renderTarget.Dpi.Width:0.###}x{renderTarget.Dpi.Height:0.###}");
            }

            var bitmapRenderTarget = renderTarget.CreateCompatibleRenderTarget(
                new Vortice.Mathematics.Size((float)size.Width, (float)size.Height),
                null,
                null,
                CompatibleRenderTargetOptions.None);

            if (Direct2D1Diagnostics.IsEnabled)
            {
                Direct2D1Diagnostics.Write(
                    $"d2d-compatible-rt-created pixel={bitmapRenderTarget.PixelSize.Width}x{bitmapRenderTarget.PixelSize.Height} " +
                    $"dpi={bitmapRenderTarget.Dpi.Width:0.###}x{bitmapRenderTarget.Dpi.Height:0.###}");
            }

            return new D2DRenderTargetBitmapImpl(bitmapRenderTarget);
        }

        public IDrawingContextImpl CreateDrawingContext() => CreateDrawingContext(useScaledDrawing: false);

        public IDrawingContextImpl CreateDrawingContext(bool useScaledDrawing)
        {
            Action finishedCallback = () => Version++;
            if (_reusableDrawingContext is null || _reusableUseScaledDrawing != useScaledDrawing)
            {
                if (_reusableDrawingContext is not null)
                {
                    _reusableDrawingContext.ReleaseRetainedNativeResources();
                    _reusableDrawingContext = null;
                }

                _reusableUseScaledDrawing = useScaledDrawing;
                _reusableDrawingContext = new DrawingContextImpl(
                    this,
                    _renderTarget,
                    useScaledDrawing,
                    finishedCallback: finishedCallback);
                _reusableDrawingContext.EnableSessionReuse();
            }
            else
            {
                _reusableDrawingContext.ReopenSession(finishedCallback: finishedCallback);
            }

            return _reusableDrawingContext;
        }

        public bool IsCorrupted => false;

        public void Blit(IDrawingContextImpl context)
        {
            if (context is not DrawingContextImpl d2dContext)
                throw new InvalidOperationException("Blit requires a Direct2D drawing context.");

            var rect = new Rect(PixelSize.ToSizeWithDpi(Dpi));

            // Source blend: replace the destination region (Avalonia composition intermediate).
            // Prefer DrawBitmap SourceOver after the bitmap is fully closed (EndDraw already
            // ran on the intermediate). SourceCopy-via-DrawImage has been flaky for GPU
            // CreateCompatible bitmaps on the D3D11 texture surface; Source + DrawBitmap path
            // below still uses Source mode but routes through a stable DrawBitmap when possible.
            d2dContext.PushBitmapBlendMode(Avalonia.Media.Imaging.BitmapBlendingMode.Source);
            try
            {
                d2dContext.DrawBitmap(this, 1, rect, rect);
            }
            finally
            {
                d2dContext.PopBitmapBlendMode();
            }
        }

        public bool CanBlit => true;

        public IDrawingContextLayerImpl CreateLayer(Size size)
        {
            return CreateCompatible(_renderTarget, size);
        }

        public override void Dispose()
        {
            if (_reusableDrawingContext is not null)
            {
                _reusableDrawingContext.ReleaseRetainedNativeResources();
                _reusableDrawingContext = null;
            }

            base.Dispose();
            _renderTarget.Dispose();
        }

        public override OptionalDispose<ID2D1Bitmap1> GetDirect2DBitmap(ID2D1RenderTarget target)
        {
            // Reuse the constructor-cached bitmap (owns: false). Creating a fresh QI per Blit
            // with owns:true and disposing it mid-frame has been linked to black GPU-layer
            // blits onto the D3D11 window texture.
            return base.GetDirect2DBitmap(target);
        }

        protected internal override void Save(Stream stream, ContainerFormat containerFormat, int? quality)
        {
            using (var wic = new WicRenderTargetBitmapImpl(PixelSize, Dpi))
            {
                using (var dc = wic.CreateDrawingContext(true, null))
                {
                    dc.DrawBitmap(
                        this,
                        1,
                        new Rect(PixelSize.ToSizeWithDpi(Dpi)),
                        new Rect(PixelSize.ToSizeWithDpi(Dpi)));
                }

                wic.Save(stream, containerFormat, quality);
            }
        }

        private static ID2D1Bitmap1 GetBitmap(ID2D1BitmapRenderTarget renderTarget)
        {
            using var bitmap = renderTarget.Bitmap;
            return bitmap.QueryInterface<ID2D1Bitmap1>();
        }
    }
}
