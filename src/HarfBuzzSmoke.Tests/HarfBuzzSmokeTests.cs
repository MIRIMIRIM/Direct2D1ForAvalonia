using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Win32;
using MIR.Direct2D1ForAvalonia;

namespace HarfBuzzSmoke.Tests;

[TestClass]
public sealed class HarfBuzzSmokeTests
{
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public void Direct2D1_WithHarfBuzz_RendersText()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Direct2D1 + HarfBuzz smoke exercises Windows-only rendering.");
        }

        AppBuilder.Configure<Application>()
            .UseWin32()
            .UseDirect2D1()
            .UseHarfBuzz()
            .SetupWithoutStarting();

        using var target = new RenderTargetBitmap(new PixelSize(220, 72), new Vector(96, 96));
        using (var context = target.CreateDrawingContext(false))
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 220, 72));

            var text = new FormattedText(
                "Direct2D1 + HarfBuzz",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                Typeface.Default,
                22,
                Brushes.Black);

            context.DrawText(text, new Point(8, 20));
        }

        using var framebuffer = new TestFramebuffer(target.PixelSize);
        target.CopyPixels(framebuffer);

        Assert.IsTrue(
            framebuffer.ContainsNonWhitePixels(minPixels: 24),
            "Expected Direct2D1 + HarfBuzz text rendering to produce visible glyph pixels.");
    }

    private sealed class TestFramebuffer : ILockedFramebuffer
    {
        private readonly byte[] _pixels;
        private readonly GCHandle _handle;
        private bool _disposed;

        public TestFramebuffer(PixelSize size)
        {
            Size = size;
            RowBytes = size.Width * 4;
            Dpi = new Vector(96, 96);
            Format = PixelFormats.Bgra8888;
            AlphaFormat = AlphaFormat.Opaque;
            _pixels = new byte[RowBytes * size.Height];
            _handle = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
        }

        public IntPtr Address => _handle.AddrOfPinnedObject();

        public PixelSize Size { get; }

        public int RowBytes { get; }

        public Vector Dpi { get; }

        public PixelFormat Format { get; }

        public AlphaFormat AlphaFormat { get; }

        public nint Key => 0;

        public bool ContainsNonWhitePixels(int minPixels)
        {
            var matches = 0;
            for (var i = 0; i < _pixels.Length; i += 4)
            {
                var b = _pixels[i];
                var g = _pixels[i + 1];
                var r = _pixels[i + 2];
                var a = _pixels[i + 3];

                if (a > 0 && (r < 245 || g < 245 || b < 245))
                {
                    matches++;
                    if (matches >= minPixels)
                        return true;
                }
            }

            return false;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _handle.Free();
        }
    }
}
