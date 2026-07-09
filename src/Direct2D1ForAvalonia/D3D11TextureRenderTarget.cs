#nullable enable

using System;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Win32.DirectX;
using MIR.Direct2D1ForAvalonia.Diagnostics;
using MIR.Direct2D1ForAvalonia.Media;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using RenderTargetProperties = Avalonia.Platform.RenderTargetProperties;

namespace MIR.Direct2D1ForAvalonia
{
    internal sealed class D3D11TextureRenderTarget : IRenderTarget, ILayerFactory
    {
        private const int MaxCachedTextureTargets = 4;

        private readonly IDirect3D11TextureRenderTarget? _target;
        private readonly IDirect3D11TextureRenderTarget2? _target2;
        private readonly ID2D1DeviceContext _deviceContext;
        private readonly TextureTargetCacheEntry?[] _textureTargetCache = new TextureTargetCacheEntry?[MaxCachedTextureTargets];
        private Vector _lastDpi = new Vector(96, 96);
        private bool _disposed;
        private int _frameId;
        private long _cacheClock;

        public D3D11TextureRenderTarget(
            IDirect3D11TexturePlatformSurface surface,
            IPlatformGraphicsContext graphicsContext)
        {
            // Prefer the 12.1 IDirect3D11TexturePlatformSurface2 path, which passes
            // scene info (including transparency level) to BeginDraw. Fall back to the
            // original interface for surfaces that don't implement the *2 variant.
            if (surface is IDirect3D11TexturePlatformSurface2 surface2)
            {
                _target2 = surface2.CreateRenderTarget(
                    graphicsContext,
                    Direct2D1Platform.Direct3D11Device.NativePointer);
            }
            else
            {
                _target = surface.CreateRenderTarget(
                    graphicsContext,
                    Direct2D1Platform.Direct3D11Device.NativePointer);
            }

            _deviceContext = Direct2D1Platform.Direct2D1Device.CreateDeviceContext(DeviceContextOptions.None);
        }

        public RenderTargetProperties Properties => new()
        {
            IsSuitableForDirectRendering = true,
            RetainsPreviousFrameContents = false
        };

        public PlatformRenderTargetState PlatformRenderTargetState =>
            _disposed ? PlatformRenderTargetState.Disposed
                : (_target2?.State ?? _target!.State);

        public IDrawingContextImpl CreateDrawingContext(
            IRenderTarget.RenderTargetSceneInfo sceneInfo,
            out RenderTargetDrawingContextProperties properties)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(D3D11TextureRenderTarget));
            }

            properties = default;

            var frameId = ++_frameId;
            var session = _target2?.BeginDraw(sceneInfo) ?? _target!.BeginDraw();
            TextureTargetCacheEntry? targetEntry = null;

            try
            {
                if (Direct2D1Diagnostics.ShouldLogFrame(frameId, important: frameId == 1))
                {
                    Direct2D1Diagnostics.Write(
                        $"d3d11-texture-frame begin id={frameId} " +
                        $"targetState={PlatformRenderTargetState} sceneSize={FormatSize(sceneInfo.Size)} sceneScaling={sceneInfo.Scaling:0.###} " +
                        $"transparency={sceneInfo.TransparencyLevel} " +
                        $"sessionSize={FormatSize(session.Size)} sessionScaling={session.Scaling:0.###} sessionOffset={FormatPoint(session.Offset)} " +
                        $"texture=0x{session.D3D11Texture2D.ToInt64():X}");
                }

                if (session.Size.Width <= 0 || session.Size.Height <= 0)
                {
                    throw new InvalidOperationException("The D3D11 texture render target has an invalid size.");
                }

                if (session.Scaling <= 0)
                {
                    throw new InvalidOperationException("The D3D11 texture render target has an invalid scaling factor.");
                }

                var dpi = new Vector(session.Scaling * 96.0, session.Scaling * 96.0);
                _lastDpi = dpi;

                _deviceContext.SetDpi((float)dpi.X, (float)dpi.Y);

                var texturePointer = session.D3D11Texture2D;
                if (texturePointer == IntPtr.Zero)
                {
                    throw new InvalidOperationException("The D3D11 texture render target returned a null texture.");
                }

                var cacheHit = TryGetTextureTarget(texturePointer, session.Size, dpi, out targetEntry);
                if (!cacheHit)
                    targetEntry = CreateAndCacheTextureTarget(texturePointer, session.Size, dpi);

                _deviceContext.Target = targetEntry.Bitmap;

                if (Direct2D1Diagnostics.ShouldLogFrame(frameId, important: frameId == 1))
                {
                    Direct2D1Diagnostics.Write(
                        $"d3d11-texture-frame target id={frameId} " +
                        $"cache={(cacheHit ? "hit" : "miss")} " +
                        $"d2dBitmapPixel={FormatSize(targetEntry.Bitmap.PixelSize)} d2dBitmapDpi={FormatSize(targetEntry.Bitmap.Dpi)} " +
                        $"contextPixel={FormatSize(_deviceContext.PixelSize)} contextDpi={FormatSize(_deviceContext.Dpi)}");
                }

                var targetTransform = session.Offset == default
                    ? (Matrix?)null
                    : Matrix.CreateTranslation(
                        session.Offset.X / session.Scaling,
                        session.Offset.Y / session.Scaling);

                return new DrawingContextImpl(
                    this,
                    _deviceContext,
                    useScaledDrawing: false,
                    finishedCallback: () => FlushDirect3DDevice(frameId),
                    targetTransform: targetTransform,
                    cleanupCallback: () =>
                    {
                        if (Direct2D1Diagnostics.ShouldLogFrame(frameId, important: frameId == 1))
                        {
                            Direct2D1Diagnostics.Write($"d3d11-texture-frame cleanup id={frameId}");
                        }

                        try
                        {
                            _deviceContext.Target = null;
                        }
                        finally
                        {
                            session.Dispose();
                        }
                    });
            }
            catch
            {
                _deviceContext.Target = null;
                session.Dispose();
                throw;
            }
        }

        public IDrawingContextLayerImpl CreateLayer(Size size)
        {
            var dpi = _lastDpi;
            var pixelSize = PixelSize.FromSizeWithDpi(size, dpi);
            if (Direct2D1Diagnostics.IsEnabled)
            {
                Direct2D1Diagnostics.Write(
                    $"d3d11-texture-create-layer requestedDip={FormatSize(size)} pixel={FormatSize(pixelSize)} dpi={FormatSize(dpi)}");
            }

            return new WicRenderTargetBitmapImpl(pixelSize, dpi);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _deviceContext.Target = null;
            ClearTextureTargetCache();
            _deviceContext.Dispose();
            if (_target2 is not null)
                _target2.Dispose();
            else
                _target?.Dispose();
        }

        private bool TryGetTextureTarget(IntPtr texturePointer, PixelSize pixelSize, Vector dpi, out TextureTargetCacheEntry entry)
        {
            for (var i = 0; i < _textureTargetCache.Length; i++)
            {
                var candidate = _textureTargetCache[i];
                if (candidate is null
                    || candidate.TexturePointer != texturePointer
                    || candidate.PixelSize != pixelSize
                    || !AreSameDpi(candidate.Dpi, dpi))
                {
                    continue;
                }

                candidate.LastUsed = ++_cacheClock;
                entry = candidate;
                return true;
            }

            entry = null!;
            return false;
        }

        private TextureTargetCacheEntry CreateAndCacheTextureTarget(IntPtr texturePointer, PixelSize pixelSize, Vector dpi)
        {
            Marshal.AddRef(texturePointer);
            var ownsRawTextureReference = true;
            TextureTargetCacheEntry? entry = null;
            ID3D11Texture2D? texture = null;
            IDXGISurface? dxgiSurface = null;
            ID2D1Bitmap1? bitmap = null;

            try
            {
                texture = new ID3D11Texture2D(texturePointer);
                ownsRawTextureReference = false;
                dxgiSurface = texture.QueryInterface<IDXGISurface>();
                bitmap = _deviceContext.CreateBitmapFromDxgiSurface(
                    dxgiSurface,
                    new BitmapProperties1(
                        new Vortice.DCommon.PixelFormat
                        {
                            AlphaMode = Vortice.DCommon.AlphaMode.Premultiplied,
                            Format = Format.B8G8R8A8_UNorm
                        },
                        (float)dpi.X,
                        (float)dpi.Y,
                        BitmapOptions.Target | BitmapOptions.CannotDraw));

                entry = new TextureTargetCacheEntry(texturePointer, pixelSize, dpi, texture, dxgiSurface, bitmap)
                {
                    LastUsed = ++_cacheClock
                };
                texture = null;
                dxgiSurface = null;
                bitmap = null;
                StoreTextureTarget(entry);
                return entry;
            }
            catch
            {
                entry?.Dispose();
                throw;
            }
            finally
            {
                if (ownsRawTextureReference)
                    Marshal.Release(texturePointer);

                bitmap?.Dispose();
                dxgiSurface?.Dispose();
                texture?.Dispose();
            }
        }

        private void StoreTextureTarget(TextureTargetCacheEntry entry)
        {
            var slot = 0;
            var oldest = long.MaxValue;

            for (var i = 0; i < _textureTargetCache.Length; i++)
            {
                var candidate = _textureTargetCache[i];
                if (candidate is null)
                {
                    slot = i;
                    oldest = long.MaxValue;
                    break;
                }

                if (candidate.LastUsed < oldest)
                {
                    slot = i;
                    oldest = candidate.LastUsed;
                }
            }

            if (_textureTargetCache[slot] is { } oldEntry)
            {
                if (Direct2D1Diagnostics.IsEnabled)
                {
                    Direct2D1Diagnostics.Write(
                        $"d3d11-texture-cache evict texture=0x{oldEntry.TexturePointer.ToInt64():X} pixel={FormatSize(oldEntry.PixelSize)} dpi={FormatSize(oldEntry.Dpi)}");
                }

                _textureTargetCache[slot]?.Dispose();
            }

            _textureTargetCache[slot] = entry;
        }

        private void ClearTextureTargetCache()
        {
            for (var i = 0; i < _textureTargetCache.Length; i++)
            {
                if (_textureTargetCache[i] is { } entry && Direct2D1Diagnostics.IsEnabled)
                {
                    Direct2D1Diagnostics.Write(
                        $"d3d11-texture-cache dispose texture=0x{entry.TexturePointer.ToInt64():X} pixel={FormatSize(entry.PixelSize)} dpi={FormatSize(entry.Dpi)}");
                }

                _textureTargetCache[i]?.Dispose();
                _textureTargetCache[i] = null;
            }
        }

        private static void FlushDirect3DDevice(int frameId)
        {
            if (Direct2D1Diagnostics.ShouldLogFrame(frameId, important: frameId == 1))
            {
                Direct2D1Diagnostics.Write($"d3d11-texture-flush id={frameId}");
            }

            Direct2D1Platform.Direct3D11ImmediateContext.Flush();
        }

        private static string FormatSize(PixelSize size)
            => $"{size.Width}x{size.Height}";

        private static string FormatSize(Size size)
            => $"{size.Width:0.###}x{size.Height:0.###}";

        private static string FormatSize(Vector size)
            => $"{size.X:0.###}x{size.Y:0.###}";

        private static string FormatSize(Vortice.Mathematics.SizeI size)
            => $"{size.Width}x{size.Height}";

        private static string FormatSize(Vortice.Mathematics.Size size)
            => $"{size.Width:0.###}x{size.Height:0.###}";

        private static string FormatSize(System.Drawing.SizeF size)
            => $"{size.Width:0.###}x{size.Height:0.###}";

        private static string FormatPoint(PixelPoint point)
            => $"{point.X},{point.Y}";

        private static bool AreSameDpi(Vector left, Vector right)
            => Math.Abs(left.X - right.X) < 0.0001 && Math.Abs(left.Y - right.Y) < 0.0001;

        private sealed class TextureTargetCacheEntry : IDisposable
        {
            public TextureTargetCacheEntry(
                IntPtr texturePointer,
                PixelSize pixelSize,
                Vector dpi,
                ID3D11Texture2D texture,
                IDXGISurface dxgiSurface,
                ID2D1Bitmap1 bitmap)
            {
                TexturePointer = texturePointer;
                PixelSize = pixelSize;
                Dpi = dpi;
                Texture = texture;
                DxgiSurface = dxgiSurface;
                Bitmap = bitmap;
            }

            public IntPtr TexturePointer { get; }

            public PixelSize PixelSize { get; }

            public Vector Dpi { get; }

            public ID3D11Texture2D Texture { get; }

            public IDXGISurface DxgiSurface { get; }

            public ID2D1Bitmap1 Bitmap { get; }

            public long LastUsed { get; set; }

            public void Dispose()
            {
                Bitmap.Dispose();
                DxgiSurface.Dispose();
                Texture.Dispose();
            }
        }
    }
}
