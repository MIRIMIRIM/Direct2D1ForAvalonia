using System;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using Vortice.DXGI;

namespace Avalonia.Direct2D1
{
    internal sealed class HwndRenderTarget : SwapChainRenderTarget
    {
        private readonly INativePlatformHandleSurface _window;

        public HwndRenderTarget(INativePlatformHandleSurface window)
        {
            _window = window;
        }

        protected override IDXGISwapChain1 CreateSwapChain(IDXGIFactory2 dxgiFactory, SwapChainDescription1 swapChainDesc)
        {
            return dxgiFactory.CreateSwapChainForHwnd(Direct2D1Platform.DxgiDevice, _window.Handle, swapChainDesc);
        }

        protected override Vortice.Mathematics.Size GetWindowDpi()
        {
            try
            {
                var dpi = GetDpiForWindow(_window.Handle);
                if (dpi != 0)
                    return new Vortice.Mathematics.Size(dpi, dpi);
            }
            catch (EntryPointNotFoundException)
            {
                // Older Windows versions fall back to 96 DPI below.
            }

            return new Vortice.Mathematics.Size(96, 96);
        }

        protected override Vortice.Mathematics.SizeI GetWindowSize()
        {
            GetClientRect(_window.Handle, out var rect);
            return new Vortice.Mathematics.SizeI(rect.Right - rect.Left, rect.Bottom - rect.Top);
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }
}
