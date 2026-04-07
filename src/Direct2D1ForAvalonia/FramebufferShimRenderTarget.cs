#nullable enable

using System;
using MIR.Direct2D1ForAvalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using Vortice.WIC;
using PixelFormat = Avalonia.Platform.PixelFormat;

namespace MIR.Direct2D1ForAvalonia
{
    internal class FramebufferShimRenderTarget : IRenderTarget
    {
        private IFramebufferRenderTarget? _target;

        public FramebufferShimRenderTarget(IFramebufferPlatformSurface surface)
        {
            _target = surface.CreateFramebufferRenderTarget();
        }

        public RenderTargetProperties Properties => new()
        {
            RetainsPreviousFrameContents = _target?.RetainsFrameContents == true,
            IsSuitableForDirectRendering = true
        };

        public PlatformRenderTargetState PlatformRenderTargetState =>
            _target?.State ?? PlatformRenderTargetState.Disposed;

        public void Dispose()
        {
            _target?.Dispose();
            _target = null;
        }

        public IDrawingContextImpl CreateDrawingContext(IRenderTarget.RenderTargetSceneInfo sceneInfo, out RenderTargetDrawingContextProperties properties)
        {
            if (_target == null)
                throw new ObjectDisposedException(nameof(FramebufferShimRenderTarget));

            var locked = _target.Lock(sceneInfo, out var lockProperties);
            properties = new RenderTargetDrawingContextProperties
            {
                PreviousFrameIsRetained = lockProperties.PreviousFrameIsRetained
            };

            if (locked.Format == PixelFormat.Rgb565)
            {
                locked.Dispose();
                throw new ArgumentException("Unsupported pixel format: " + locked.Format);
            }

            return new FramebufferShim(locked).CreateDrawingContext(useScaledDrawing: false);
        }

        private sealed class FramebufferShim : WicRenderTargetBitmapImpl
        {
            private readonly ILockedFramebuffer _target;

            public FramebufferShim(ILockedFramebuffer target)
                : base(target.Size, target.Dpi, target.Format, target.AlphaFormat)
            {
                _target = target;
            }

            public override IDrawingContextImpl CreateDrawingContext(bool useScaledDrawing)
            {
                return CreateDrawingContext(useScaledDrawing, () =>
                {
                    using (var l = WicImpl.Lock(BitmapLockFlags.Read))
                    {
                        var sourceStride = (int)l.Stride;
                        var destinationStride = _target.RowBytes;
                        var rowBytes = Math.Min(sourceStride, destinationStride);

                        unsafe
                        {
                            var sourceBase = (byte*)l.Data.DataPointer;
                            var destinationBase = (byte*)_target.Address;

                            var height = _target.Size.Height;
                            if (rowBytes == sourceStride && rowBytes == destinationStride)
                            {
                                var totalBytes = checked((long)rowBytes * height);
                                Buffer.MemoryCopy(sourceBase, destinationBase, totalBytes, totalBytes);
                            }
                            else
                            {
                                for (var y = 0; y < height; y++)
                                {
                                    var sourceRow = sourceBase + (y * sourceStride);
                                    var destinationRow = destinationBase + (y * destinationStride);
                                    Buffer.MemoryCopy(sourceRow, destinationRow, rowBytes, rowBytes);
                                }
                            }
                        }
                    }

                    Dispose();
                    _target.Dispose();
                });
            }
        }
    }
}