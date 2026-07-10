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
        /// <summary>Eligible to return to <see cref="D2DCompatibleLayerPool"/> on Dispose.</summary>
        private bool _poolEnabled;
        /// <summary>Currently sitting in the pool (Dispose is a no-op until rented again).</summary>
        private bool _inPool;
        /// <summary>
        /// After pool rent (or first attach), clear to transparent on the next BeginDraw so
        /// reused GPU content cannot leak through unpainted regions — matches Skia/Avalonia
        /// "fresh layer is transparent" expectations.
        /// </summary>
        private bool _clearOnNextSession;
        private D2DCompatibleLayerPool.LayerKey _poolKey;
        private bool _nativeDisposed;

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

        /// <summary>
        /// Composition-friendly factory: rents a pooled compatible RT when possible.
        /// </summary>
        public static D2DRenderTargetBitmapImpl CreateCompatiblePooled(
            ID2D1RenderTarget renderTarget,
            Size size)
            => D2DCompatibleLayerPool.Rent(renderTarget, size);

        internal void AttachToPool(D2DCompatibleLayerPool.LayerKey key)
        {
            _poolEnabled = true;
            _inPool = false;
            _poolKey = key;
            // CreateCompatible leaves content undefined; Skia intermediates start clear/transparent.
            _clearOnNextSession = true;
        }

        /// <summary>
        /// Called when a pooled layer is rented again — invalidate bitmap consumers and bump
        /// <see cref="BitmapImpl.Version"/> so blit/upload caches do not reuse stale content.
        /// </summary>
        internal void PrepareForPoolReuse()
        {
            _inPool = false;
            Version++;
            _clearOnNextSession = true;
        }

        /// <summary>
        /// Actually releases native resources (used by the pool when discarding).
        /// </summary>
        internal void ForceDisposeNative()
        {
            _poolEnabled = false;
            _inPool = false;
            DisposeCore();
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

            // Pooled layers retain prior GPU pixels until cleared. Avalonia/Skia treat a newly
            // obtained intermediate as transparent; without this, unpainted corners/holes would
            // show stale content after rent (Source blit would then copy garbage).
            if (_clearOnNextSession)
            {
                _reusableDrawingContext.ClearLayerToTransparent();
                _clearOnNextSession = false;
            }

            return _reusableDrawingContext;
        }

        public bool IsCorrupted => false;

        public void Blit(IDrawingContextImpl context)
        {
            if (context is not DrawingContextImpl d2dContext)
                throw new InvalidOperationException("Blit requires a Direct2D drawing context.");

            var rect = new Rect(PixelSize.ToSizeWithDpi(Dpi));

            // Source replace of the destination region (Avalonia composition intermediate).
            // Dedicated path: nearest-neighbour, no blend-mode stack, full-target Clear skip-clip.
            d2dContext.BlitCompositionLayer(this, rect);
        }

        public bool CanBlit => true;

        public IDrawingContextLayerImpl CreateLayer(Size size)
        {
            return CreateCompatiblePooled(_renderTarget, size);
        }

        public override void Dispose()
        {
            if (_nativeDisposed)
                return;

            // Already sitting in the pool — Avalonia double-dispose is a no-op.
            if (_inPool)
                return;

            // Composition disposes layers every frame; return to the pool instead of freeing GPU RT.
            if (_poolEnabled)
            {
                _inPool = true;
                // Keep the reusable DrawingContextImpl for the next rent (session reuse).
                D2DCompatibleLayerPool.Return(this, _poolKey);
                return;
            }

            DisposeCore();
        }

        private void DisposeCore()
        {
            if (_nativeDisposed)
                return;
            _nativeDisposed = true;

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
