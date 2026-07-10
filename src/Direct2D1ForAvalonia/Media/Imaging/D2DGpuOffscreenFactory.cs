using System;
using Avalonia;
using Avalonia.Platform;
using Vortice.Direct2D1;
using Vortice.DXGI;

namespace MIR.Direct2D1ForAvalonia.Media.Imaging;

/// <summary>
/// Creates GPU-backed offscreen render targets for
/// <see cref="IPlatformRenderInterfaceContext.CreateOffscreenRenderTarget"/>.
/// Uses a process-wide seed device context on the shared D2D device, then
/// <see cref="D2DRenderTargetBitmapImpl.CreateCompatible"/> so layers share the device
/// resource cache with window surfaces.
/// </summary>
internal static class D2DGpuOffscreenFactory
{
    private static readonly object s_lock = new();
    private static ID2D1DeviceContext? s_seedContext;
    private static ID2D1Bitmap1? s_seedBitmap;

    /// <summary>
    /// Creates a GPU-compatible intermediate. Falls back to <see cref="WicRenderTargetBitmapImpl"/>
    /// only when Direct2D has not been initialised.
    /// </summary>
    public static IDrawingContextLayerImpl Create(PixelSize pixelSize, Vector dpi)
    {
        if (Direct2D1Platform.Direct2D1Device is null)
            return new WicRenderTargetBitmapImpl(pixelSize, dpi);

        var seed = EnsureSeed(dpi);
        // CreateCompatible expects DIPs matching the parent DPI.
        var dipSize = pixelSize.ToSizeWithDpi(dpi);
        return D2DRenderTargetBitmapImpl.CreateCompatible(seed, dipSize);
    }

    private static ID2D1DeviceContext EnsureSeed(Vector dpi)
    {
        lock (s_lock)
        {
            if (s_seedContext is not null)
            {
                // Keep seed DPI aligned with the request so CreateCompatible sizing is correct.
                s_seedContext.SetDpi((float)dpi.X, (float)dpi.Y);
                return s_seedContext;
            }

            var dc = Direct2D1Platform.Direct2D1Device.CreateDeviceContext(DeviceContextOptions.None);
            try
            {
                dc.SetDpi((float)dpi.X, (float)dpi.Y);
                var props = new BitmapProperties1(
                    new Vortice.DCommon.PixelFormat(
                        Format.B8G8R8A8_UNorm,
                        Vortice.DCommon.AlphaMode.Premultiplied),
                    (float)dpi.X,
                    (float)dpi.Y,
                    BitmapOptions.Target);

                var bitmap = dc.CreateBitmap(new Vortice.Mathematics.SizeI(1, 1), IntPtr.Zero, 0, props);
                dc.Target = bitmap;
                s_seedBitmap = bitmap;
                s_seedContext = dc;
                return dc;
            }
            catch
            {
                dc.Dispose();
                throw;
            }
        }
    }
}
