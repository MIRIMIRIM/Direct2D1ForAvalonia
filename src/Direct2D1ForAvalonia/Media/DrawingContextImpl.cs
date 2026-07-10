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
        private Action? _finishedCallback;
        private Action? _cleanupCallback;
        private readonly string _diagnosticTargetName;
        // True while BeginDraw has been called and EndDraw has not. Allows the same instance
        // to be reopened across frames (see ReopenSession) without reallocating stacks/caches.
        private bool _sessionOpen;
        // When true, Dispose ends a session but keeps the (possibly QI'd) device context alive
        // for ReopenSession. Hosts that pool DrawingContextImpl must set this before first Dispose.
        private bool _retainAcrossSessions;

        private readonly Stack<RenderOptions> _renderOptionsStack = new Stack<RenderOptions>();
        private readonly Stack<TextOptions> _textOptionsStack = new Stack<TextOptions>();
        // true = a real D2D layer was pushed (needs PopLayer); false = no-op push (e.g. opacity >= 1).
        // Layer resources themselves are managed by Direct2D: PushLayer(..., null) is the D2D 1.1+
        // recommended path and avoids CreateLayer + a short-lived per-frame pool.
        private readonly Stack<bool> _layerPushed = new Stack<bool>();
        private readonly Stack<BrushImpl?> _opacityMaskBrushes = new Stack<BrushImpl?>();
        private readonly Stack<BitmapBlendingMode> _bitmapBlendModeStack = new Stack<BitmapBlendingMode>();
        // For each PushClip, records how PopClip should undo it and owns any geometric mask.
        // Deferred rounded clips may be merged with a following PushOpacity into a single layer,
        // or kept as a soft-clip (no D2D layer) when subsequent draws can bake clip+opacity.
        private readonly Stack<ClipEntry> _clipStack = new Stack<ClipEntry>();
        // Number of soft-merged opacities that did not push a D2D layer (PopOpacity is a no-op).
        private int _softOpacityDepth;
        // Standalone soft opacity (no rounded clip): bake into solid fills/simple pens.
        // null = none deferred. Converted to a real layer on incompatible draws.
        private float? _standaloneSoftOpacity;
        // Lightweight counters for offscreen vs real-window profiling (reset each session).
        private int _softPathHits;
        private int _softPathMisses;
        private int _layerPushes;
        private int _deferredClipFlushes;
        private int _pushClips;
        private int _pushOpacities;
        private int _drawRectangles;
        private static readonly PropertyInfo? s_imageBrushBitmapProperty = typeof(IImageBrushSource).GetProperty(
            "Bitmap",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly PropertyInfo? s_imageBrushBitmapItemProperty = s_imageBrushBitmapProperty?.PropertyType.GetProperty(
            "Item",
            BindingFlags.Instance | BindingFlags.Public);
        // Solid brushes and stroke styles live on D2DDeviceResourceCache (render-target lifetime)
        // so they survive across frames. See GetOrCreateSolidBrush / GetOrCreateStrokeStyle.
        // Cross-frame device-resource cache keyed on the render target. Used for gradient stop
        // collections, which depend only on stops+spread (not geometry) and so recur every frame.
        private readonly D2DDeviceResourceCache _deviceResources;
        private readonly SolidRectBatch _solidRectBatch = new();
        private RenderOptions _renderOptions;
        private TextOptions _textOptions;
        private readonly Matrix? _postTransform;
        private Matrix? _targetTransform;
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
            // Prefer the Avalonia host (window texture / bitmap impl) over the raw D2D RCW type so
            // process-wide call stats can distinguish window surface vs intermediate targets.
            _diagnosticTargetName = layerFactory?.GetType().Name
                ?? (_swapChain is not null ? "SwapChain" : _renderTarget.GetType().Name);
            _deviceResources = D2DDeviceResourceCache.For(_renderTarget);

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

            OpenSession();
        }

        /// <summary>
        /// Marks this instance as pooled: session <see cref="Dispose"/> will not release a QI'd
        /// device context so <see cref="ReopenSession"/> can call BeginDraw again.
        /// Must be called before the first session Dispose.
        /// Also arms command-list deferral for simple solid-rect sessions when still empty.
        /// </summary>
        internal void EnableSessionReuse()
        {
            _retainAcrossSessions = true;
            // Constructor already opened a session with deferral off; re-arm before any draws.
            _solidRectBatch.BeginSession(enableCommandListReuse: true);
        }

        /// <summary>
        /// Reopens a previously disposed drawing context for another frame on the same device
        /// context. Avoids reallocating per-frame stacks and re-resolving device resource caches.
        /// </summary>
        internal void ReopenSession(
            Action? finishedCallback = null,
            Action? cleanupCallback = null,
            Matrix? targetTransform = null)
        {
            if (_sessionOpen)
                throw new InvalidOperationException("Drawing context session is already open.");

            _retainAcrossSessions = true;

            // Defensive: a reused context must not carry clip/layer state across frames.
            if (_clipStack.Count != 0 || _layerPushed.Count != 0 || _softOpacityDepth != 0
                || _standaloneSoftOpacity is not null
                || _opacityMaskBrushes.Count != 0 || _bitmapBlendModeStack.Count != 0
                || _renderOptionsStack.Count != 0 || _textOptionsStack.Count != 0)
            {
                throw new InvalidOperationException("Drawing context still has pushed state from a previous frame.");
            }

            _finishedCallback = finishedCallback;
            _cleanupCallback = cleanupCallback;
            _targetTransform = targetTransform;
            _transform = Matrix.Identity;
            _renderOptions = default;
            _textOptions = default;
            OpenSession();
        }

        /// <summary>
        /// Soft-path / layer counters for the current (or last completed) session.
        /// Used by diagnostics and windowed profiling — not part of the public API.
        /// </summary>
        internal (int SoftHits, int SoftMisses, int LayerPushes, int DeferredFlushes, int PushClips, int PushOpacities, int DrawRectangles) SessionCounters
            => (_softPathHits, _softPathMisses, _layerPushes, _deferredClipFlushes, _pushClips, _pushOpacities, _drawRectangles);

        private void OpenSession()
        {
            _sessionOpen = true;
            _softPathHits = 0;
            _softPathMisses = 0;
            _layerPushes = 0;
            _deferredClipFlushes = 0;
            _pushClips = 0;
            _pushOpacities = 0;
            _drawRectangles = 0;
            _solidRectBatch.BeginSession(enableCommandListReuse: _retainAcrossSessions);
            DrawingContextCallStats.OnSessionOpen(_diagnosticTargetName);
            _deviceContext.BeginDraw();

            // Reset world transform so a reused context never inherits the previous frame's matrix.
            if (_targetTransform.HasValue || _postTransform.HasValue)
                ApplyTransform();
            else
                _deviceContext.Transform = Matrix3x2.Identity;
        }

        /// <summary>
        /// Flushes deferred solid-rect strokes / leaves simple-session deferral (call before any
        /// non-simple draw or state change that must observe prior primitives).
        /// </summary>
        private void FlushPrimitiveBatch()
        {
            _solidRectBatch.MarkNonSimple(_deviceContext, _deviceResources);
        }

        /// <summary>
        /// Gets the current transform of the drawing context.
        /// </summary>
        public Matrix Transform
        {
            get { return _transform; }
            set
            {
                FlushPrimitiveBatch();
                FlushDeferredClip();
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
            FlushPrimitiveBatch();
            FlushDeferredClip();
            _deviceContext.Clear(color.ToDirect2D());
        }

        /// <summary>
        /// Ends a draw operation.
        /// </summary>
        public void Dispose()
        {
            // Ending a reusable session is idempotent: a second Dispose after ReopenSession has
            // not been called is a no-op (matches Avalonia's using-per-frame pattern).
            if (!_sessionOpen)
                return;

            // Release clip geometries left behind by unmatched PushClip. Actual D2D layers
            // are balanced via _layerPushed below. Shared (cached) geometries stay in the cache.
            while (_clipStack.Count > 0)
            {
                ReleaseClipGeometry(_clipStack.Pop());
            }
            _softOpacityDepth = 0;
            _standaloneSoftOpacity = null;

            // Balance any unmatched PushLayer before EndDraw. D2D owns the layer resources when
            // PushLayer is called with a null layer, so there is nothing for us to dispose.
            while (_layerPushed.Count > 0)
            {
                if (_layerPushed.Pop())
                {
                    try { _deviceContext.PopLayer(); }
                    catch { /* best-effort during teardown */ }
                }
            }

            try
            {
                if (Direct2D1Diagnostics.IsEnabled)
                {
                    Direct2D1Diagnostics.Write(
                        $"drawing-context-dispose begin target={_diagnosticTargetName} hasSwapChain={_swapChain != null} hasFinishedCallback={_finishedCallback != null} hasCleanupCallback={_cleanupCallback != null}");
                }

                // Stroke multi-batch + optional command-list replay/rebuild for simple sessions.
                _solidRectBatch.EndSession(_deviceContext, _deviceResources);

                Direct2D1FrameProfiler.MarkEndDrawStart(
                    _softPathHits, _softPathMisses, _layerPushes, _deferredClipFlushes,
                    _pushClips, _pushOpacities, _drawRectangles);
                _deviceContext.EndDraw().CheckError();
                Direct2D1FrameProfiler.MarkEndDrawDone();
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
                    // Drain any opacity-mask brushes left pushed by a render that aborted before
                    // matching pops. Layer resources are owned by Direct2D when PushLayer is
                    // called with a null layer, and unmatched pushes are popped above.
                    foreach (var maskBrush in _opacityMaskBrushes)
                    {
                        maskBrush?.Dispose();
                    }
                    _opacityMaskBrushes.Clear();

                    // Solid brushes / stroke styles are owned by D2DDeviceResourceCache and must
                    // not be disposed with the per-frame drawing context.

                    // Mark the session closed so the instance can be reopened.
                    _sessionOpen = false;
                    _finishedCallback = null;
                    _cleanupCallback = null;

                    // Owned QI'd device contexts (WIC RT → ID2D1DeviceContext) must survive
                    // across ReopenSession. One-shot contexts still release on session end.
                    if (_ownsDeviceContext && !_retainAcrossSessions)
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
            FlushDeferredClip();
            if (EffectiveBitmapBlendingMode == BitmapBlendingMode.Destination)
                return;

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
                case BitmapBlendingMode.Source:
                    return CompositeMode.SourceCopy;
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
            FlushDeferredClip();
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
            FlushPrimitiveBatch();
            FlushDeferredClip();
            if (pen?.Brush != null)
            {
                var bounds = new Rect(p1, p2);

                using (var d2dBrush = CreateBrush(pen.Brush, bounds))
                {
                    var d2dStroke = GetOrCreateStrokeStyle(pen);
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
            FlushPrimitiveBatch();
            FlushDeferredClip();
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
                {
                    var d2dStroke = GetOrCreateStrokeStyle(pen);
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
            _drawRectangles++;
            DrawingContextCallStats.OnDrawRectangle(_diagnosticTargetName);
            if (TryDrawRectangleWithSoftClip(brush, pen, rrect, boxShadow))
                return;

            // Hot path for UI chrome: solid fill and/or solid stroke, uniform rounded (or axis-
            // aligned) rect, no shadows, no deferred clip. Avoids CreateBrush type-switch and
            // default stroke-style COM when the pen matches D2D defaults.
            if (TryDrawSimpleSolidRectangle(brush, pen, rrect, boxShadow))
                return;

            FlushPrimitiveBatch();
            FlushDeferredClip();
            var rc = rrect.Rect.ToDirect2D();
            var rect = rrect.Rect;
            var radiusX = Math.Max(rrect.RadiiTopLeft.X,
                Math.Max(rrect.RadiiTopRight.X, Math.Max(rrect.RadiiBottomRight.X, rrect.RadiiBottomLeft.X)));
            var radiusY = Math.Max(rrect.RadiiTopLeft.Y,
                Math.Max(rrect.RadiiTopRight.Y, Math.Max(rrect.RadiiBottomRight.Y, rrect.RadiiBottomLeft.Y)));
            var isRounded = !IsZero(radiusX) || !IsZero(radiusY);

            // Direct2D's native FillRoundedRectangle/DrawRoundedRectangle only support a single
            // radius pair for all four corners. When all corners share the same radius (the common
            // case), we can use the native primitives and skip the PathGeometry allocation entirely.
            // When radii differ per corner, we fall back to a PathGeometry.
            // The native primitive is only equivalent to the geometry path when no radius
            // clamping is required. D2D clamps each radius independently to half the extent,
            // whereas NormalizeRadii (and Skia) scale radii proportionally — the two diverge
            // once a radius exceeds half of a single dimension. Guard on the unclamped case.
            var uniformRadius = isRounded
                && AreRadiiUniform(rrect)
                && 2 * radiusX <= rect.Width + 0.0001
                && 2 * radiusY <= rect.Height + 0.0001;
            var d2dRoundedRect = uniformRadius
                ? new RoundedRectangle
                {
                    Rect = rc,
                    RadiusX = (float)rrect.RadiiTopLeft.X,
                    RadiusY = (float)rrect.RadiiTopLeft.Y
                }
                : default;

            // Build the PathGeometry only when needed: non-uniform radii, or box shadows
            // (which require a geometry to cast/inset from). For uniform-radius rects without
            // shadows, the native D2D FillRoundedRectangle/DrawRoundedRectangle primitives
            // are used directly — no geometry allocation at all.
            var needsGeometry = !uniformRadius || boxShadow != default;
            using var roundedGeometry = (isRounded && needsGeometry) ? CreateRoundedRectGeometry(rrect) : null;
            using var rectGeometry = (!isRounded && boxShadow != default) ? Direct2D1Platform.Direct2D1Factory.CreateRectangleGeometry(rc) : null;
            ID2D1Geometry? shapeGeometry = (ID2D1Geometry?)roundedGeometry ?? rectGeometry;

            DrawBoxShadows(rrect, shapeGeometry, boxShadow, inset: false);

            if (brush != null)
            {
                using (var b = CreateBrush(brush, rect))
                {
                    if (b.PlatformBrush != null)
                    {
                        if (isRounded)
                        {
                            if (uniformRadius)
                                _deviceContext.FillRoundedRectangle(d2dRoundedRect, b.PlatformBrush);
                            else
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
                {
                    var d2dStroke = GetOrCreateStrokeStyle(pen);
                    if (wrapper.PlatformBrush != null)
                    {
                        var thickness = (float)pen.Thickness;
                        if (isRounded)
                        {
                            if (uniformRadius)
                            {
                                if (d2dStroke is null)
                                    _deviceContext.DrawRoundedRectangle(d2dRoundedRect, wrapper.PlatformBrush, thickness);
                                else
                                    _deviceContext.DrawRoundedRectangle(d2dRoundedRect, wrapper.PlatformBrush, thickness, d2dStroke);
                            }
                            else if (d2dStroke is null)
                                _deviceContext.DrawGeometry(roundedGeometry!, wrapper.PlatformBrush, thickness);
                            else
                                _deviceContext.DrawGeometry(roundedGeometry!, wrapper.PlatformBrush, thickness, d2dStroke);
                        }
                        else if (d2dStroke is null)
                            _deviceContext.DrawRectangle(rc, wrapper.PlatformBrush, thickness);
                        else
                            _deviceContext.DrawRectangle(rc, wrapper.PlatformBrush, thickness, d2dStroke);
                    }
                }
            }
        }

        /// <summary>
        /// Fast path for solid-color fill/stroke rectangles (axis-aligned or uniform rounded).
        /// Covers RoundedRectGrid and typical chrome: no shadows, no active soft clip, solid brushes only.
        /// Uses <see cref="SolidRectBatch"/> for multi-stroke merge and command-list session replay.
        /// </summary>
        private bool TryDrawSimpleSolidRectangle(
            IBrush? brush,
            IPen? pen,
            RoundedRect rrect,
            BoxShadows boxShadow)
        {
            if (boxShadow != default)
                return false;
            // Soft clip / standalone soft opacity must go through the soft or full path.
            if (_standaloneSoftOpacity is not null)
                return false;
            if (_clipStack.Count > 0)
            {
                var top = _clipStack.Peek().State;
                if (top is ClipState.Deferred or ClipState.SoftMerged)
                    return false; // soft path already tried and missed
            }

            ISolidColorBrush? solidFill = null;
            if (brush is not null)
            {
                if (brush is not ISolidColorBrush sf)
                    return false;
                solidFill = sf;
            }

            ISolidColorBrush? solidStroke = null;
            float strokeThickness = 0;
            ID2D1StrokeStyle? strokeStyle = null;
            if (pen is not null)
            {
                if (pen.Brush is not ISolidColorBrush sp || pen.Thickness <= 0)
                    return false;
                solidStroke = sp;
                strokeThickness = (float)pen.Thickness;
                strokeStyle = GetOrCreateStrokeStyle(pen);
            }

            if (solidFill is null && solidStroke is null)
                return false;

            // Translucent solids: keep immediate ordered path (no batch / no CL).
            if (solidFill is { Opacity: < 0.999 } || solidStroke is { Opacity: < 0.999 })
                return false;

            FlushDeferredClip();

            var rect = rrect.Rect;
            var radiusX = Math.Max(rrect.RadiiTopLeft.X,
                Math.Max(rrect.RadiiTopRight.X, Math.Max(rrect.RadiiBottomRight.X, rrect.RadiiBottomLeft.X)));
            var radiusY = Math.Max(rrect.RadiiTopLeft.Y,
                Math.Max(rrect.RadiiTopRight.Y, Math.Max(rrect.RadiiBottomRight.Y, rrect.RadiiBottomLeft.Y)));
            var isRounded = !IsZero(radiusX) || !IsZero(radiusY);

            if (isRounded)
            {
                if (!AreRadiiUniform(rrect)
                    || 2 * radiusX > rect.Width + 0.0001
                    || 2 * radiusY > rect.Height + 0.0001)
                {
                    return false; // non-uniform / clamp case → full path
                }
            }

            return _solidRectBatch.HandleSimpleRect(
                _deviceContext,
                _deviceResources,
                rrect,
                isRounded,
                isRounded ? (float)rrect.RadiiTopLeft.X : 0,
                isRounded ? (float)rrect.RadiiTopLeft.Y : 0,
                solidFill,
                solidStroke,
                strokeThickness,
                strokeStyle);
        }

        /// <summary>
        /// Checks whether all four corner radii of a <see cref="RoundedRect"/> are equal,
        /// meaning the D2D native rounded-rect primitives can be used instead of a PathGeometry.
        /// </summary>
        private static bool AreRadiiUniform(RoundedRect rrect)
        {
            return AreClose(rrect.RadiiTopLeft.X, rrect.RadiiTopRight.X)
                && AreClose(rrect.RadiiTopLeft.X, rrect.RadiiBottomRight.X)
                && AreClose(rrect.RadiiTopLeft.X, rrect.RadiiBottomLeft.X)
                && AreClose(rrect.RadiiTopLeft.Y, rrect.RadiiTopRight.Y)
                && AreClose(rrect.RadiiTopLeft.Y, rrect.RadiiBottomRight.Y)
                && AreClose(rrect.RadiiTopLeft.Y, rrect.RadiiBottomLeft.Y);
        }

        public void DrawRegion(IBrush? brush, IPen? pen, IPlatformRenderInterfaceRegion region)
        {
            FlushDeferredClip();
            if (region.IsEmpty)
                return;

            if (region is not Direct2DRegionImpl d2dRegion)
                throw new InvalidOperationException("Region was not created by this Direct2D backend.");

            using var geometry = d2dRegion.CreateGeometry();
            var bounds = Direct2DRegionImpl.ToRect(d2dRegion.Bounds);

            if (brush != null)
            {
                using (var d2dBrush = CreateBrush(brush, bounds))
                {
                    if (d2dBrush.PlatformBrush != null)
                    {
                        _deviceContext.FillGeometry(geometry, d2dBrush.PlatformBrush);
                    }
                }
            }

            if (pen?.Brush != null)
            {
                using (var d2dBrush = CreateBrush(pen.Brush, bounds.Inflate(pen.Thickness / 2)))
                {
                    var d2dStroke = GetOrCreateStrokeStyle(pen);
                    if (d2dBrush.PlatformBrush != null)
                    {
                        _deviceContext.DrawGeometry(
                            geometry,
                            d2dBrush.PlatformBrush,
                            (float)pen.Thickness,
                            d2dStroke);
                    }
                }
            }
        }

        /// <inheritdoc />
        public void DrawEllipse(IBrush? brush, IPen? pen, Rect rect)
        {
            FlushPrimitiveBatch();
            FlushDeferredClip();
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
                {
                    var d2dStroke = GetOrCreateStrokeStyle(pen);
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
            FlushDeferredClip();
            using (var brush = CreateBrush(foreground, glyphRun.Bounds))
            {
                var immutableGlyphRun = (GlyphRunImpl)glyphRun;

                var dxGlyphRun = immutableGlyphRun.GlyphRun;
                var previousTextAntialiasMode = _deviceContext.TextAntialiasMode;

                // When BaselinePixelAlignment is requested, snap the baseline origin to the
                // device pixel grid to produce crisper text at small sizes.
                var baselineOrigin = glyphRun.BaselineOrigin;
                if (TextOptions.BaselinePixelAlignment == BaselinePixelAlignment.Aligned)
                {
                    var dpi = _deviceContext.Dpi;
                    baselineOrigin = new Point(
                        Math.Round(baselineOrigin.X * dpi.Width / 96.0) * 96.0 / dpi.Width,
                        Math.Round(baselineOrigin.Y * dpi.Height / 96.0) * 96.0 / dpi.Height);
                }

                try
                {
                    _deviceContext.TextAntialiasMode = GetEffectiveTextAntialiasMode();
                    _renderTarget.DrawGlyphRun(baselineOrigin.ToVortice(), dxGlyphRun,
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
            _pushClips++;
            DrawingContextCallStats.OnPushClip();
            FlushDeferredClip();
            _clipStack.Push(ClipEntry.AxisAligned());
            _deviceContext.PushAxisAlignedClip(clip.ToVortice(), AntialiasMode.PerPrimitive);
        }

        public void PushClip(RoundedRect clip)
        {
            _pushClips++;
            DrawingContextCallStats.OnPushClip();
            FlushDeferredClip();

            var radiusX = Math.Max(clip.RadiiTopLeft.X,
                Math.Max(clip.RadiiTopRight.X, Math.Max(clip.RadiiBottomRight.X, clip.RadiiBottomLeft.X)));
            var radiusY = Math.Max(clip.RadiiTopLeft.Y,
                Math.Max(clip.RadiiTopRight.Y, Math.Max(clip.RadiiBottomRight.Y, clip.RadiiBottomLeft.Y)));

            if (radiusX <= 0 || radiusY <= 0)
            {
                // No rounding: a plain axis-aligned clip is cheaper and exactly equivalent.
                _clipStack.Push(ClipEntry.AxisAligned());
                _deviceContext.PushAxisAlignedClip(clip.Rect.ToDirect2D(), AntialiasMode.PerPrimitive);
                return;
            }

            // Direct2D has no native rounded-rect clip. Defer both layer creation and geometry
            // allocation: a following PushOpacity can soft-merge, and compatible solid fills can
            // bake the clip with FillRoundedRectangle — never touching a D2D layer.
            _clipStack.Push(ClipEntry.Deferred(clip, clip.Rect.ToDirect2D()));
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

        private static ID2D1Geometry CreateRoundedRectGeometry(RoundedRect roundedRect)
        {
            // Fast path: uniform corner radii map directly onto D2D's native rounded-rect
            // geometry. Building a path geometry with four arcs is much more expensive and is
            // unnecessary for the common Avalonia RoundedRect(rect, radius) form.
            if (AreRadiiUniform(roundedRect))
            {
                var rounded = new RoundedRectangle
                {
                    Rect = roundedRect.Rect.ToDirect2D(),
                    RadiusX = (float)roundedRect.RadiiTopLeft.X,
                    RadiusY = (float)roundedRect.RadiiTopLeft.Y
                };
                return Direct2D1Platform.Direct2D1Factory.CreateRoundedRectangleGeometry(rounded);
            }

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
            ID2D1Geometry? originalGeometry,
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
                    DrawInsetBoxShadow(roundedRect, originalGeometry!, shadow);
                else
                    DrawOuterBoxShadow(roundedRect, originalGeometry!, shadow);
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
            _pushClips++;
            DrawingContextCallStats.OnPushClip();
            if (region is not Direct2DRegionImpl d2dRegion)
                throw new InvalidOperationException("Region was not created by this Direct2D backend.");

            FlushDeferredClip();

            if (region.IsEmpty)
            {
                _clipStack.Push(ClipEntry.AxisAligned());
                _deviceContext.PushAxisAlignedClip(default, AntialiasMode.PerPrimitive);
                return;
            }

            var geometry = d2dRegion.CreateGeometry();
            var parameters = new LayerParameters
            {
                ContentBounds = Direct2DRegionImpl.ToRect(region.Bounds).ToDirect2D(),
                MaskTransform = Matrix3x2.Identity,
                Opacity = 1,
                GeometricMask = geometry,
                MaskAntialiasMode = AntialiasMode.Aliased
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

            _clipStack.Push(ClipEntry.LayerOwned(geometry, ownsGeometry: true));
        }

        public void PopClip()
        {
            var entry = _clipStack.Pop();
            switch (entry.State)
            {
                case ClipState.AxisAligned:
                    _deviceContext.PopAxisAlignedClip();
                    break;

                case ClipState.LayerOwned:
                    PopLayer();
                    ReleaseClipGeometry(entry);
                    break;

                case ClipState.Deferred:
                    // Never flushed to a layer (e.g. empty push/pop, or only nested no-ops).
                    ReleaseClipGeometry(entry);
                    break;

                case ClipState.MergedIntoOpacity:
                    // Combined layer was already popped by PopOpacity; only the mask remains.
                    ReleaseClipGeometry(entry);
                    break;

                case ClipState.SoftMerged:
                    // Soft clip never pushed a D2D layer; PopOpacity already cleared soft depth.
                    ReleaseClipGeometry(entry);
                    break;

                default:
                    ReleaseClipGeometry(entry);
                    break;
            }
        }

        public void PushLayer(Rect bounds)
        {
            FlushDeferredClip();
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
            FlushDeferredClip();
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
            _pushOpacities++;
            DrawingContextCallStats.OnPushOpacity();
            // Soft-merge path: keep rounded clip + opacity as a soft clip (no D2D layer, no
            // geometry allocation yet). Compatible solid fills bake clip+opacity via native
            // FillRoundedRectangle. Incompatible draws flush to a real layer.
            if (opacity < 1
                && _clipStack.Count > 0
                && _clipStack.Peek().State == ClipState.Deferred)
            {
                var deferred = _clipStack.Pop();
                var contentBounds = deferred.ContentBounds;
                if (bounds is { } opacityBounds && opacityBounds != default(Rect))
                {
                    var left = Math.Max(contentBounds.Left, (float)opacityBounds.X);
                    var top = Math.Max(contentBounds.Top, (float)opacityBounds.Y);
                    var right = Math.Min(contentBounds.Right, (float)opacityBounds.Right);
                    var bottom = Math.Min(contentBounds.Bottom, (float)opacityBounds.Bottom);
                    if (right > left && bottom > top)
                        contentBounds = new RawRectF(left, top, right, bottom);
                }

                _clipStack.Push(ClipEntry.SoftMerged(deferred.ClipShape, contentBounds, (float)opacity));
                _softOpacityDepth++;
                return;
            }

            FlushDeferredClip();

            if (opacity < 1)
            {
                // Standalone soft opacity (no rounded clip): bake into solid fills / simple pens.
                // Nested standalone or soft-merged-active cases fall through to a real layer.
                if (_standaloneSoftOpacity is null && _softOpacityDepth == 0)
                {
                    _standaloneSoftOpacity = (float)opacity;
                    return;
                }

                FlushStandaloneSoftOpacity();

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
                _layerPushed.Push(false);
        }

        public void PopOpacity()
        {
            // Standalone soft opacity closes without a D2D layer (unless flushed mid-scope).
            if (_standaloneSoftOpacity is not null && _softOpacityDepth == 0)
            {
                _standaloneSoftOpacity = null;
                return;
            }

            if (_softOpacityDepth > 0)
            {
                _softOpacityDepth--;
                return;
            }

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
            if (_layerPushed.Pop())
                _deviceContext.PopLayer();
        }

        private void PushDirect2DLayer(LayerParameters parameters)
        {
            // Pass null for the layer resource so Direct2D manages it (D2D 1.1+ recommended path).
            // Creating and pooling ID2D1Layer objects is a D2D 1.0-era pattern; GPU targets also
            // reuse DrawingContextImpl across frames, so a short-lived per-frame layer pool would
            // still not outlive the session that pushed it.
            _deviceContext.PushLayer(parameters, null);
            _layerPushed.Push(true);
            _layerPushes++;
            DrawingContextCallStats.OnLayerPush();
        }

        /// <summary>
        /// Materialises a deferred rounded-rect clip into a real D2D layer. Called before any
        /// draw/state change that is not an opacity merge candidate.
        /// </summary>
        private void FlushDeferredClip()
        {
            // Solid-rect stroke batch must land before clip/layer state changes.
            if (_solidRectBatch.HasDeferredStrokes || _solidRectBatch.IsDeferredSimpleSession)
                FlushPrimitiveBatch();

            // Standalone soft opacity cannot stay deferred across a hard clip/layer boundary.
            FlushStandaloneSoftOpacity();

            if (_clipStack.Count == 0)
                return;

            var top = _clipStack.Peek();
            if (top.State is not (ClipState.Deferred or ClipState.SoftMerged))
                return;

            _clipStack.Pop();
            _deferredClipFlushes++;

            // Soft-merged opacity was tracked via _softOpacityDepth (no D2D layer). Consuming it
            // here means subsequent drawing will use a real layer; drop one soft depth so PopOpacity
            // will not skip a real layer pop.
            if (top.State == ClipState.SoftMerged && _softOpacityDepth > 0)
                _softOpacityDepth--;

            // Geometry is created lazily — soft path often never needs it.
            var geometry = top.Geometry;
            var ownsGeometry = top.OwnsGeometry;
            if (geometry is null)
            {
                geometry = AcquireRoundedClipGeometry(top.ClipShape, out ownsGeometry);
            }

            var parameters = new LayerParameters
            {
                ContentBounds = top.ContentBounds,
                MaskTransform = Matrix3x2.Identity,
                Opacity = top.State == ClipState.SoftMerged ? top.Opacity : 1,
                GeometricMask = geometry,
                MaskAntialiasMode = AntialiasMode.PerPrimitive
            };

            try
            {
                PushDirect2DLayer(parameters);
            }
            catch
            {
                if (ownsGeometry)
                    geometry.Dispose();
                throw;
            }

            if (top.State == ClipState.SoftMerged)
                _clipStack.Push(ClipEntry.MergedIntoOpacity(geometry, ownsGeometry));
            else
                _clipStack.Push(ClipEntry.LayerOwned(geometry, ownsGeometry));
        }

        /// <summary>
        /// Converts a deferred standalone soft opacity into a real D2D opacity layer so subsequent
        /// incompatible draws (or stack changes) remain correct. PopOpacity will PopLayer.
        /// </summary>
        private void FlushStandaloneSoftOpacity()
        {
            if (_standaloneSoftOpacity is not float opacity)
                return;

            _standaloneSoftOpacity = null;
            var bounds = new Rect(0, 0, _renderTarget.PixelSize.Width, _renderTarget.PixelSize.Height);
            var parameters = new LayerParameters
            {
                MaskTransform = Matrix3x2.Identity,
                Opacity = opacity,
                ContentBounds = bounds.ToDirect2D()
            };
            PushDirect2DLayer(parameters);
        }

        /// <summary>
        /// Soft clip / soft opacity: bake solid fills and simple solid pens without a D2D layer.
        /// Uniform-radius shapes use native Fill/DrawRoundedRectangle (no geometry COM object).
        /// </summary>
        private bool TryDrawRectangleWithSoftClip(
            IBrush? brush,
            IPen? pen,
            RoundedRect rrect,
            BoxShadows boxShadow)
        {
            if (boxShadow != default)
            {
                _softPathMisses++;
                DrawingContextCallStats.OnSoftMiss();
                return false;
            }

            var hasSoftClip = _clipStack.Count > 0
                && _clipStack.Peek().State is ClipState.SoftMerged or ClipState.Deferred;
            var hasStandaloneSoft = _standaloneSoftOpacity is not null;

            if (!hasSoftClip && !hasStandaloneSoft)
                return false;

            // Soft path: solid fills and/or simple solid pens only.
            ISolidColorBrush? solidFill = null;
            if (brush is not null)
            {
                if (brush is not ISolidColorBrush fillSolid)
                {
                    _softPathMisses++;
                    DrawingContextCallStats.OnSoftMiss();
                    return false;
                }
                solidFill = fillSolid;
            }

            ISolidColorBrush? solidStroke = null;
            if (pen is not null)
            {
                if (!IsSimpleSolidPen(pen, out solidStroke))
                {
                    _softPathMisses++;
                    DrawingContextCallStats.OnSoftMiss();
                    return false;
                }
            }

            if (solidFill is null && solidStroke is null)
                return false;

            float clipOpacity = 1f;
            RoundedRect clipShape = default;
            RawRectF contentBounds = default;
            var paintClipShape = false;

            if (hasSoftClip)
            {
                var top = _clipStack.Peek();
                clipOpacity = top.State == ClipState.SoftMerged ? top.Opacity : 1f;
                clipShape = top.ClipShape;
                contentBounds = top.ContentBounds;

                // Stroke expands half thickness outside the fill rect.
                var halfStroke = pen is null ? 0.0 : pen.Thickness * 0.5;
                var shape = rrect.Rect;
                if (shape.X - halfStroke + 0.01 < contentBounds.Left
                    || shape.Y - halfStroke + 0.01 < contentBounds.Top
                    || shape.Right + halfStroke - 0.01 > contentBounds.Right
                    || shape.Bottom + halfStroke - 0.01 > contentBounds.Bottom)
                {
                    _softPathMisses++;
                    DrawingContextCallStats.OnSoftMiss();
                    return false;
                }

                // fill == clip → paint clip shape for corner-AA parity with a geometric mask.
                // fill fully interior to rounded clip → paint fill shape (multi-draw under one clip).
                // otherwise fall through to real layer (partial intersection not approximated).
                if (solidFill is not null && pen is null && RectEquals(shape, clipShape.Rect))
                    paintClipShape = true;
                else if (!IsFullyInteriorToRoundedClip(shape, halfStroke, clipShape))
                {
                    _softPathMisses++;
                    DrawingContextCallStats.OnSoftMiss();
                    return false;
                }
            }

            var standalone = _standaloneSoftOpacity ?? 1f;
            var bakedOpacity = clipOpacity * standalone;
            // Soft draws leave the simple-rect session; flush any deferred strokes first.
            FlushPrimitiveBatch();
            _softPathHits++;
            DrawingContextCallStats.OnSoftHit(_diagnosticTargetName);

            if (solidFill is not null)
            {
                var effectiveOpacity = solidFill.Opacity * bakedOpacity;
                if (effectiveOpacity > 0)
                {
                    var fillBrush = _deviceResources.GetOrCreateSolidBrush(
                        _deviceContext,
                        solidFill.Color,
                        effectiveOpacity);

                    if (fillBrush.PlatformBrush is not null)
                    {
                        // Paint clip shape when fill==clip so corner AA matches a geometric mask.
                        FillSoftRounded(paintClipShape ? clipShape : rrect, fillBrush.PlatformBrush, stashOnSoftClip: paintClipShape);
                    }
                }
            }

            if (solidStroke is not null && pen is not null)
            {
                // IPen has no separate Opacity; stroke alpha lives on the brush.
                var effectiveOpacity = solidStroke.Opacity * bakedOpacity;
                if (effectiveOpacity > 0)
                {
                    var strokeBrush = _deviceResources.GetOrCreateSolidBrush(
                        _deviceContext,
                        solidStroke.Color,
                        effectiveOpacity);
                    var strokeStyle = GetOrCreateStrokeStyle(pen);
                    if (strokeBrush.PlatformBrush is not null)
                    {
                        var target = paintClipShape ? clipShape : rrect;
                        StrokeSoftRounded(target, strokeBrush.PlatformBrush, (float)pen.Thickness, strokeStyle);
                    }
                }
            }

            return true;
        }

        private void FillSoftRounded(RoundedRect shape, ID2D1Brush brush, bool stashOnSoftClip)
        {
            if (AreRadiiUniform(shape) && IsZeroRadiusOrSafe(shape))
            {
                var maxR = Math.Max(shape.RadiiTopLeft.X, shape.RadiiTopLeft.Y);
                if (maxR <= 0)
                {
                    _deviceContext.FillRectangle(shape.Rect.ToDirect2D(), brush);
                    return;
                }

                var rounded = new RoundedRectangle
                {
                    Rect = shape.Rect.ToDirect2D(),
                    RadiusX = (float)shape.RadiiTopLeft.X,
                    RadiusY = (float)shape.RadiiTopLeft.Y
                };
                _deviceContext.FillRoundedRectangle(rounded, brush);
                return;
            }

            // Non-uniform corners need a path geometry. When painting the soft clip shape itself,
            // stash geometry on the clip entry so PopClip can release ownership correctly.
            if (stashOnSoftClip
                && _clipStack.Count > 0
                && _clipStack.Peek().State is ClipState.SoftMerged or ClipState.Deferred)
            {
                var top = _clipStack.Peek();
                var geometry = top.Geometry;
                var owns = top.OwnsGeometry;
                if (geometry is null)
                {
                    geometry = AcquireRoundedClipGeometry(shape, out owns);
                    _clipStack.Pop();
                    _clipStack.Push(top.WithGeometry(geometry, owns));
                }
                _deviceContext.FillGeometry(geometry, brush);
                return;
            }

            using var geometryOwned = CreateRoundedRectGeometry(shape);
            _deviceContext.FillGeometry(geometryOwned, brush);
        }

        private void StrokeSoftRounded(
            RoundedRect shape,
            ID2D1Brush brush,
            float thickness,
            ID2D1StrokeStyle? strokeStyle)
        {
            if (AreRadiiUniform(shape) && IsZeroRadiusOrSafe(shape))
            {
                var maxR = Math.Max(shape.RadiiTopLeft.X, shape.RadiiTopLeft.Y);
                if (maxR <= 0)
                {
                    if (strokeStyle is null)
                        _deviceContext.DrawRectangle(shape.Rect.ToDirect2D(), brush, thickness);
                    else
                        _deviceContext.DrawRectangle(shape.Rect.ToDirect2D(), brush, thickness, strokeStyle);
                    return;
                }

                var rounded = new RoundedRectangle
                {
                    Rect = shape.Rect.ToDirect2D(),
                    RadiusX = (float)shape.RadiiTopLeft.X,
                    RadiusY = (float)shape.RadiiTopLeft.Y
                };
                if (strokeStyle is null)
                    _deviceContext.DrawRoundedRectangle(rounded, brush, thickness);
                else
                    _deviceContext.DrawRoundedRectangle(rounded, brush, thickness, strokeStyle);
                return;
            }

            using var geometry = CreateRoundedRectGeometry(shape);
            if (strokeStyle is null)
                _deviceContext.DrawGeometry(geometry, brush, thickness);
            else
                _deviceContext.DrawGeometry(geometry, brush, thickness, strokeStyle);
        }

        private static bool IsSimpleSolidPen(IPen pen, out ISolidColorBrush solidStroke)
        {
            solidStroke = null!;
            if (pen.Brush is not ISolidColorBrush solid)
                return false;
            if (pen.Thickness <= 0)
                return false;
            // Dashed pens need a stroke style path; keep soft path solid-only.
            if (pen.DashStyle is { Dashes.Count: > 0 })
                return false;
            solidStroke = solid;
            return true;
        }

        private static bool RectEquals(Rect a, Rect b)
            => Math.Abs(a.X - b.X) < 0.01
               && Math.Abs(a.Y - b.Y) < 0.01
               && Math.Abs(a.Width - b.Width) < 0.01
               && Math.Abs(a.Height - b.Height) < 0.01;

        /// <summary>
        /// True when <paramref name="shape"/> (optionally expanded by stroke) lies entirely inside
        /// the axis-aligned inset of the rounded clip, so corner AA of the clip never affects the draw.
        /// </summary>
        private static bool IsFullyInteriorToRoundedClip(Rect shape, double halfStroke, RoundedRect clip)
        {
            var maxRadius = Math.Max(
                Math.Max(clip.RadiiTopLeft.X, clip.RadiiTopLeft.Y),
                Math.Max(
                    Math.Max(clip.RadiiTopRight.X, clip.RadiiTopRight.Y),
                    Math.Max(
                        Math.Max(clip.RadiiBottomRight.X, clip.RadiiBottomRight.Y),
                        Math.Max(clip.RadiiBottomLeft.X, clip.RadiiBottomLeft.Y))));

            var inset = maxRadius + halfStroke;
            var cr = clip.Rect;
            return shape.X - halfStroke + 0.01 >= cr.X + inset
                   && shape.Y - halfStroke + 0.01 >= cr.Y + inset
                   && shape.Right + halfStroke - 0.01 <= cr.Right - inset
                   && shape.Bottom + halfStroke - 0.01 <= cr.Bottom - inset;
        }

        private static bool IsZeroRadiusOrSafe(RoundedRect rrect)
        {
            var radiusX = Math.Max(rrect.RadiiTopLeft.X,
                Math.Max(rrect.RadiiTopRight.X, Math.Max(rrect.RadiiBottomRight.X, rrect.RadiiBottomLeft.X)));
            var radiusY = Math.Max(rrect.RadiiTopLeft.Y,
                Math.Max(rrect.RadiiTopRight.Y, Math.Max(rrect.RadiiBottomRight.Y, rrect.RadiiBottomLeft.Y)));
            if (radiusX <= 0 && radiusY <= 0)
                return true;
            return 2 * radiusX <= rrect.Rect.Width + 0.0001
                   && 2 * radiusY <= rrect.Rect.Height + 0.0001;
        }

        private static void ReleaseClipGeometry(in ClipEntry entry)
        {
            if (entry.OwnsGeometry)
                entry.Geometry?.Dispose();
        }

        /// <summary>
        /// Returns a rounded-rect geometry suitable as a clip mask. Prefer a process-wide cache so
        /// repeated clips (and multi-iteration benchmarks / UI frames) reuse the same ID2D1Geometry
        /// instead of rebuilding path/native rounded geometries every time.
        /// </summary>
        private static ID2D1Geometry AcquireRoundedClipGeometry(RoundedRect roundedRect, out bool ownsGeometry)
        {
            var key = RoundedRectGeometryKey.From(roundedRect);
            lock (s_roundedClipGeometryCacheLock)
            {
                if (s_roundedClipGeometryCache.TryGetValue(key, out var cached))
                {
                    ownsGeometry = false;
                    return cached;
                }

                var created = CreateRoundedRectGeometry(roundedRect);
                if (s_roundedClipGeometryCache.Count < MaxRoundedClipGeometryCacheSize)
                {
                    s_roundedClipGeometryCache[key] = created;
                    ownsGeometry = false;
                    return created;
                }

                // Cache full: caller owns and must dispose.
                ownsGeometry = true;
                return created;
            }
        }

        private enum ClipState : byte
        {
            AxisAligned = 0,
            LayerOwned = 1,
            Deferred = 2,
            MergedIntoOpacity = 3,
            SoftMerged = 4,
        }

        private readonly struct ClipEntry
        {
            public ClipState State { get; }
            public ID2D1Geometry? Geometry { get; }
            public RoundedRect ClipShape { get; }
            public RawRectF ContentBounds { get; }
            public bool OwnsGeometry { get; }
            public float Opacity { get; }

            private ClipEntry(
                ClipState state,
                ID2D1Geometry? geometry,
                RoundedRect clipShape,
                RawRectF contentBounds,
                bool ownsGeometry,
                float opacity)
            {
                State = state;
                Geometry = geometry;
                ClipShape = clipShape;
                ContentBounds = contentBounds;
                OwnsGeometry = ownsGeometry;
                Opacity = opacity;
            }

            public static ClipEntry AxisAligned()
                => new(ClipState.AxisAligned, null, default, default, ownsGeometry: false, opacity: 1);

            public static ClipEntry LayerOwned(ID2D1Geometry geometry, bool ownsGeometry)
                => new(ClipState.LayerOwned, geometry, default, default, ownsGeometry, opacity: 1);

            public static ClipEntry Deferred(RoundedRect clipShape, RawRectF contentBounds)
                => new(ClipState.Deferred, null, clipShape, contentBounds, ownsGeometry: false, opacity: 1);

            public static ClipEntry MergedIntoOpacity(ID2D1Geometry geometry, bool ownsGeometry)
                => new(ClipState.MergedIntoOpacity, geometry, default, default, ownsGeometry, opacity: 1);

            public static ClipEntry SoftMerged(RoundedRect clipShape, RawRectF contentBounds, float opacity)
                => new(ClipState.SoftMerged, null, clipShape, contentBounds, ownsGeometry: false, opacity);

            public ClipEntry WithGeometry(ID2D1Geometry geometry, bool ownsGeometry)
                => new(State, geometry, ClipShape, ContentBounds, ownsGeometry, Opacity);
        }

        // Process-wide cache of rounded clip geometries. Keys are quantized to avoid float drift.
        // Entries are never disposed while the process lives (bounded by MaxRoundedClipGeometryCacheSize).
        private static readonly object s_roundedClipGeometryCacheLock = new();
        private static readonly Dictionary<RoundedRectGeometryKey, ID2D1Geometry> s_roundedClipGeometryCache = new();
        private const int MaxRoundedClipGeometryCacheSize = 512;

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
                return GetOrCreateSolidBrush(solidColorBrush);
            }
            else if (linearGradientBrush != null)
            {
                return new LinearGradientBrushImpl(linearGradientBrush, _deviceContext, destinationRect, _deviceResources);
            }
            else if (radialGradientBrush != null)
            {
                return new RadialGradientBrushImpl(radialGradientBrush, _deviceContext, destinationRect, _deviceResources);
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

            return GetOrCreateSolidBrush(null);
        }

        /// <summary>
        /// Returns a device-cached <see cref="SolidColorBrushImpl"/> for the given color+opacity.
        /// Owned by <see cref="D2DDeviceResourceCache"/> for the render target's lifetime.
        /// </summary>
        private SolidColorBrushImpl GetOrCreateSolidBrush(ISolidColorBrush? brush)
        {
            var color = brush?.Color ?? default;
            var opacity = brush?.Opacity ?? 1.0;
            return _deviceResources.GetOrCreateSolidBrush(_deviceContext, color, opacity);
        }

        /// <summary>
        /// Returns a device-cached <see cref="ID2D1StrokeStyle"/> for the given pen.
        /// Stroke styles are immutable factory resources shared across frames.
        /// </summary>
        private ID2D1StrokeStyle? GetOrCreateStrokeStyle(IPen? pen)
        {
            if (pen is null)
                return null;

            return _deviceResources.GetOrCreateStrokeStyle(pen, _deviceContext);
        }

        public void PushGeometryClip(IGeometryImpl clip)
        {
            FlushDeferredClip();
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
            FlushDeferredClip();
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
        }

        private TextAntialiasMode GetEffectiveTextAntialiasMode()
        {
            var textRenderingMode = TextOptions.TextRenderingMode;

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

    /// <summary>
    /// Quantized key for caching rounded-rectangle clip geometries across frames.
    /// </summary>
    internal readonly struct RoundedRectGeometryKey : IEquatable<RoundedRectGeometryKey>
    {
        // 1/64 px quantization — stable for layout pixel values while collapsing float noise.
        private const double Scale = 64.0;

        private readonly long _x;
        private readonly long _y;
        private readonly long _w;
        private readonly long _h;
        private readonly int _tlX;
        private readonly int _tlY;
        private readonly int _trX;
        private readonly int _trY;
        private readonly int _brX;
        private readonly int _brY;
        private readonly int _blX;
        private readonly int _blY;

        private RoundedRectGeometryKey(
            long x, long y, long w, long h,
            int tlX, int tlY, int trX, int trY,
            int brX, int brY, int blX, int blY)
        {
            _x = x; _y = y; _w = w; _h = h;
            _tlX = tlX; _tlY = tlY; _trX = trX; _trY = trY;
            _brX = brX; _brY = brY; _blX = blX; _blY = blY;
        }

        public static RoundedRectGeometryKey From(RoundedRect roundedRect)
        {
            static long Q(double v) => (long)Math.Round(v * Scale);
            static int Qi(double v) => (int)Math.Round(v * Scale);

            var r = roundedRect.Rect;
            return new RoundedRectGeometryKey(
                Q(r.X), Q(r.Y), Q(r.Width), Q(r.Height),
                Qi(roundedRect.RadiiTopLeft.X), Qi(roundedRect.RadiiTopLeft.Y),
                Qi(roundedRect.RadiiTopRight.X), Qi(roundedRect.RadiiTopRight.Y),
                Qi(roundedRect.RadiiBottomRight.X), Qi(roundedRect.RadiiBottomRight.Y),
                Qi(roundedRect.RadiiBottomLeft.X), Qi(roundedRect.RadiiBottomLeft.Y));
        }

        public bool Equals(RoundedRectGeometryKey other) =>
            _x == other._x && _y == other._y && _w == other._w && _h == other._h
            && _tlX == other._tlX && _tlY == other._tlY
            && _trX == other._trX && _trY == other._trY
            && _brX == other._brX && _brY == other._brY
            && _blX == other._blX && _blY == other._blY;

        public override bool Equals(object? obj) => obj is RoundedRectGeometryKey other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(_x); hash.Add(_y); hash.Add(_w); hash.Add(_h);
            hash.Add(_tlX); hash.Add(_tlY); hash.Add(_trX); hash.Add(_trY);
            hash.Add(_brX); hash.Add(_brY); hash.Add(_blX); hash.Add(_blY);
            return hash.ToHashCode();
        }
    }

    /// <summary>
    /// A composite key for caching solid-color D2D brushes by color and opacity.
    /// </summary>
    internal readonly struct SolidBrushKey : IEquatable<SolidBrushKey>
    {
        // ARGB in the upper 32 bits, quantized opacity in the lower 32 bits. Kept as
        // distinct halves of a 64-bit value so no two (color, opacity) pairs can collide.
        private readonly ulong _colorAndOpacity;

        public SolidBrushKey(Color color, double opacity)
        {
            var argb = (uint)((color.A << 24) | (color.R << 16) | (color.G << 8) | color.B);
            // Quantize opacity to 1/65535 resolution to avoid floating-point key drift.
            var op = (uint)Math.Clamp(Math.Round(opacity * 65535.0), 0, 65535);
            _colorAndOpacity = ((ulong)argb << 32) | op;
        }

        public bool Equals(SolidBrushKey other) => _colorAndOpacity == other._colorAndOpacity;
        public override bool Equals(object? obj) => obj is SolidBrushKey other && Equals(other);
        public override int GetHashCode() => _colorAndOpacity.GetHashCode();
    }

    /// <summary>
    /// A composite key for caching D2D stroke styles by pen properties.
    /// Stroke styles are immutable device resources identified by their cap/join/dash properties.
    /// </summary>
    internal readonly struct StrokeStyleKey : IEquatable<StrokeStyleKey>
    {
        private readonly PenLineCap _lineCap;
        private readonly PenLineJoin _lineJoin;
        private readonly float _miterLimit;
        private readonly Vortice.Direct2D1.DashStyle _dashStyle;
        private readonly float _dashOffset;
        private readonly int _dashHash;

        public StrokeStyleKey(IPen pen)
        {
            _lineCap = pen.LineCap;
            _lineJoin = pen.LineJoin;
            _miterLimit = (float)pen.MiterLimit;

            var dashStyle = pen.DashStyle;
            if (dashStyle?.Dashes is { Count: > 0 } dashes)
            {
                _dashStyle = Vortice.Direct2D1.DashStyle.Custom;
                _dashOffset = (float)dashStyle.Offset;
                // Hash the dash array values
                var hash = 17;
                for (var i = 0; i < dashes.Count; i++)
                    hash = (hash * 31) + ((float)dashes[i]).GetHashCode();
                _dashHash = hash;
            }
            else
            {
                _dashStyle = Vortice.Direct2D1.DashStyle.Solid;
                _dashOffset = 0;
                _dashHash = 0;
            }
        }

        public bool Equals(StrokeStyleKey other) =>
            _lineCap == other._lineCap
            && _lineJoin == other._lineJoin
            && _miterLimit == other._miterLimit
            && _dashStyle == other._dashStyle
            && _dashOffset == other._dashOffset
            && _dashHash == other._dashHash;

        public override bool Equals(object? obj) => obj is StrokeStyleKey other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(_lineCap, _lineJoin, _miterLimit, _dashStyle, _dashOffset, _dashHash);
    }
}
