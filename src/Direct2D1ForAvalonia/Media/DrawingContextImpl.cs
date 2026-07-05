using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Reflection;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Utilities;
using Avalonia.Media.Imaging;
using MIR.Direct2D1ForAvalonia.Diagnostics;
using BitmapInterpolationMode = Avalonia.Media.Imaging.BitmapInterpolationMode;
using Vortice.Direct2D1;
using Vortice.DXGI;
using SharpGen.Runtime;
using Vortice;
using Vortice.DCommon;
using AVector = Avalonia.Vector;

namespace MIR.Direct2D1ForAvalonia.Media
{
    /// <summary>
    /// Draws using Direct2D1.
    /// </summary>
    internal class DrawingContextImpl : IDrawingContextImpl
    {
        private readonly ILayerFactory? _layerFactory;
        private readonly ID2D1RenderTarget _renderTarget;
        private readonly ID2D1DeviceContext _deviceContext;
        private readonly bool _ownsDeviceContext;
        private readonly IDXGISwapChain1? _swapChain;
        private readonly Action? _finishedCallback;
        private readonly Action? _cleanupCallback;
        private readonly string _diagnosticTargetName;

        private readonly Stack<RenderOptions> _renderOptionsStack = new Stack<RenderOptions>();
        private readonly Stack<TextOptions> _textOptionsStack = new Stack<TextOptions>();
        private readonly Stack<ID2D1Layer?> _layers = new Stack<ID2D1Layer?>();
        private readonly Stack<BrushImpl?> _opacityMaskBrushes = new Stack<BrushImpl?>();
        private readonly Stack<ID2D1Layer> _layerPool = new Stack<ID2D1Layer>();
        private readonly Stack<BitmapBlendingMode> _bitmapBlendModeStack = new Stack<BitmapBlendingMode>();
        // For each PushClip, records the kind of clip pushed so PopClip can undo the right one.
        // When the entry is a non-null geometry, the clip was a rounded-rect layer whose
        // geometric mask must outlive the layer and be disposed alongside it on PopClip.
        private readonly Stack<ID2D1Geometry?> _clipKindStack = new Stack<ID2D1Geometry?>();
        private static readonly PropertyInfo? s_imageBrushBitmapProperty = typeof(IImageBrushSource).GetProperty(
            "Bitmap",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly PropertyInfo? s_imageBrushBitmapItemProperty = s_imageBrushBitmapProperty?.PropertyType.GetProperty(
            "Item",
            BindingFlags.Instance | BindingFlags.Public);
        private RenderOptions _renderOptions;
        private TextOptions _textOptions;
        private readonly Matrix? _postTransform;
        private readonly Matrix? _targetTransform;
        private Matrix _transform = Matrix.Identity;

        /// <summary>
        /// Initializes a new instance of the <see cref="DrawingContextImpl"/> class.
        /// </summary>
        /// <param name="renderTarget">The render target to draw to.</param>
        /// <param name="layerFactory">
        /// An object to use to create layers. May be null, in which case a
        /// <see cref="WicRenderTargetBitmapImpl"/> will created when a new layer is requested.
        /// </param>
        /// <param name="useScaledDrawing">Whether to scale drawings according to the DPI of <paramref name="renderTarget"/>.</param>
        /// <param name="swapChain">An optional swap chain associated with this drawing context.</param>
        /// <param name="finishedCallback">An optional delegate to be called when context is disposed.</param>
        /// <param name="targetTransform">An optional transform required by the target surface.</param>
        /// <param name="cleanupCallback">An optional delegate that is always called when context is disposed.</param>
        public DrawingContextImpl(
            ILayerFactory? layerFactory,
            ID2D1RenderTarget renderTarget,
            bool useScaledDrawing,
            IDXGISwapChain1? swapChain = null,
            Action? finishedCallback = null,
            Matrix? targetTransform = null,
            Action? cleanupCallback = null)
        {
            _layerFactory = layerFactory;
            _renderTarget = renderTarget;
            _swapChain = swapChain;
            _finishedCallback = finishedCallback;
            _targetTransform = targetTransform;
            _cleanupCallback = cleanupCallback;
            _diagnosticTargetName = _renderTarget.GetType().Name;

            if (_renderTarget is ID2D1DeviceContext deviceContext)
            {
                _deviceContext = deviceContext;
                _ownsDeviceContext = false;
            }
            else
            {
                _deviceContext = _renderTarget.QueryInterface<ID2D1DeviceContext>();
                _ownsDeviceContext = true;
            }

            if (!useScaledDrawing)
            {
                var scaling = _renderTarget.Dpi.Width / 96;
                if (!AreClose(1, scaling))
                    _postTransform = Matrix.CreateScale(1 / scaling, 1 / scaling);
            }

            if (Direct2D1Diagnostics.IsEnabled)
            {
                Direct2D1Diagnostics.Write(
                    $"drawing-context-create target={_renderTarget.GetType().Name} " +
                    $"pixel={_renderTarget.PixelSize.Width}x{_renderTarget.PixelSize.Height} " +
                    $"dpi={_renderTarget.Dpi.Width:0.###}x{_renderTarget.Dpi.Height:0.###} " +
                    $"useScaledDrawing={useScaledDrawing} ownsDeviceContext={_ownsDeviceContext} " +
                    $"postTransform={_postTransform?.ToString() ?? "null"} targetTransform={_targetTransform?.ToString() ?? "null"}");
            }

            _deviceContext.BeginDraw();

            if (_targetTransform.HasValue)
            {
                ApplyTransform();
            }
        }

        /// <summary>
        /// Gets the current transform of the drawing context.
        /// </summary>
        public Matrix Transform
        {
            get { return _transform; }
            set
            {
                _transform = value;
                ApplyTransform();
            }
        }

        public RenderOptions RenderOptions
        {
            get => _renderOptions;
            set
            {
                _renderOptions = value;
                ApplyRenderOptions(value);
            }
        }

        public TextOptions TextOptions
        {
            get => _textOptions;
            private set => _textOptions = value;
        }

        /// <inheritdoc/>
        public void Clear(Color color)
        {
            _deviceContext.Clear(color.ToDirect2D());
        }

        /// <summary>
        /// Ends a draw operation.
        /// </summary>
        public void Dispose()
        {
            foreach (var layer in _layerPool)
            {
                layer.Dispose();
            }

            // Clean up any rounded-rect clip geometries left behind by a PushClip without a
            // matching PopClip (e.g. render aborted mid-frame).
            foreach (var clipGeometry in _clipKindStack)
            {
                clipGeometry?.Dispose();
            }
            _clipKindStack.Clear();

            try
            {
                if (Direct2D1Diagnostics.IsEnabled)
                {
                    Direct2D1Diagnostics.Write(
                        $"drawing-context-dispose begin target={_diagnosticTargetName} hasSwapChain={_swapChain != null} hasFinishedCallback={_finishedCallback != null} hasCleanupCallback={_cleanupCallback != null}");
                }

                _deviceContext.EndDraw().CheckError();
                if (Direct2D1Diagnostics.IsEnabled)
                    Direct2D1Diagnostics.Write($"drawing-context-dispose enddraw target={_diagnosticTargetName}");

                if (_swapChain != null)
                {
                    _swapChain.Present(1, PresentFlags.None).CheckError();
                    if (Direct2D1Diagnostics.IsEnabled)
                        Direct2D1Diagnostics.Write($"drawing-context-dispose present target={_diagnosticTargetName}");
                }

                _finishedCallback?.Invoke();
                if (Direct2D1Diagnostics.IsEnabled)
                    Direct2D1Diagnostics.Write($"drawing-context-dispose finished target={_diagnosticTargetName}");
            }
            catch (SharpGenException ex) when ((uint)ex.HResult == 0x8899000C) // D2DERR_RECREATE_TARGET
            {
                if (Direct2D1Diagnostics.IsEnabled)
                    Direct2D1Diagnostics.Write($"drawing-context-dispose recreate-target target={_diagnosticTargetName} hresult=0x{ex.HResult:X8}");
                throw new RenderTargetCorruptedException(ex);
            }
            catch (Exception ex)
            {
                if (Direct2D1Diagnostics.IsEnabled)
                    Direct2D1Diagnostics.Write($"drawing-context-dispose error target={_diagnosticTargetName} {ex.GetType().Name}: {ex.Message}");
                throw;
            }
            finally
            {
                try
                {
                    _cleanupCallback?.Invoke();
                    if (Direct2D1Diagnostics.IsEnabled)
                        Direct2D1Diagnostics.Write($"drawing-context-dispose cleanup target={_diagnosticTargetName}");
                }
                finally
                {
                    // Drain any layers/masks left pushed by a render that aborted before matching
                    // pops (e.g. an exception between PushOpacity/OpacityMask and its pop). Layers
                    // here are still live RCWs (the pool only holds returned ones), and mask brushes
                    // own their own D2D brush, so dispose both before tearing down the device.
                    foreach (var layer in _layers)
                    {
                        layer?.Dispose();
                    }
                    _layers.Clear();

                    foreach (var maskBrush in _opacityMaskBrushes)
                    {
                        maskBrush?.Dispose();
                    }
                    _opacityMaskBrushes.Clear();

                    if (_ownsDeviceContext)
                    {
                        _deviceContext.Dispose();
                    }
                }
            }
        }

        private void ApplyTransform()
        {
            var transform = _transform;

            if (_postTransform.HasValue)
            {
                transform *= _postTransform.Value;
            }

            if (_targetTransform.HasValue)
            {
                transform *= _targetTransform.Value;
            }

            _deviceContext.Transform = transform.ToDirect2D();
        }

        /// <summary>
        /// Draws a bitmap image.
        /// </summary>
        /// <param name="source">The bitmap image.</param>
        /// <param name="opacity">The opacity to draw with.</param>
        /// <param name="sourceRect">The rect in the image to draw.</param>
        /// <param name="destRect">The rect in the output to draw to.</param>
        public void DrawBitmap(IBitmapImpl source, double opacity, Rect sourceRect, Rect destRect)
        {
            using (var d2d = ((BitmapImpl)source).GetDirect2DBitmap(_deviceContext))
            {
                var interpolationMode = GetInterpolationMode(RenderOptions.BitmapInterpolationMode);
                var compositeMode = GetCompositeMode(EffectiveBitmapBlendingMode);

                // Vortice 3.8.3 does not expose the ID2D1DeviceContext::DrawBitmap overload
                // that takes a composite mode. The default SourceOver path uses DrawBitmap
                // directly (fast); any other composite mode routes through DrawImage, which
                // does accept a composite mode. DrawImage draws at 1:1 with a target offset,
                // so a scaling world transform is applied when destRect differs from sourceRect.
                if (compositeMode == CompositeMode.SourceOver)
                {
                    _deviceContext.DrawBitmap(
                        d2d.Value,
                        destRect.ToVortice(),
                        (float)opacity,
                        interpolationMode,
                        sourceRect.ToVortice(),
                        null);
                }
                else
                {
                    var previousTransform = _deviceContext.Transform;
                    try
                    {
                        var scaleX = (float)(sourceRect.Width > 0 ? destRect.Width / sourceRect.Width : 1);
                        var scaleY = (float)(sourceRect.Height > 0 ? destRect.Height / sourceRect.Height : 1);
                        var drawTransform = Matrix3x2.CreateScale(scaleX, scaleY)
                            * Matrix3x2.CreateTranslation(new Vector2((float)destRect.X, (float)destRect.Y))
                            * previousTransform;
                        _deviceContext.Transform = drawTransform;

                        using var opacityEffect = CreateOpacityEffect(d2d.Value, (float)opacity);
                        using var opacityOutput = opacityEffect?.Output;
                        var image = opacityOutput ?? d2d.Value;

                        _deviceContext.DrawImage(
                            image,
                            null,
                            sourceRect.ToVortice(),
                            interpolationMode,
                            compositeMode);
                    }
                    finally
                    {
                        _deviceContext.Transform = previousTransform;
                    }
                }
            }
        }

        private static InterpolationMode GetInterpolationMode(BitmapInterpolationMode interpolationMode)
        {
            switch (interpolationMode)
            {
                case BitmapInterpolationMode.Unspecified:
                case BitmapInterpolationMode.LowQuality:
                    return InterpolationMode.Linear;
                case BitmapInterpolationMode.MediumQuality:
                    return InterpolationMode.MultiSampleLinear;
                case BitmapInterpolationMode.HighQuality:
                    return InterpolationMode.HighQualityCubic;
                case BitmapInterpolationMode.None:
                    return InterpolationMode.NearestNeighbor;
                default:
                    throw new ArgumentOutOfRangeException(nameof(interpolationMode), interpolationMode, null);
            }
        }

        public static CompositeMode GetCompositeMode(BitmapBlendingMode blendingMode)
        {
            switch (blendingMode)
            {
                case BitmapBlendingMode.SourceIn:
                    return CompositeMode.SourceIn;
                case BitmapBlendingMode.SourceOut:
                    return CompositeMode.SourceOut;
                case BitmapBlendingMode.Unspecified:
                case BitmapBlendingMode.SourceOver:
                    return CompositeMode.SourceOver;
                case BitmapBlendingMode.SourceAtop:
                    return CompositeMode.SourceAtop;
                case BitmapBlendingMode.DestinationIn:
                    return CompositeMode.DestinationIn;
                case BitmapBlendingMode.DestinationOut:
                    return CompositeMode.DestinationOut;
                case BitmapBlendingMode.DestinationOver:
                    return CompositeMode.DestinationOver;
                case BitmapBlendingMode.DestinationAtop:
                    return CompositeMode.DestinationAtop;
                case BitmapBlendingMode.Xor:
                    return CompositeMode.Xor;
                case BitmapBlendingMode.Plus:
                    return CompositeMode.Plus;
                default:
                    throw new ArgumentOutOfRangeException(nameof(blendingMode), blendingMode, null);
            }
        }

        /// <summary>
        /// Draws a bitmap image.
        /// </summary>
        /// <param name="source">The bitmap image.</param>
        /// <param name="opacityMask">The opacity mask to draw with.</param>
        /// <param name="opacityMaskRect">The destination rect for the opacity mask.</param>
        /// <param name="destRect">The rect in the output to draw to.</param>
        public void DrawBitmap(IBitmapImpl source, IBrush opacityMask, Rect opacityMaskRect, Rect destRect)
        {
            var interpolationMode = GetInterpolationMode(RenderOptions.BitmapInterpolationMode);

            using (var d2dSource = ((BitmapImpl)source).GetDirect2DBitmap(_deviceContext))
            using (var sourceBrush = _deviceContext.CreateBitmapBrush(d2dSource.Value, new BitmapBrushProperties1 { InterpolationMode = interpolationMode }))
            using (var d2dOpacityMask = CreateBrush(opacityMask, opacityMaskRect))
            using (var geometry = Direct2D1Platform.Direct2D1Factory.CreateRectangleGeometry(destRect.ToDirect2D()))
            {
                if (d2dOpacityMask.PlatformBrush != null)
                {
                    d2dOpacityMask.PlatformBrush.Transform = Matrix.CreateTranslation(opacityMaskRect.Position).ToDirect2D();
                }

                _deviceContext.FillGeometry(
                    geometry,
                    sourceBrush,
                    d2dOpacityMask.PlatformBrush);
            }
        }

        /// <summary>
        /// Draws a line.
        /// </summary>
        /// <param name="pen">The stroke pen.</param>
        /// <param name="p1">The first point of the line.</param>
        /// <param name="p2">The second point of the line.</param>
        public void DrawLine(IPen? pen, Point p1, Point p2)
        {
            if (pen?.Brush != null)
            {
                var bounds = new Rect(p1, p2);

                using (var d2dBrush = CreateBrush(pen.Brush, bounds))
                using (var d2dStroke = pen.ToDirect2DStrokeStyle(_deviceContext))
                {
                    if (d2dBrush.PlatformBrush != null)
                    {
                        _deviceContext.DrawLine(
                            p1.ToVortice(),
                            p2.ToVortice(),
                            d2dBrush.PlatformBrush,
                            (float)pen.Thickness,
                            d2dStroke);
                    }
                }
            }
        }

        /// <summary>
        /// Draws a geometry.
        /// </summary>
        /// <param name="brush">The fill brush.</param>
        /// <param name="pen">The stroke pen.</param>
        /// <param name="geometry">The geometry.</param>
        public void DrawGeometry(IBrush? brush, IPen? pen, IGeometryImpl geometry)
        {
            if (brush != null)
            {
                using (var d2dBrush = CreateBrush(brush, geometry.Bounds))
                {
                    if (d2dBrush.PlatformBrush != null)
                    {
                        var impl = (GeometryImpl)geometry;
                        _deviceContext.FillGeometry(impl.Geometry, d2dBrush.PlatformBrush);
                    }
                }
            }

            if (pen?.Brush != null)
            {
                using (var d2dBrush = CreateBrush(pen.Brush, geometry.GetRenderBounds(pen)))
                using (var d2dStroke = pen.ToDirect2DStrokeStyle(_deviceContext))
                {
                    if (d2dBrush.PlatformBrush != null)
                    {
                        var impl = (GeometryImpl)geometry;
                        _deviceContext.DrawGeometry(impl.Geometry, d2dBrush.PlatformBrush, (float)pen.Thickness, d2dStroke);
                    }
                }
            }
        }

        /// <inheritdoc />
        public void DrawRectangle(IBrush? brush, IPen? pen, RoundedRect rrect, BoxShadows boxShadow = default)
        {
            var rc = rrect.Rect.ToDirect2D();
            var rect = rrect.Rect;
            var radiusX = Math.Max(rrect.RadiiTopLeft.X,
                Math.Max(rrect.RadiiTopRight.X, Math.Max(rrect.RadiiBottomRight.X, rrect.RadiiBottomLeft.X)));
            var radiusY = Math.Max(rrect.RadiiTopLeft.Y,
                Math.Max(rrect.RadiiTopRight.Y, Math.Max(rrect.RadiiBottomRight.Y, rrect.RadiiBottomLeft.Y)));
            var isRounded = !IsZero(radiusX) || !IsZero(radiusY);
            using var roundedGeometry = isRounded ? CreateRoundedRectGeometry(rrect) : null;
            using var rectGeometry = isRounded ? null : Direct2D1Platform.Direct2D1Factory.CreateRectangleGeometry(rc);
            var shapeGeometry = roundedGeometry ?? (ID2D1Geometry)rectGeometry!;

            DrawBoxShadows(rrect, shapeGeometry, boxShadow, inset: false);

            if (brush != null)
            {
                using (var b = CreateBrush(brush, rect))
                {
                    if (b.PlatformBrush != null)
                    {
                        if (isRounded)
                        {
                            _deviceContext.FillGeometry(roundedGeometry!, b.PlatformBrush);
                        }
                        else
                        {
                            _deviceContext.FillRectangle(rc, b.PlatformBrush);
                        }
                    }
                }
            }

            DrawBoxShadows(rrect, shapeGeometry, boxShadow, inset: true);

            if (pen?.Brush != null)
            {
                using (var wrapper = CreateBrush(pen.Brush, rect))
                using (var d2dStroke = pen.ToDirect2DStrokeStyle(_deviceContext))
                {
                    if (wrapper.PlatformBrush != null)
                    {
                        if (isRounded)
                        {
                            _deviceContext.DrawGeometry(
                                roundedGeometry!,
                                wrapper.PlatformBrush,
                                (float)pen.Thickness,
                                d2dStroke);
                        }
                        else
                        {
                            _deviceContext.DrawRectangle(
                                rc,
                                wrapper.PlatformBrush,
                                (float)pen.Thickness,
                                d2dStroke);
                        }
                    }
                }
            }
        }

        public void DrawRegion(IBrush? brush, IPen? pen, IPlatformRenderInterfaceRegion region)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc />
        public void DrawEllipse(IBrush? brush, IPen? pen, Rect rect)
        {
            var rc = rect.ToDirect2D();

            if (brush != null)
            {
                using (var b = CreateBrush(brush, rect))
                {
                    if (b.PlatformBrush != null)
                    {
                        _deviceContext.FillEllipse(new Ellipse
                        {
                            Point = rect.Center.ToVortice(),
                            RadiusX = (float)(rect.Width / 2),
                            RadiusY = (float)(rect.Height / 2)
                        }, b.PlatformBrush);
                    }
                }
            }

            if (pen?.Brush != null)
            {
                using (var wrapper = CreateBrush(pen.Brush, rect))
                using (var d2dStroke = pen.ToDirect2DStrokeStyle(_deviceContext))
                {
                    if (wrapper.PlatformBrush != null)
                    {
                        _deviceContext.DrawEllipse(new Ellipse
                        {
                            Point = rect.Center.ToVortice(),
                            RadiusX = (float)(rect.Width / 2),
                            RadiusY = (float)(rect.Height / 2)
                        }, wrapper.PlatformBrush, (float)pen.Thickness, d2dStroke);
                    }
                }
            }
        }

        /// <summary>
        /// Draws a glyph run.
        /// </summary>
        /// <param name="foreground">The foreground.</param>
        /// <param name="glyphRun">The glyph run.</param>
        public void DrawGlyphRun(IBrush? foreground, IGlyphRunImpl glyphRun)
        {
            using (var brush = CreateBrush(foreground, glyphRun.Bounds))
            {
                var immutableGlyphRun = (GlyphRunImpl)glyphRun;

                var dxGlyphRun = immutableGlyphRun.GlyphRun;
                var previousTextAntialiasMode = _deviceContext.TextAntialiasMode;

                try
                {
                    _deviceContext.TextAntialiasMode = GetEffectiveTextAntialiasMode();
                    _renderTarget.DrawGlyphRun(glyphRun.BaselineOrigin.ToVortice(), dxGlyphRun,
                        brush.PlatformBrush, MeasuringMode.Natural);
                }
                finally
                {
                    _deviceContext.TextAntialiasMode = previousTextAntialiasMode;
                }
            }
        }

        public IDrawingContextLayerImpl CreateLayer(PixelSize pixelSize)
        {
            var dpi = new Avalonia.Vector(_deviceContext.Dpi.Width, _deviceContext.Dpi.Height);
            if (Direct2D1Diagnostics.IsEnabled)
            {
                var requestedDip = pixelSize.ToSizeWithDpi(dpi);
                Direct2D1Diagnostics.Write(
                    $"drawing-context-create-layer requestedPixel={pixelSize.Width}x{pixelSize.Height} " +
                    $"dpi={dpi.X:0.###}x{dpi.Y:0.###} requestedDip={requestedDip.Width:0.###}x{requestedDip.Height:0.###} " +
                    $"hasLayerFactory={_layerFactory != null}");
            }

            if (_layerFactory != null)
            {
                return _layerFactory.CreateLayer(pixelSize.ToSizeWithDpi(dpi));
            }
            else
            {
                var platform = AvaloniaLocator.Current.GetRequiredService<IPlatformRenderInterface>();

                return (IDrawingContextLayerImpl)platform.CreateRenderTargetBitmap(pixelSize, dpi);
            }
        }

        /// <summary>
        /// Pushes a clip rectange.
        /// </summary>
        /// <param name="clip">The clip rectangle.</param>
        /// <returns>A disposable used to undo the clip rectangle.</returns>
        public void PushClip(Rect clip)
        {
            _clipKindStack.Push(null);
            _deviceContext.PushAxisAlignedClip(clip.ToVortice(), AntialiasMode.PerPrimitive);
        }

        public void PushClip(RoundedRect clip)
        {
            var radiusX = Math.Max(clip.RadiiTopLeft.X,
                Math.Max(clip.RadiiTopRight.X, Math.Max(clip.RadiiBottomRight.X, clip.RadiiBottomLeft.X)));
            var radiusY = Math.Max(clip.RadiiTopLeft.Y,
                Math.Max(clip.RadiiTopRight.Y, Math.Max(clip.RadiiBottomRight.Y, clip.RadiiBottomLeft.Y)));

            if (radiusX <= 0 || radiusY <= 0)
            {
                // No rounding: a plain axis-aligned clip is cheaper and exactly equivalent.
                _clipKindStack.Push(null);
                _deviceContext.PushAxisAlignedClip(clip.Rect.ToDirect2D(), AntialiasMode.PerPrimitive);
                return;
            }

            // Direct2D has no native rounded-rect clip; push a layer whose geometric mask is
            // a rounded-rectangle geometry so the clip honors the corner radii. The geometry
            // must stay alive until PopClip, so it is tracked in the clip-kind stack.
            var geometry = CreateRoundedRectGeometry(clip);

            var parameters = new LayerParameters
            {
                ContentBounds = clip.Rect.ToDirect2D(),
                MaskTransform = Matrix3x2.Identity,
                Opacity = 1,
                GeometricMask = geometry,
                MaskAntialiasMode = AntialiasMode.PerPrimitive
            };

            try
            {
                PushDirect2DLayer(parameters);
            }
            catch
            {
                geometry.Dispose();
                throw;
            }

            _clipKindStack.Push(geometry);
        }

        private ID2D1Effect? CreateOpacityEffect(ID2D1Image source, float opacity)
        {
            if (opacity >= 1)
                return null;

            var effect = new ID2D1Effect(_deviceContext.CreateEffect(EffectGuids.Opacity));
            effect.SetInput(0, source, true);
            effect.SetValue((uint)OpacityProperties.Opacity, opacity);
            return effect;
        }

        private static ID2D1PathGeometry CreateRoundedRectGeometry(RoundedRect roundedRect)
        {
            var geometry = Direct2D1Platform.Direct2D1Factory.CreatePathGeometry();
            try
            {
                using (var sink = geometry.Open())
                {
                    AddRoundedRectFigure(sink, roundedRect);
                    sink.Close();
                }

                return geometry;
            }
            catch
            {
                geometry.Dispose();
                throw;
            }
        }

        private static void AddRoundedRectFigure(ID2D1GeometrySink sink, RoundedRect roundedRect)
        {
            var rect = roundedRect.Rect;
            var left = (float)rect.Left;
            var top = (float)rect.Top;
            var right = (float)rect.Right;
            var bottom = (float)rect.Bottom;

            var topLeft = roundedRect.RadiiTopLeft;
            var topRight = roundedRect.RadiiTopRight;
            var bottomRight = roundedRect.RadiiBottomRight;
            var bottomLeft = roundedRect.RadiiBottomLeft;
            NormalizeRadii(rect.Width, rect.Height, ref topLeft, ref topRight, ref bottomRight, ref bottomLeft);

            sink.BeginFigure(new Vector2(left + (float)topLeft.X, top), FigureBegin.Filled);

            sink.AddLine(new Vector2(right - (float)topRight.X, top));
            AddCornerArc(sink, topRight, new Vector2(right, top + (float)topRight.Y));

            sink.AddLine(new Vector2(right, bottom - (float)bottomRight.Y));
            AddCornerArc(sink, bottomRight, new Vector2(right - (float)bottomRight.X, bottom));

            sink.AddLine(new Vector2(left + (float)bottomLeft.X, bottom));
            AddCornerArc(sink, bottomLeft, new Vector2(left, bottom - (float)bottomLeft.Y));

            sink.AddLine(new Vector2(left, top + (float)topLeft.Y));
            AddCornerArc(sink, topLeft, new Vector2(left + (float)topLeft.X, top));

            sink.EndFigure(FigureEnd.Closed);
        }

        private static void NormalizeRadii(
            double width,
            double height,
            ref AVector topLeft,
            ref AVector topRight,
            ref AVector bottomRight,
            ref AVector bottomLeft)
        {
            topLeft = ClampRadius(topLeft);
            topRight = ClampRadius(topRight);
            bottomRight = ClampRadius(bottomRight);
            bottomLeft = ClampRadius(bottomLeft);

            var scale = 1.0;
            scale = Math.Min(scale, GetRadiusScale(width, topLeft.X + topRight.X));
            scale = Math.Min(scale, GetRadiusScale(width, bottomLeft.X + bottomRight.X));
            scale = Math.Min(scale, GetRadiusScale(height, topLeft.Y + bottomLeft.Y));
            scale = Math.Min(scale, GetRadiusScale(height, topRight.Y + bottomRight.Y));

            if (scale < 1.0)
            {
                topLeft *= scale;
                topRight *= scale;
                bottomRight *= scale;
                bottomLeft *= scale;
            }
        }

        private static AVector ClampRadius(AVector radius)
            => new(Math.Max(radius.X, 0), Math.Max(radius.Y, 0));

        private static double GetRadiusScale(double available, double required)
            => available <= 0 ? 0 : required > available && required > 0 ? available / required : 1.0;

        private static void AddCornerArc(ID2D1GeometrySink sink, AVector radius, Vector2 endPoint)
        {
            if (radius.X <= 0 || radius.Y <= 0)
            {
                sink.AddLine(endPoint);
                return;
            }

            sink.AddArc(new Vortice.Direct2D1.ArcSegment
            {
                Point = endPoint,
                Size = new Vortice.Mathematics.Size((float)radius.X, (float)radius.Y),
                RotationAngle = 0,
                SweepDirection = Vortice.Direct2D1.SweepDirection.Clockwise,
                ArcSize = ArcSize.Small
            });
        }

        private void DrawBoxShadows(
            RoundedRect roundedRect,
            ID2D1Geometry originalGeometry,
            BoxShadows boxShadows,
            bool inset)
        {
            for (var i = 0; i < boxShadows.Count; i++)
            {
                var shadow = boxShadows[i];
                if (shadow.Equals(default(BoxShadow)) ||
                    shadow.IsInset != inset ||
                    shadow.Color.A == 0)
                {
                    continue;
                }

                if (inset)
                    DrawInsetBoxShadow(roundedRect, originalGeometry, shadow);
                else
                    DrawOuterBoxShadow(roundedRect, originalGeometry, shadow);
            }
        }

        private void DrawOuterBoxShadow(
            RoundedRect roundedRect,
            ID2D1Geometry originalGeometry,
            BoxShadow shadow)
        {
            var caster = InflateAndOffsetRoundedRect(
                roundedRect,
                shadow.Spread,
                shadow.OffsetX,
                shadow.OffsetY);

            if (caster.Rect.Width <= 0 || caster.Rect.Height <= 0)
                return;

            using var casterGeometry = CreateRoundedRectGeometry(caster);
            var maskBounds = GetShadowMaskBounds(caster.Rect, shadow.Blur);
            using var boundsGeometry = Direct2D1Platform.Direct2D1Factory.CreateRectangleGeometry(maskBounds.ToDirect2D());
            using var clipGeometry = CombineGeometries(boundsGeometry, originalGeometry, CombineMode.Exclude);

            DrawShadowGeometry(casterGeometry, maskBounds, shadow, clipGeometry, maskBounds);
        }

        private void DrawInsetBoxShadow(
            RoundedRect roundedRect,
            ID2D1Geometry originalGeometry,
            BoxShadow shadow)
        {
            var outerRect = AreaCastingShadowInHole(
                roundedRect.Rect,
                shadow.Blur,
                shadow.Spread,
                shadow.OffsetX,
                shadow.OffsetY);
            outerRect = OffsetRect(outerRect, shadow.OffsetX, shadow.OffsetY);

            var inner = InflateAndOffsetRoundedRect(
                roundedRect,
                -shadow.Spread,
                shadow.OffsetX,
                shadow.OffsetY);

            using var outerGeometry = Direct2D1Platform.Direct2D1Factory.CreateRectangleGeometry(outerRect.ToDirect2D());
            using var casterGeometry = inner.Rect.Width > 0 && inner.Rect.Height > 0
                ? CombineInsetShadowGeometry(outerGeometry, inner)
                : Direct2D1Platform.Direct2D1Factory.CreateRectangleGeometry(outerRect.ToDirect2D());

            var maskBounds = GetShadowMaskBounds(outerRect, shadow.Blur);
            DrawShadowGeometry(casterGeometry, maskBounds, shadow, originalGeometry, roundedRect.Rect);
        }

        private void DrawShadowGeometry(
            ID2D1Geometry geometry,
            Rect maskBounds,
            BoxShadow shadow,
            ID2D1Geometry clipGeometry,
            Rect clipBounds)
        {
            if (maskBounds.Width <= 0 || maskBounds.Height <= 0)
                return;

            void Draw()
            {
                if (shadow.Blur <= 0)
                {
                    using var shadowBrush = _deviceContext.CreateSolidColorBrush(shadow.Color.ToDirect2D());
                    _deviceContext.FillGeometry(geometry, shadowBrush);
                    return;
                }

                using var mask = CreateGeometryMask(geometry, maskBounds);
                using var shadowEffect = CreateShadowEffect(mask.Bitmap, shadow);
                using var output = shadowEffect.Output;
                _deviceContext.DrawImage(
                    output,
                    new Vector2((float)maskBounds.X, (float)maskBounds.Y),
                    null,
                    InterpolationMode.Linear,
                    CompositeMode.SourceOver);
            }

            DrawWithGeometryClip(clipGeometry, clipBounds, Draw);
        }

        private ID2D1BitmapRenderTarget CreateGeometryMask(ID2D1Geometry geometry, Rect maskBounds)
        {
            var mask = _renderTarget.CreateCompatibleRenderTarget(
                maskBounds.Size.ToSharpDX(),
                null,
                null,
                CompatibleRenderTargetOptions.None);

            try
            {
                mask.BeginDraw();
                mask.Clear(Colors.Transparent.ToDirect2D());
                mask.Transform = Matrix3x2.CreateTranslation(
                    (float)-maskBounds.X,
                    (float)-maskBounds.Y);

                using (var brush = mask.CreateSolidColorBrush(Colors.White.ToDirect2D()))
                {
                    mask.FillGeometry(geometry, brush);
                }

                mask.EndDraw().CheckError();
                return mask;
            }
            catch
            {
                mask.Dispose();
                throw;
            }
        }

        private ID2D1Effect CreateShadowEffect(ID2D1Image source, BoxShadow shadow)
        {
            var effect = new ID2D1Effect(_deviceContext.CreateEffect(EffectGuids.Shadow));
            effect.SetInput(0, source, true);
            effect.SetValue((uint)ShadowProperties.BlurStandardDeviation, SkBlurRadiusToSigma(shadow.Blur));
            effect.SetValue(
                (uint)ShadowProperties.Color,
                new Vector4(
                    shadow.Color.R / 255f,
                    shadow.Color.G / 255f,
                    shadow.Color.B / 255f,
                    shadow.Color.A / 255f));
            return effect;
        }

        private void DrawWithGeometryClip(ID2D1Geometry clipGeometry, Rect bounds, Action draw)
        {
            var parameters = new LayerParameters
            {
                ContentBounds = bounds.ToDirect2D(),
                MaskTransform = Matrix3x2.Identity,
                Opacity = 1,
                GeometricMask = clipGeometry,
                MaskAntialiasMode = AntialiasMode.PerPrimitive
            };

            PushDirect2DLayer(parameters);
            try
            {
                draw();
            }
            finally
            {
                PopLayer();
            }
        }

        private static ID2D1Geometry CombineInsetShadowGeometry(ID2D1Geometry outerGeometry, RoundedRect inner)
        {
            using var innerGeometry = CreateRoundedRectGeometry(inner);
            return CombineGeometries(outerGeometry, innerGeometry, CombineMode.Exclude);
        }

        private static ID2D1PathGeometry CombineGeometries(
            ID2D1Geometry first,
            ID2D1Geometry second,
            CombineMode mode)
        {
            var result = Direct2D1Platform.Direct2D1Factory.CreatePathGeometry();
            try
            {
                using (var sink = result.Open())
                {
                    first.CombineWithGeometry(second, mode, sink);
                    sink.Close();
                }

                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        private static RoundedRect InflateAndOffsetRoundedRect(
            RoundedRect roundedRect,
            double inflate,
            double offsetX,
            double offsetY)
        {
            var rect = new Rect(
                roundedRect.Rect.X - inflate + offsetX,
                roundedRect.Rect.Y - inflate + offsetY,
                roundedRect.Rect.Width + (inflate * 2),
                roundedRect.Rect.Height + (inflate * 2));

            return new RoundedRect(
                rect,
                InflateRadius(roundedRect.RadiiTopLeft, inflate),
                InflateRadius(roundedRect.RadiiTopRight, inflate),
                InflateRadius(roundedRect.RadiiBottomRight, inflate),
                InflateRadius(roundedRect.RadiiBottomLeft, inflate));
        }

        private static AVector InflateRadius(AVector radius, double inflate)
            => new(Math.Max(0, radius.X + inflate), Math.Max(0, radius.Y + inflate));

        private static Rect GetShadowMaskBounds(Rect geometryBounds, double blur)
            => geometryBounds.Inflate(GetShadowPadding(blur));

        private static double GetShadowPadding(double blur)
        {
            if (blur <= 0)
                return 0;

            var sigma = SkBlurRadiusToSigma(blur);
            return Math.Ceiling(Math.Max(blur, sigma * 3.0) + 1.0);
        }

        private static float SkBlurRadiusToSigma(double radius)
            => radius <= 0 ? 0 : (0.288675f * (float)radius) + 0.5f;

        private static Rect AreaCastingShadowInHole(
            Rect holeRect,
            double shadowBlur,
            double shadowSpread,
            double offsetX,
            double offsetY)
        {
            var area = holeRect.Inflate(shadowBlur);
            if (shadowSpread < 0)
                area = area.Inflate(-shadowSpread);

            var offsetArea = OffsetRect(area, -offsetX, -offsetY);
            return area.Union(offsetArea);
        }

        private static Rect OffsetRect(Rect rect, double offsetX, double offsetY)
            => new(rect.X + offsetX, rect.Y + offsetY, rect.Width, rect.Height);

        public void PushClip(IPlatformRenderInterfaceRegion region)
        {
            throw new NotSupportedException();
        }

        public void PopClip()
        {
            var geometry = _clipKindStack.Pop();
            if (geometry is null)
            {
                _deviceContext.PopAxisAlignedClip();
            }
            else
            {
                PopLayer();
                geometry.Dispose();
            }
        }

        public void PushLayer(Rect bounds)
        {
            var parameters = new LayerParameters
            {
                ContentBounds = bounds.ToDirect2D(),
                MaskTransform = Matrix3x2.Identity,
                Opacity = 1
            };
            PushDirect2DLayer(parameters);
        }

        void IDrawingContextImpl.PopLayer()
        {
            PopLayer();
        }

        /// <summary>
        /// Pushes an opacity value.
        /// </summary>
        /// <param name="opacity">The opacity.</param>
        /// <param name="bounds">The bounds.</param>
        /// <returns>A disposable used to undo the opacity.</returns>
        public void PushOpacity(double opacity, Rect? bounds)
        {
            if (opacity < 1)
            {
                if (bounds == null || bounds == default(Rect))
                {
                    bounds = new Rect(0, 0, _renderTarget.PixelSize.Width, _renderTarget.PixelSize.Height);
                }

                var parameters = new LayerParameters
                {
                    MaskTransform = Matrix3x2.Identity,
                    Opacity = (float)opacity
                };

                if (bounds.HasValue)
                {
                    parameters.ContentBounds = bounds.Value.ToDirect2D();
                }

                PushDirect2DLayer(parameters);
            }
            else
                _layers.Push(null);
        }

        public void PopOpacity()
        {
            PopLayer();
        }

        public void PushRenderOptions(RenderOptions renderOptions)
        {
            _renderOptionsStack.Push(RenderOptions);

            RenderOptions = RenderOptions.MergeWith(renderOptions);
        }

        public void PushTextOptions(TextOptions textOptions)
        {
            _textOptionsStack.Push(TextOptions);

            TextOptions = TextOptions.MergeWith(textOptions);
        }

        public void PopRenderOptions()
        {
            RenderOptions = _renderOptionsStack.Pop();
        }

        public void PopTextOptions()
        {
            TextOptions = _textOptionsStack.Pop();
        }

        private void PopLayer()
        {
            var layer = _layers.Pop();
            if (layer != null)
            {
                _deviceContext.PopLayer();
                _layerPool.Push(layer);
            }
        }

        private void PushDirect2DLayer(LayerParameters parameters)
        {
            var layer = RentLayer(out var fromPool);
            try
            {
                _deviceContext.PushLayer(parameters, layer);
            }
            catch
            {
                ReturnUnusedLayer(layer, fromPool);
                throw;
            }

            _layers.Push(layer);
        }

        private ID2D1Layer RentLayer(out bool fromPool)
        {
            if (_layerPool.Count != 0)
            {
                fromPool = true;
                return _layerPool.Pop();
            }

            fromPool = false;
            return _deviceContext.CreateLayer();
        }

        private void ReturnUnusedLayer(ID2D1Layer layer, bool fromPool)
        {
            if (fromPool)
                _layerPool.Push(layer);
            else
                layer.Dispose();
        }

        /// <summary>
        /// Creates a Direct2D brush wrapper for a Avalonia brush.
        /// </summary>
        /// <param name="brush">The avalonia brush.</param>
        /// <param name="destinationRect">The size of the brush's target area.</param>
        /// <returns>The Direct2D brush wrapper.</returns>
        public BrushImpl CreateBrush(IBrush? brush, Rect destinationRect)
        {
            var solidColorBrush = brush as ISolidColorBrush;
            var linearGradientBrush = brush as ILinearGradientBrush;
            var radialGradientBrush = brush as IRadialGradientBrush;
            var conicGradientBrush = brush as IConicGradientBrush;
            var imageBrush = brush as IImageBrush;
            var sceneBrush = brush as ISceneBrush;
            var sceneBrushContent = brush as ISceneBrushContent;

            if (solidColorBrush != null)
            {
                return new SolidColorBrushImpl(solidColorBrush, _deviceContext);
            }
            else if (linearGradientBrush != null)
            {
                return new LinearGradientBrushImpl(linearGradientBrush, _deviceContext, destinationRect);
            }
            else if (radialGradientBrush != null)
            {
                return new RadialGradientBrushImpl(radialGradientBrush, _deviceContext, destinationRect);
            }
            else if (conicGradientBrush != null)
            {
                return new ConicGradientBrushImpl(conicGradientBrush, _deviceContext, destinationRect);
            }
            else if (imageBrush?.Source is { } imageBrushSource && TryGetImageBrushBitmap(imageBrushSource, out var imageBrushBitmap))
            {
                return new ImageBrushImpl(
                    imageBrush,
                    _deviceContext,
                    imageBrushBitmap,
                    destinationRect);
            }
            else if (sceneBrush != null || sceneBrushContent != null)
            {
                if (sceneBrushContent == null && sceneBrush != null)
                {
                    sceneBrushContent = sceneBrush.CreateContent();
                }
                if (sceneBrushContent != null)
                {
                    var rect = sceneBrushContent.Rect;
                    var intermediateSize = rect.Size;

                    if (intermediateSize.Width >= 1 && intermediateSize.Height >= 1)
                    {
                        // We need to ensure the size we're requesting is an integer pixel size, otherwise
                        // D2D alters the DPI of the render target, which messes stuff up. PixelSize.FromSize
                        // will do the rounding for us.
                        var dpi = new Avalonia.Vector(_deviceContext.Dpi.Width, _deviceContext.Dpi.Height);
                        var pixelSize = PixelSize.FromSizeWithDpi(intermediateSize, dpi);

                        var transform = rect.TopLeft == default ?
                            Matrix.Identity :
                            Matrix.CreateTranslation(-rect.X, -rect.Y);

                        var brushTransform = Matrix.Identity;

                        if (sceneBrushContent.Transform != null)
                        {
                            var transformOrigin = sceneBrushContent.TransformOrigin.ToPixels(rect);
                            var offset = Matrix.CreateTranslation(transformOrigin);

                            brushTransform = -offset * sceneBrushContent.Transform.Value * offset;
                        }

                        using (var intermediate = _deviceContext.CreateCompatibleRenderTarget(
                                   pixelSize.ToSizeWithDpi(dpi).ToSharpDX(),
                                   null,
                                   null,
                                   CompatibleRenderTargetOptions.None))
                        {
                            using (var ctx = new RenderTarget(intermediate).CreateDrawingContext(true))
                            {
                                intermediate.Clear(null);

                                if (sceneBrush?.TileMode == TileMode.None)
                                {
                                    transform = brushTransform * transform;
                                }

                                sceneBrushContent.Render(ctx, transform);
                            }

                            return new ImageBrushImpl(
                                sceneBrushContent.Brush,
                                _deviceContext,
                                new D2DBitmapImpl(intermediate.Bitmap.QueryInterface<ID2D1Bitmap1>()),
                                destinationRect);
                        }

                    }
                }
            }

            return new SolidColorBrushImpl(null, _deviceContext);
        }

        public void PushGeometryClip(IGeometryImpl clip)
        {
            var parameters = new LayerParameters
            {
                ContentBounds = PrimitiveExtensions.RectangleInfinite,
                MaskTransform = Matrix3x2.Identity,
                Opacity = 1,
                GeometricMask = ((GeometryImpl)clip).Geometry,
                MaskAntialiasMode = AntialiasMode.PerPrimitive
            };
            PushDirect2DLayer(parameters);

        }

        public void PopGeometryClip()
        {
            PopLayer();
        }

        /// <summary>
        /// The bitmap blending mode currently in effect. A pushed blend mode (via
        /// <see cref="PushBitmapBlendMode"/> takes precedence; otherwise the per-draw
        /// <see cref="RenderOptions.BitmapBlendingMode"/> is used.
        /// </summary>
        private BitmapBlendingMode EffectiveBitmapBlendingMode
            => _bitmapBlendModeStack.Count > 0
                ? _bitmapBlendModeStack.Peek()
                : RenderOptions.BitmapBlendingMode;

        public void PushBitmapBlendMode(BitmapBlendingMode blendingMode)
        {
            _bitmapBlendModeStack.Push(blendingMode);
        }

        public void PopBitmapBlendMode()
        {
            _bitmapBlendModeStack.Pop();
        }

        public void PushOpacityMask(IBrush mask, Rect bounds)
        {
            var opacityBrush = CreateBrush(mask, bounds);
            var parameters = new LayerParameters
            {
                ContentBounds = PrimitiveExtensions.RectangleInfinite,
                MaskTransform = Matrix3x2.Identity,
                Opacity = 1,
                OpacityBrush = opacityBrush.PlatformBrush
            };
            try
            {
                PushDirect2DLayer(parameters);
            }
            catch
            {
                opacityBrush.Dispose();
                throw;
            }

            _opacityMaskBrushes.Push(opacityBrush);
        }

        public void PopOpacityMask()
        {
            try
            {
                PopLayer();
            }
            finally
            {
                _opacityMaskBrushes.Pop()?.Dispose();
            }
        }

        public object? GetFeature(Type t) => null;

        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties, typeof(IImageBrushSource))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, "Avalonia.Utilities.IRef`1", "Avalonia.Base")]
        private static bool TryGetImageBrushBitmap(IImageBrushSource imageBrushSource, out BitmapImpl bitmap)
        {
            bitmap = null!;

            var bitmapRef = s_imageBrushBitmapProperty?.GetValue(imageBrushSource);
            if (bitmapRef is null)
                return false;

            if (s_imageBrushBitmapItemProperty?.GetValue(bitmapRef) is not BitmapImpl platformBitmap)
                return false;

            bitmap = platformBitmap;
            return true;
        }

        private static bool AreClose(double left, double right) => Math.Abs(left - right) < 0.0001;

        private static bool IsZero(double value) => Math.Abs(value) < 0.0001;

        private void ApplyRenderOptions(RenderOptions renderOptions)
        {
            _deviceContext.AntialiasMode = renderOptions.EdgeMode != EdgeMode.Aliased ? AntialiasMode.PerPrimitive : AntialiasMode.Aliased;
#pragma warning disable CS0618
            _deviceContext.TextAntialiasMode = GetTextAntialiasMode(renderOptions.TextRenderingMode);
#pragma warning restore CS0618
        }

        private TextAntialiasMode GetEffectiveTextAntialiasMode()
        {
#pragma warning disable CS0618
            var textRenderingMode = TextOptions.TextRenderingMode != TextRenderingMode.Unspecified
                ? TextOptions.TextRenderingMode
                : RenderOptions.TextRenderingMode;
#pragma warning restore CS0618

            if (textRenderingMode == TextRenderingMode.Unspecified)
            {
                textRenderingMode = RenderOptions.EdgeMode != EdgeMode.Aliased
                    ? TextRenderingMode.SubpixelAntialias
                    : TextRenderingMode.Alias;
            }

            return GetTextAntialiasMode(textRenderingMode);
        }

        private static TextAntialiasMode GetTextAntialiasMode(TextRenderingMode textRenderingMode)
        {
            return textRenderingMode switch
            {
                TextRenderingMode.Alias => TextAntialiasMode.Aliased,
                TextRenderingMode.Antialias => TextAntialiasMode.Grayscale,
                TextRenderingMode.SubpixelAntialias => TextAntialiasMode.Cleartype,
                _ => TextAntialiasMode.Default
            };
        }
    }
}
