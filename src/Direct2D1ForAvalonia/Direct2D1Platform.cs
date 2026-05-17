using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using Avalonia;
using MIR.DirectWriteForAvalonia;
using MIR.Direct2D1ForAvalonia.Media;
using MIR.Direct2D1ForAvalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using Avalonia.Win32.DirectX;
using GlyphRun = Avalonia.Media.GlyphRun;
using Vortice.Direct3D11;
using Vortice.Direct2D1;
using Vortice.WIC;
using Vortice.DXGI;
using PixelFormat = Avalonia.Platform.PixelFormat;
using BitmapInterpolationMode = Avalonia.Media.Imaging.BitmapInterpolationMode;

namespace MIR.Direct2D1ForAvalonia
{
    [SupportedOSPlatform("windows")]
    public static class Direct2DApplicationExtensions
    {
        public static AppBuilder UseDirect2D1(this AppBuilder builder)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("MIR.Direct2D1ForAvalonia is only supported on Windows.");

            builder.UseRenderingSubsystem(Direct2D1Platform.Initialize, "Direct2D1");
            return builder;
        }
    }
}

namespace MIR.Direct2D1ForAvalonia
{
    [SupportedOSPlatform("windows")]
    internal class Direct2D1Platform : IPlatformRenderInterface
    {
        private static readonly Direct2D1Platform s_instance = new Direct2D1Platform();

        public static ID3D11Device Direct3D11Device { get; private set; } = null!;

        public static ID3D11DeviceContext Direct3D11ImmediateContext { get; private set; } = null!;

        public static ID2D1Factory1 Direct2D1Factory { get; private set; } = null!;

        public static ID2D1Device Direct2D1Device { get; private set; } = null!;
        public static IWICImagingFactory ImagingFactory { get; private set; } = null!;

        public static IDXGIDevice1 DxgiDevice { get; private set; } = null!;

        private static readonly object s_initLock = new object();
        private static bool s_initialized = false;

        internal static void InitializeDirect2D()
        {
            SharpGenRuntimeInitializer.Initialize();

            lock (s_initLock)
            {
                if (s_initialized)
                {
                    return;
                }
#if DEBUG
                if (Debugger.IsAttached)
                {
                    try
                    {
                        Direct2D1Factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(
                            Vortice.Direct2D1.FactoryType.MultiThreaded,
                            debugLevel: DebugLevel.Error);
                    }
                    catch
                    {
                        // ignore, retry below without the debug layer
                    }
                }
#endif
                if (Direct2D1Factory == null)
                {
                    Direct2D1Factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(
                        Vortice.Direct2D1.FactoryType.MultiThreaded,
                        debugLevel: DebugLevel.None);
                }
                ImagingFactory = new IWICImagingFactory();

                var featureLevels = new[]
                {
                    Vortice.Direct3D.FeatureLevel.Level_11_1,
                    Vortice.Direct3D.FeatureLevel.Level_11_0,
                    Vortice.Direct3D.FeatureLevel.Level_10_1,
                    Vortice.Direct3D.FeatureLevel.Level_10_0,
                    Vortice.Direct3D.FeatureLevel.Level_9_3,
                    Vortice.Direct3D.FeatureLevel.Level_9_2,
                    Vortice.Direct3D.FeatureLevel.Level_9_1,
                };

                Direct3D11Device = D3D11.D3D11CreateDevice(
                    Vortice.Direct3D.DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                    featureLevels);

                Direct3D11ImmediateContext = Direct3D11Device.ImmediateContext;

                DxgiDevice = Direct3D11Device.QueryInterface<IDXGIDevice1>();

                Direct2D1Device = Direct2D1Factory.CreateDevice(DxgiDevice);

                s_initialized = true;
            }
        }

        public static void Initialize()
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("MIR.Direct2D1ForAvalonia is only supported on Windows.");

            InitializeDirect2D();
            DirectWritePlatform.InitializeFontManager();
            AvaloniaLocator.CurrentMutable
                .Bind<IPlatformRenderInterface>().ToConstant(s_instance);
        }

        private IRenderTarget CreateRenderTarget(
            IEnumerable<IPlatformRenderSurface> surfaces,
            IPlatformGraphicsContext? graphicsContext)
        {
            IDirect3D11TexturePlatformSurface? textureSurface = null;
            IExternalDirect2DRenderTargetSurface? externalSurface = null;
            INativePlatformHandleSurface? hwndSurface = null;
            IFramebufferPlatformSurface? framebufferSurface = null;

            foreach (var s in surfaces)
            {
                if (s is IDirect3D11TexturePlatformSurface texture)
                {
                    textureSurface = texture;
                    continue;
                }

                if (s is IExternalDirect2DRenderTargetSurface external)
                {
                    externalSurface = external;
                    continue;
                }

                if (s is INativePlatformHandleSurface nativeWindow)
                {
                    if (nativeWindow.HandleDescriptor == "HWND")
                    {
                        hwndSurface = nativeWindow;
                    }

                    continue;
                }

                if (s is IFramebufferPlatformSurface fb)
                {
                    framebufferSurface = fb;
                }
            }

            if (textureSurface != null)
            {
                return new D3D11TextureRenderTarget(
                    textureSurface,
                    graphicsContext ?? Direct2DGraphicsContext.Instance);
            }

            if (externalSurface != null)
            {
                return new ExternalRenderTarget(externalSurface);
            }

            if (hwndSurface != null)
            {
                return new HwndRenderTarget(hwndSurface);
            }

            if (framebufferSurface != null)
            {
                return new FramebufferShimRenderTarget(framebufferSurface);
            }

            throw new NotSupportedException("Don't know how to create a Direct2D1 renderer from any of provided surfaces");
        }

        public IRenderTargetBitmapImpl CreateRenderTargetBitmap(PixelSize size, Vector dpi)
        {
            return new WicRenderTargetBitmapImpl(size, dpi);
        }

        public IWriteableBitmapImpl CreateWriteableBitmap(PixelSize size, Vector dpi, PixelFormat format, AlphaFormat alphaFormat)
        {
            return new WriteableWicBitmapImpl(size, dpi, format, alphaFormat);
        }

        public IGeometryImpl CreateEllipseGeometry(Rect rect) => new EllipseGeometryImpl(rect);
        public IGeometryImpl CreateLineGeometry(Point p1, Point p2) => new LineGeometryImpl(p1, p2);
        public IGeometryImpl CreateRectangleGeometry(Rect rect) => new RectangleGeometryImpl(rect);
        public IStreamGeometryImpl CreateStreamGeometry() => new StreamGeometryImpl();
        public IGeometryImpl CreateGeometryGroup(FillRule fillRule, IReadOnlyList<IGeometryImpl> children) => new GeometryGroupImpl(fillRule, children);
        public IGeometryImpl CreateCombinedGeometry(GeometryCombineMode combineMode, IGeometryImpl g1, IGeometryImpl g2) => new CombinedGeometryImpl(combineMode, g1, g2);
        public IGlyphRunImpl CreateGlyphRun(GlyphTypeface glyphTypeface, double fontRenderingEmSize, IReadOnlyList<GlyphInfo> glyphInfos, Point baselineOrigin)
        {
            return new GlyphRunImpl(glyphTypeface, fontRenderingEmSize, glyphInfos, baselineOrigin);
        }

        class D2DApi : IPlatformRenderInterfaceContext
        {
            private readonly Direct2D1Platform _platform;
            private readonly IPlatformGraphicsContext? _graphicsContext;

            public D2DApi(Direct2D1Platform platform, IPlatformGraphicsContext? graphicsContext)
            {
                _platform = platform;
                _graphicsContext = graphicsContext;
            }
            public object? TryGetFeature(Type featureType) => null;

            public IDrawingContextLayerImpl CreateOffscreenRenderTarget(PixelSize pixelSize, Vector scaling, bool enableTextAntialiasing)
            {
                var dpi = scaling * 96.0;
                return new WicRenderTargetBitmapImpl(pixelSize, dpi);
            }

            public void Dispose()
            {
            }

            public IRenderTarget CreateRenderTarget(IEnumerable<IPlatformRenderSurface> surfaces) =>
                _platform.CreateRenderTarget(surfaces, _graphicsContext);
            public bool IsLost => false;
            public IReadOnlyDictionary<Type, object> PublicFeatures { get; } = new Dictionary<Type, object>();
            public PixelSize? MaxOffscreenRenderTargetPixelSize => null;
        }

        public IPlatformRenderInterfaceContext CreateBackendContext(IPlatformGraphicsContext? graphicsContext) =>
            new D2DApi(this, graphicsContext);

        public IGeometryImpl BuildGlyphRunGeometry(GlyphRun glyphRun)
        {
            if (glyphRun.GlyphTypeface.PlatformTypeface is not DirectWriteGlyphTypeface glyphTypeface)
            {
                throw new InvalidOperationException("PlatformImpl can't be null.");
            }

            var pathGeometry = Direct2D1Factory.CreatePathGeometry();

            using (var sink = pathGeometry.Open())
            {
                var glyphInfos = glyphRun.GlyphInfos;
                var glyphs = new ushort[glyphInfos.Count];

                for (int i = 0; i < glyphInfos.Count; i++)
                {
                    glyphs[i] = glyphInfos[i].GlyphIndex;
                }

                glyphTypeface.FontFace.GetGlyphRunOutline((float)glyphRun.FontRenderingEmSize, glyphs, null, null, false, !glyphRun.IsLeftToRight, sink);

                sink.Close();
            }

            var (baselineOriginX, baselineOriginY) = glyphRun.BaselineOrigin;

            var transformedGeometry = Direct2D1Factory.CreateTransformedGeometry(
                pathGeometry,
                new System.Numerics.Matrix3x2(1.0f, 0.0f, 0.0f, 1.0f, (float)baselineOriginX, (float)baselineOriginY));

            return new TransformedGeometryWrapper(transformedGeometry);
        }

        private class TransformedGeometryWrapper : GeometryImpl
        {
            public TransformedGeometryWrapper(ID2D1TransformedGeometry geometry) : base(geometry)
            {

            }
        }

        /// <inheritdoc />
        public IBitmapImpl LoadBitmap(string fileName)
        {
            return new WicBitmapImpl(fileName);
        }

        /// <inheritdoc />
        public IBitmapImpl LoadBitmap(Stream stream)
        {
            return new WicBitmapImpl(stream);
        }

        public IWriteableBitmapImpl LoadWriteableBitmapToWidth(Stream stream, int width,
            BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality)
        {
            return new WriteableWicBitmapImpl(stream, width, true, interpolationMode);
        }

        public IWriteableBitmapImpl LoadWriteableBitmapToHeight(Stream stream, int height,
            BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality)
        {
            return new WriteableWicBitmapImpl(stream, height, false, interpolationMode);
        }

        public IWriteableBitmapImpl LoadWriteableBitmap(string fileName)
        {
            return new WriteableWicBitmapImpl(fileName);
        }

        public IWriteableBitmapImpl LoadWriteableBitmap(Stream stream)
        {
            return new WriteableWicBitmapImpl(stream);
        }

        /// <inheritdoc />
        public IBitmapImpl LoadBitmapToWidth(Stream stream, int width, BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality)
        {
            return new WicBitmapImpl(stream, width, true, interpolationMode);
        }

        /// <inheritdoc />
        public IBitmapImpl LoadBitmapToHeight(Stream stream, int height, BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality)
        {
            return new WicBitmapImpl(stream, height, false, interpolationMode);
        }

        /// <inheritdoc />
        public IBitmapImpl ResizeBitmap(IBitmapImpl bitmapImpl, PixelSize destinationSize, BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality)
        {
            // https://github.com/sharpdx/SharpDX/issues/959 blocks implementation.
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        public IBitmapImpl LoadBitmap(PixelFormat format, AlphaFormat alphaFormat, IntPtr data, PixelSize size, Vector dpi, int stride)
        {
            return new WicBitmapImpl(format, alphaFormat, data, size, dpi, stride);
        }

        public bool SupportsIndividualRoundRects => false;

        public AlphaFormat DefaultAlphaFormat => AlphaFormat.Premul;

        public PixelFormat DefaultPixelFormat => PixelFormat.Bgra8888;
        public bool IsSupportedBitmapPixelFormat(PixelFormat format) =>
            format == PixelFormats.Bgra8888 
            || format == PixelFormats.Rgba8888;

        public bool SupportsRegions => false;
        public IPlatformRenderInterfaceRegion CreateRegion() => throw new NotSupportedException();

        private sealed class Direct2DGraphicsContext : IPlatformGraphicsContext
        {
            public static readonly Direct2DGraphicsContext Instance = new();

            private Direct2DGraphicsContext()
            {
            }

            public bool IsLost => false;

            public IDisposable EnsureCurrent() => Disposable.Empty;

            public object? TryGetFeature(Type featureType) => null;

            public void Dispose()
            {
            }
        }

        private sealed class Disposable : IDisposable
        {
            public static readonly Disposable Empty = new();

            private Disposable()
            {
            }

            public void Dispose()
            {
            }
        }
    }
}
