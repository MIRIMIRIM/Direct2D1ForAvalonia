using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Vortice.Direct2D1;

namespace MIR.Direct2D1ForAvalonia.Media;

/// <summary>
/// A cache of device-dependent D2D resources that outlives a single frame.
/// <para>
/// GPU targets share one cache per <em>native</em> <c>ID2D1Device</c> pointer (not the COM RCW).
/// Each <see cref="For"/> that resolves a device takes a lease; when the last lease is released
/// the cache is unregistered and native resources are disposed. Device loss via
/// <see cref="InvalidateForDevice"/> drops the entry regardless of leases.
/// </para>
/// <para>
/// Classic WIC / factory render targets with no device association keep a per-render-target cache
/// (no lease; lifetime follows the RT via ConditionalWeakTable).
/// </para>
/// <para>
/// <b>Thread safety model:</b> dictionary mutations and stats are guarded by a per-cache lock.
/// Command lists are returned via QI (borrower-owned RCW). Solid brushes, stroke styles, and
/// gradient stops are returned as <b>cache-owned</b> references valid for the duration of the
/// current draw call on the calling thread — do not hold them across frames or across
/// <see cref="InvalidateForDevice"/>. LRU eviction drops dictionary entries without disposing
/// native objects that may still be referenced by deferred batches; full disposal happens on
/// cache teardown (last lease or device invalidation).
/// </para>
/// </summary>
internal sealed class D2DDeviceResourceCache
{
    // Fallback for non-device-context targets (e.g. pure WIC RT without a device association).
    private static readonly ConditionalWeakTable<ID2D1RenderTarget, D2DDeviceResourceCache> s_renderTargetCaches = new();

    // Native ID2D1Device* → cache. IntPtr identity is stable across RCW instances.
    private static readonly object s_deviceTableLock = new();
    private static readonly Dictionary<IntPtr, D2DDeviceResourceCache> s_deviceCaches = new();

    private const int MaxCommandLists = 48;
    private const int MaxSolidBrushes = 512;
    private const int MaxStrokeStyles = 128;
    private const int MaxLinearGradientBrushes = 128;
    private const int MaxRadialGradientBrushes = 128;

    // Per-cache gate: protects dictionaries, LRU, stats, and native disposal.
    private readonly object _gate = new();
    // Zero for WIC/per-RT caches; non-zero for native-device-scoped caches.
    private readonly IntPtr _nativeDevice;
    private int _leaseCount;

    // Gradient stop collections are intentionally uncapped: they are low-cardinality (bounded by
    // the app's brush definitions, not by per-frame colors) and D2D brushes AddRef them, so we
    // cannot safely evict without risking a use-after-free on a brush that still references the
    // collection. Solid brushes and stroke styles, by contrast, are owned solely by this cache.
    private readonly Dictionary<GradientStopKey, ID2D1GradientStopCollection> _gradientStops = new();
    private readonly Dictionary<SolidBrushKey, SolidColorBrushImpl> _solidBrushes = new();
    private readonly LinkedList<SolidBrushKey> _solidBrushLru = new();
    private readonly Dictionary<SolidBrushKey, LinkedListNode<SolidBrushKey>> _solidBrushLruNodes = new();
    private readonly Dictionary<StrokeStyleKey, ID2D1StrokeStyle> _strokeStyles = new();
    private readonly LinkedList<StrokeStyleKey> _strokeStyleLru = new();
    private readonly Dictionary<StrokeStyleKey, LinkedListNode<StrokeStyleKey>> _strokeStyleLruNodes = new();
    // Simple solid-rect session command lists, shared across all DCs on this native device so
    // composition intermediate targets (new bitmap RT / new DrawingContextImpl each dirty
    // paint) still hit steady-state DrawImage replay.
    private readonly Dictionary<CommandListKey, CommandListEntry> _commandLists = new();
    private readonly LinkedList<CommandListKey> _commandListLru = new();
    private readonly Dictionary<CommandListKey, LinkedListNode<CommandListKey>> _commandListLruNodes = new();
    private readonly Dictionary<LinearGradientBrushKey, ID2D1LinearGradientBrush> _linearGradients = new();
    private readonly LinkedList<LinearGradientBrushKey> _linearGradientLru = new();
    private readonly Dictionary<LinearGradientBrushKey, LinkedListNode<LinearGradientBrushKey>> _linearGradientLruNodes = new();
    private readonly Dictionary<RadialGradientBrushKey, ID2D1RadialGradientBrush> _radialGradients = new();
    private readonly LinkedList<RadialGradientBrushKey> _radialGradientLru = new();
    private readonly Dictionary<RadialGradientBrushKey, LinkedListNode<RadialGradientBrushKey>> _radialGradientLruNodes = new();
    private int _commandListHits;
    private int _commandListStores;
    private int _linearGradientHits;
    private int _linearGradientCreates;
    private int _radialGradientHits;
    private int _radialGradientCreates;

    /// <summary>Device-scoped command-list lookup hits (steady intermediate repaints).</summary>
    public int CommandListHits
    {
        get { lock (_gate) return _commandListHits; }
    }

    /// <summary>Device-scoped command-list stores (first paint / content change).</summary>
    public int CommandListStores
    {
        get { lock (_gate) return _commandListStores; }
    }

    public void ResetCommandListStats()
    {
        lock (_gate)
        {
            _commandListHits = 0;
            _commandListStores = 0;
        }
    }

    private D2DDeviceResourceCache(IntPtr nativeDevice)
    {
        _nativeDevice = nativeDevice;
    }

    public static D2DDeviceResourceCache For(ID2D1RenderTarget renderTarget)
    {
        // Resolve the native D2D device pointer when possible (device context or QI from
        // ID2D1BitmapRenderTarget / compatible intermediate). Same native device → same cache.
        if (TryGetNativeDevicePointer(renderTarget, out var nativeDevice) && nativeDevice != IntPtr.Zero)
        {
            lock (s_deviceTableLock)
            {
                if (!s_deviceCaches.TryGetValue(nativeDevice, out var cache))
                {
                    cache = new D2DDeviceResourceCache(nativeDevice);
                    s_deviceCaches[nativeDevice] = cache;
                }

                cache._leaseCount++;
                return cache;
            }
        }

        // Classic WIC / factory RTs with no device: still per-render-target (no lease).
        return s_renderTargetCaches.GetValue(renderTarget, static _ => new D2DDeviceResourceCache(IntPtr.Zero));
    }

    /// <summary>
    /// Drops one lease taken by <see cref="For"/>. When the last lease on a native-device cache
    /// is released, the cache is unregistered and native resources are disposed.
    /// </summary>
    public void ReleaseLease()
    {
        if (_nativeDevice == IntPtr.Zero)
            return;

        var shouldTeardown = false;
        lock (s_deviceTableLock)
        {
            if (_leaseCount > 0)
                _leaseCount--;

            if (_leaseCount != 0)
                return;

            if (s_deviceCaches.TryGetValue(_nativeDevice, out var registered)
                && ReferenceEquals(registered, this))
            {
                s_deviceCaches.Remove(_nativeDevice);
                shouldTeardown = true;
            }
        }

        if (shouldTeardown)
            ReleaseNativeResources();
    }

    /// <summary>
    /// Drops the cache for the given native device. Call on D2DERR_RECREATE_TARGET / device loss.
    /// When <paramref name="device"/> is null, no-ops (does not wipe other devices' caches).
    /// </summary>
    public static void InvalidateForDevice(ID2D1Device? device)
    {
        if (device is null)
            return;

        var native = device.NativePointer;
        if (native == IntPtr.Zero)
            return;

        D2DDeviceResourceCache? cache;
        lock (s_deviceTableLock)
        {
            if (s_deviceCaches.Remove(native, out cache))
                cache._leaseCount = 0;
        }

        cache?.ReleaseNativeResources();
        // Compatible composition layers share the same device; drop them on loss too.
        Imaging.D2DCompatibleLayerPool.Clear();
    }

    /// <summary>
    /// Tries to obtain the native <c>ID2D1Device*</c> for a render target. Works for
    /// <see cref="ID2D1DeviceContext"/> and for compatible bitmap RTs that QI to a device context.
    /// </summary>
    private static bool TryGetNativeDevicePointer(ID2D1RenderTarget renderTarget, out IntPtr nativeDevice)
    {
        nativeDevice = IntPtr.Zero;

        try
        {
            if (renderTarget is ID2D1DeviceContext dc)
            {
                // dc.Device returns a new RCW each time; use NativePointer only, then dispose RCW.
                var device = dc.Device;
                if (device is null)
                    return false;
                try
                {
                    nativeDevice = device.NativePointer;
                    return nativeDevice != IntPtr.Zero;
                }
                finally
                {
                    device.Dispose();
                }
            }

            // Composition intermediate: ID2D1BitmapRenderTarget → QI DeviceContext → Device.
            using var qi = renderTarget.QueryInterfaceOrNull<ID2D1DeviceContext>();
            if (qi is null)
                return false;

            var qiDevice = qi.Device;
            if (qiDevice is null)
                return false;
            try
            {
                nativeDevice = qiDevice.NativePointer;
                return nativeDevice != IntPtr.Zero;
            }
            finally
            {
                qiDevice.Dispose();
            }
        }
        catch
        {
            nativeDevice = IntPtr.Zero;
            return false;
        }
    }

    /// <summary>
    /// Disposes native resources held by this cache. Safe to call once after removal from the
    /// device table.
    /// </summary>
    private void ReleaseNativeResources()
    {
        lock (_gate)
        {
            foreach (var entry in _commandLists.Values)
                entry.CommandList.Dispose();
            _commandLists.Clear();
            _commandListLru.Clear();
            _commandListLruNodes.Clear();

            foreach (var brush in _solidBrushes.Values)
                brush.ForceReleaseNative();
            _solidBrushes.Clear();
            _solidBrushLru.Clear();
            _solidBrushLruNodes.Clear();

            foreach (var style in _strokeStyles.Values)
                style.Dispose();
            _strokeStyles.Clear();
            _strokeStyleLru.Clear();
            _strokeStyleLruNodes.Clear();

            foreach (var stops in _gradientStops.Values)
                stops.Dispose();
            _gradientStops.Clear();

            foreach (var brush in _linearGradients.Values)
                brush.Dispose();
            _linearGradients.Clear();
            _linearGradientLru.Clear();
            _linearGradientLruNodes.Clear();

            foreach (var brush in _radialGradients.Values)
                brush.Dispose();
            _radialGradients.Clear();
            _radialGradientLru.Clear();
            _radialGradientLruNodes.Clear();
        }
    }

    /// <summary>
    /// Device-scoped linear gradient brush cache hits (template UI / GradientFill steady state).
    /// </summary>
    public int LinearGradientHits
    {
        get { lock (_gate) return _linearGradientHits; }
    }

    /// <summary>
    /// Device-scoped linear gradient brush creations.
    /// </summary>
    public int LinearGradientCreates
    {
        get { lock (_gate) return _linearGradientCreates; }
    }

    /// <summary>Device-scoped radial gradient brush cache hits.</summary>
    public int RadialGradientHits
    {
        get { lock (_gate) return _radialGradientHits; }
    }

    /// <summary>Device-scoped radial gradient brush creations.</summary>
    public int RadialGradientCreates
    {
        get { lock (_gate) return _radialGradientCreates; }
    }

    /// <summary>
    /// Returns a cached <see cref="ID2D1LinearGradientBrush"/> for the given brush+destination.
    /// Owned by the cache — do not dispose.
    /// </summary>
    public ID2D1LinearGradientBrush GetOrCreateLinearGradientBrush(
        ID2D1RenderTarget target,
        ILinearGradientBrush brush,
        Rect destinationRect)
    {
        var startPoint = brush.StartPoint.ToPixels(destinationRect);
        var endPoint = brush.EndPoint.ToPixels(destinationRect);
        var transform = BrushTransform.Apply(brush, destinationRect, Matrix.Identity);
        var key = new LinearGradientBrushKey(
            brush.GradientStops,
            brush.SpreadMethod,
            startPoint,
            endPoint,
            brush.Opacity,
            transform);

        lock (_gate)
        {
            if (_linearGradients.TryGetValue(key, out var cached))
            {
                if (_linearGradients.Count > MaxLinearGradientBrushes * 3 / 4)
                    TouchLinearGradient(key);
                _linearGradientHits++;
                return cached;
            }

            while (_linearGradients.Count >= MaxLinearGradientBrushes && _linearGradientLru.Count > 0)
            {
                var oldestKey = _linearGradientLru.First!.Value;
                if (_linearGradients.Remove(oldestKey, out var oldBrush))
                    oldBrush.Dispose();
                if (_linearGradientLruNodes.TryGetValue(oldestKey, out var node))
                {
                    _linearGradientLru.Remove(node);
                    _linearGradientLruNodes.Remove(oldestKey);
                }
            }

            var stops = GetOrCreateGradientStopsUnlocked(target, brush.GradientStops, brush.SpreadMethod);
            var created = target.CreateLinearGradientBrush(
                new LinearGradientBrushProperties
                {
                    StartPoint = startPoint.ToVortice(),
                    EndPoint = endPoint.ToVortice()
                },
                new BrushProperties
                {
                    Opacity = (float)brush.Opacity,
                    Transform = transform.ToDirect2D(),
                },
                stops);

            _linearGradients[key] = created;
            _linearGradientLruNodes[key] = _linearGradientLru.AddLast(key);
            _linearGradientCreates++;
            return created;
        }
    }

    private void TouchLinearGradient(LinearGradientBrushKey key)
    {
        if (!_linearGradientLruNodes.TryGetValue(key, out var node))
            return;
        _linearGradientLru.Remove(node);
        _linearGradientLruNodes[key] = _linearGradientLru.AddLast(key);
    }

    /// <summary>
    /// Returns a cached <see cref="ID2D1RadialGradientBrush"/> for the given brush+destination.
    /// Owned by the cache — do not dispose.
    /// </summary>
    public ID2D1RadialGradientBrush GetOrCreateRadialGradientBrush(
        ID2D1RenderTarget target,
        IRadialGradientBrush brush,
        Rect destinationRect)
    {
        var centerPoint = brush.Center.ToPixels(destinationRect);
        var gradientOrigin = brush.GradientOrigin.ToPixels(destinationRect) - centerPoint;
        var radiusX = brush.RadiusX.ToValue(destinationRect.Width);
        var radiusY = brush.RadiusY.ToValue(destinationRect.Height);
        var transform = BrushTransform.Apply(brush, destinationRect, Matrix.Identity);
        var key = new RadialGradientBrushKey(
            brush.GradientStops,
            brush.SpreadMethod,
            centerPoint,
            gradientOrigin,
            radiusX,
            radiusY,
            brush.Opacity,
            transform);

        lock (_gate)
        {
            if (_radialGradients.TryGetValue(key, out var cached))
            {
                if (_radialGradients.Count > MaxRadialGradientBrushes * 3 / 4)
                    TouchRadialGradient(key);
                _radialGradientHits++;
                return cached;
            }

            while (_radialGradients.Count >= MaxRadialGradientBrushes && _radialGradientLru.Count > 0)
            {
                var oldestKey = _radialGradientLru.First!.Value;
                if (_radialGradients.Remove(oldestKey, out var oldBrush))
                    oldBrush.Dispose();
                if (_radialGradientLruNodes.TryGetValue(oldestKey, out var node))
                {
                    _radialGradientLru.Remove(node);
                    _radialGradientLruNodes.Remove(oldestKey);
                }
            }

            var stops = GetOrCreateGradientStopsUnlocked(target, brush.GradientStops, brush.SpreadMethod);
            var created = target.CreateRadialGradientBrush(
                new RadialGradientBrushProperties
                {
                    Center = centerPoint.ToVortice(),
                    GradientOriginOffset = gradientOrigin.ToVortice(),
                    RadiusX = (float)radiusX,
                    RadiusY = (float)radiusY
                },
                new BrushProperties
                {
                    Opacity = (float)brush.Opacity,
                    Transform = transform.ToDirect2D(),
                },
                stops);

            _radialGradients[key] = created;
            _radialGradientLruNodes[key] = _radialGradientLru.AddLast(key);
            _radialGradientCreates++;
            return created;
        }
    }

    private void TouchRadialGradient(RadialGradientBrushKey key)
    {
        if (!_radialGradientLruNodes.TryGetValue(key, out var node))
            return;
        _radialGradientLru.Remove(node);
        _radialGradientLruNodes[key] = _radialGradientLru.AddLast(key);
    }

    /// <summary>
    /// Returns a cached <see cref="ID2D1GradientStopCollection"/> for the given stops and spread
    /// method, creating one on first use. The returned collection is owned by the cache — callers
    /// must not dispose it.
    /// </summary>
    public ID2D1GradientStopCollection GetOrCreateGradientStops(
        ID2D1RenderTarget target,
        IReadOnlyList<IGradientStop> stops,
        GradientSpreadMethod spreadMethod)
    {
        lock (_gate)
            return GetOrCreateGradientStopsUnlocked(target, stops, spreadMethod);
    }

    private ID2D1GradientStopCollection GetOrCreateGradientStopsUnlocked(
        ID2D1RenderTarget target,
        IReadOnlyList<IGradientStop> stops,
        GradientSpreadMethod spreadMethod)
    {
        var key = new GradientStopKey(stops, spreadMethod);
        if (_gradientStops.TryGetValue(key, out var cached))
            return cached;

        var d2dStops = new Vortice.Direct2D1.GradientStop[stops.Count];
        for (var i = 0; i < stops.Count; i++)
        {
            d2dStops[i] = new Vortice.Direct2D1.GradientStop
            {
                Color = stops[i].Color.ToDirect2D(),
                Position = (float)stops[i].Offset
            };
        }

        var collection = target.CreateGradientStopCollection(d2dStops, spreadMethod.ToDirect2D());
        _gradientStops[key] = collection;
        return collection;
    }

    /// <summary>
    /// Returns a cached solid-color brush for the given color+opacity. Owned by the cache —
    /// callers must not dispose it (SolidColorBrushImpl.IsCached is set).
    /// </summary>
    public SolidColorBrushImpl GetOrCreateSolidBrush(ID2D1RenderTarget target, Color color, double opacity)
    {
        var key = new SolidBrushKey(color, opacity);
        lock (_gate)
        {
            if (_solidBrushes.TryGetValue(key, out var cached))
            {
                if (_solidBrushes.Count > MaxSolidBrushes * 3 / 4)
                    TouchSolidBrush(key);
                return cached;
            }

            // Drop dictionary entry only — do not ForceReleaseNative. Callers (and deferred
            // paths) may still hold the SolidColorBrushImpl wrapper; zeroing PlatformBrush
            // under them would create a use-after-free. Native COM is released when the last
            // managed RCW is collected or when ReleaseNativeResources runs.
            while (_solidBrushes.Count >= MaxSolidBrushes && _solidBrushLru.Count > 0)
            {
                var oldestKey = _solidBrushLru.First!.Value;
                _solidBrushes.Remove(oldestKey);
                if (_solidBrushLruNodes.TryGetValue(oldestKey, out var node))
                {
                    _solidBrushLru.Remove(node);
                    _solidBrushLruNodes.Remove(oldestKey);
                }
            }

            var impl = new SolidColorBrushImpl(color, opacity, target)
            {
                IsCached = true
            };
            _solidBrushes[key] = impl;
            _solidBrushLruNodes[key] = _solidBrushLru.AddLast(key);
            return impl;
        }
    }

    /// <summary>
    /// Returns a cached stroke style for the given pen properties, or <c>null</c> when the pen
    /// matches D2D's default stroke. Non-null styles are owned by the cache — do not dispose.
    /// </summary>
    public ID2D1StrokeStyle? GetOrCreateStrokeStyle(IPen pen, ID2D1RenderTarget target)
    {
        if (IsDefaultSolidStroke(pen))
            return null;

        var key = new StrokeStyleKey(pen);
        lock (_gate)
        {
            if (_strokeStyles.TryGetValue(key, out var cached))
            {
                if (_strokeStyles.Count > MaxStrokeStyles * 3 / 4)
                    TouchStrokeStyle(key);
                return cached;
            }

            // Drop dictionary entry only — do not Dispose. SolidRectBatch / line / ellipse
            // deferred ops hold the same cache-owned RCW until flush; disposing here is a
            // deterministic use-after-free when a session exceeds MaxStrokeStyles styles.
            while (_strokeStyles.Count >= MaxStrokeStyles && _strokeStyleLru.Count > 0)
            {
                var oldestKey = _strokeStyleLru.First!.Value;
                _strokeStyles.Remove(oldestKey);
                if (_strokeStyleLruNodes.TryGetValue(oldestKey, out var node))
                {
                    _strokeStyleLru.Remove(node);
                    _strokeStyleLruNodes.Remove(oldestKey);
                }
            }

            var style = pen.ToDirect2DStrokeStyle(target);
            _strokeStyles[key] = style;
            _strokeStyleLruNodes[key] = _strokeStyleLru.AddLast(key);
            return style;
        }
    }

    /// <summary>
    /// True when D2D's implicit default stroke style matches the pen (no COM object needed).
    /// </summary>
    public static bool IsDefaultSolidStroke(IPen pen)
    {
        if (pen.DashStyle is { Dashes.Count: > 0 })
            return false;
        if (pen.LineCap != PenLineCap.Flat)
            return false;
        if (pen.LineJoin != PenLineJoin.Miter)
            return false;
        return Math.Abs(pen.MiterLimit - 10.0) < 0.001;
    }

    /// <summary>
    /// Looks up a previously recorded simple-session command list. On hit, returns a
    /// <em>new</em> RCW via <c>QueryInterface</c> so the caller may use it while the cache
    /// continues to own (and may later dispose) the stored list. Caller <b>must</b>
    /// <see cref="IDisposable.Dispose"/> the returned list after drawing.
    /// </summary>
    public bool TryGetCommandList(
        long contentHash,
        int pixelWidth,
        int pixelHeight,
        out ID2D1CommandList commandList)
    {
        var key = new CommandListKey(contentHash, pixelWidth, pixelHeight);
        lock (_gate)
        {
            if (_commandLists.TryGetValue(key, out var entry))
            {
                TouchCommandList(key);
                _commandListHits++;
                // Separate RCW + AddRef so concurrent eviction Dispose cannot zero the caller's
                // NativePointer while DrawImage is in flight on another thread.
                commandList = entry.CommandList.QueryInterface<ID2D1CommandList>();
                return true;
            }
        }

        commandList = null!;
        return false;
    }

    /// <summary>
    /// Stores a command list for cross-context replay. Takes ownership of
    /// <paramref name="commandList"/>.
    /// </summary>
    public void StoreCommandList(
        long contentHash,
        int pixelWidth,
        int pixelHeight,
        ID2D1CommandList commandList)
    {
        var key = new CommandListKey(contentHash, pixelWidth, pixelHeight);
        var entry = new CommandListEntry(commandList);

        lock (_gate)
        {
            if (_commandLists.TryGetValue(key, out var existing))
            {
                if (!ReferenceEquals(existing.CommandList, commandList))
                    existing.CommandList.Dispose();
                _commandLists[key] = entry;
                TouchCommandList(key);
                _commandListStores++;
                return;
            }

            while (_commandLists.Count >= MaxCommandLists && _commandListLru.Count > 0)
            {
                var oldestKey = _commandListLru.First!.Value;
                if (_commandLists.TryGetValue(oldestKey, out var oldestEntry))
                    oldestEntry.CommandList.Dispose();
                EvictCommandList(oldestKey);
            }

            _commandLists[key] = entry;
            _commandListLruNodes[key] = _commandListLru.AddLast(key);
            _commandListStores++;
        }
    }

    private void TouchCommandList(CommandListKey key)
    {
        if (_commandListLruNodes.TryGetValue(key, out var node))
        {
            _commandListLru.Remove(node);
            _commandListLruNodes[key] = _commandListLru.AddLast(key);
        }
    }

    private void TouchSolidBrush(SolidBrushKey key)
    {
        if (_solidBrushLruNodes.TryGetValue(key, out var node))
        {
            _solidBrushLru.Remove(node);
            _solidBrushLruNodes[key] = _solidBrushLru.AddLast(key);
        }
    }

    private void TouchStrokeStyle(StrokeStyleKey key)
    {
        if (_strokeStyleLruNodes.TryGetValue(key, out var node))
        {
            _strokeStyleLru.Remove(node);
            _strokeStyleLruNodes[key] = _strokeStyleLru.AddLast(key);
        }
    }

    private void EvictCommandList(CommandListKey key)
    {
        _commandLists.Remove(key);
        if (_commandListLruNodes.TryGetValue(key, out var node))
        {
            _commandListLru.Remove(node);
            _commandListLruNodes.Remove(key);
        }
    }
}

/// <summary>Key for device-scoped simple-session command lists (content + target size).</summary>
internal readonly struct CommandListKey : IEquatable<CommandListKey>
{
    private readonly long _hash;
    private readonly int _width;
    private readonly int _height;

    public CommandListKey(long contentHash, int pixelWidth, int pixelHeight)
    {
        _hash = contentHash;
        _width = pixelWidth;
        _height = pixelHeight;
    }

    public bool Equals(CommandListKey other)
        => _hash == other._hash && _width == other._width && _height == other._height;

    public override bool Equals(object? obj) => obj is CommandListKey other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_hash, _width, _height);
}

/// <summary>
/// A cached command list keyed by a 64-bit content fingerprint and target dimensions.
/// </summary>
internal readonly struct CommandListEntry
{
    public CommandListEntry(ID2D1CommandList commandList)
    {
        CommandList = commandList;
    }

    public ID2D1CommandList CommandList { get; }
}

/// <summary>
/// Key for a fully materialised radial gradient brush (stops + ellipse geometry in dest pixels).
/// </summary>
internal readonly struct RadialGradientBrushKey : IEquatable<RadialGradientBrushKey>
{
    private readonly GradientStopKey _stops;
    private readonly float _cx, _cy, _ox, _oy, _rx, _ry;
    private readonly float _opacity;
    private readonly float _m11, _m12, _m21, _m22, _m31, _m32;
    private readonly int _hash;

    public RadialGradientBrushKey(
        IReadOnlyList<IGradientStop> stops,
        GradientSpreadMethod spreadMethod,
        Point center,
        Vector originOffset,
        double radiusX,
        double radiusY,
        double opacity,
        Matrix transform)
    {
        _stops = new GradientStopKey(stops, spreadMethod);
        _cx = (float)center.X;
        _cy = (float)center.Y;
        _ox = (float)originOffset.X;
        _oy = (float)originOffset.Y;
        _rx = (float)radiusX;
        _ry = (float)radiusY;
        _opacity = (float)opacity;
        _m11 = (float)transform.M11;
        _m12 = (float)transform.M12;
        _m21 = (float)transform.M21;
        _m22 = (float)transform.M22;
        _m31 = (float)transform.M31;
        _m32 = (float)transform.M32;
        _hash = HashCode.Combine(
            _stops,
            HashCode.Combine(_cx, _cy, _ox, _oy, _rx, _ry),
            HashCode.Combine(_opacity, _m11, _m12, _m21, _m22, _m31, _m32));
    }

    public bool Equals(RadialGradientBrushKey other)
        => _stops.Equals(other._stops)
           && _cx == other._cx && _cy == other._cy
           && _ox == other._ox && _oy == other._oy
           && _rx == other._rx && _ry == other._ry
           && _opacity == other._opacity
           && _m11 == other._m11 && _m12 == other._m12
           && _m21 == other._m21 && _m22 == other._m22
           && _m31 == other._m31 && _m32 == other._m32;

    public override bool Equals(object? obj) => obj is RadialGradientBrushKey other && Equals(other);

    public override int GetHashCode() => _hash;
}

/// <summary>
/// Key for a fully materialised linear gradient brush (stops + geometry in destination pixels).
/// </summary>
internal readonly struct LinearGradientBrushKey : IEquatable<LinearGradientBrushKey>
{
    private readonly GradientStopKey _stops;
    private readonly float _x0, _y0, _x1, _y1;
    private readonly float _opacity;
    private readonly float _m11, _m12, _m21, _m22, _m31, _m32;
    private readonly int _hash;

    public LinearGradientBrushKey(
        IReadOnlyList<IGradientStop> stops,
        GradientSpreadMethod spreadMethod,
        Point start,
        Point end,
        double opacity,
        Matrix transform)
    {
        _stops = new GradientStopKey(stops, spreadMethod);
        _x0 = (float)start.X;
        _y0 = (float)start.Y;
        _x1 = (float)end.X;
        _y1 = (float)end.Y;
        _opacity = (float)opacity;
        _m11 = (float)transform.M11;
        _m12 = (float)transform.M12;
        _m21 = (float)transform.M21;
        _m22 = (float)transform.M22;
        _m31 = (float)transform.M31;
        _m32 = (float)transform.M32;
        _hash = HashCode.Combine(
            _stops,
            HashCode.Combine(_x0, _y0, _x1, _y1, _opacity),
            HashCode.Combine(_m11, _m12, _m21, _m22, _m31, _m32));
    }

    public bool Equals(LinearGradientBrushKey other)
        => _stops.Equals(other._stops)
           && _x0 == other._x0 && _y0 == other._y0
           && _x1 == other._x1 && _y1 == other._y1
           && _opacity == other._opacity
           && _m11 == other._m11 && _m12 == other._m12
           && _m21 == other._m21 && _m22 == other._m22
           && _m31 == other._m31 && _m32 == other._m32;

    public override bool Equals(object? obj) => obj is LinearGradientBrushKey other && Equals(other);

    public override int GetHashCode() => _hash;
}

/// <summary>
/// A composite key identifying a gradient stop collection by its ordered stops and spread method.
/// Stores the exact stop values (packed color + offset) so lookups are collision-free.
/// </summary>
internal readonly struct GradientStopKey : IEquatable<GradientStopKey>
{
    private readonly GradientSpreadMethod _spreadMethod;
    private readonly ulong[] _stops;
    private readonly int _hash;

    public GradientStopKey(IReadOnlyList<IGradientStop> stops, GradientSpreadMethod spreadMethod)
    {
        _spreadMethod = spreadMethod;
        _stops = new ulong[stops.Count];

        var hash = new HashCode();
        hash.Add((int)spreadMethod);
        hash.Add(stops.Count);
        for (var i = 0; i < stops.Count; i++)
        {
            var argb = stops[i].Color.ToUInt32();
            var off = (uint)Math.Clamp(Math.Round(stops[i].Offset * 65535.0), 0, 65535);
            var packed = ((ulong)argb << 32) | off;
            _stops[i] = packed;
            hash.Add(packed);
        }
        _hash = hash.ToHashCode();
    }

    public bool Equals(GradientStopKey other)
    {
        if (_spreadMethod != other._spreadMethod || _stops.Length != other._stops.Length)
            return false;
        for (var i = 0; i < _stops.Length; i++)
        {
            if (_stops[i] != other._stops[i])
                return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is GradientStopKey other && Equals(other);

    public override int GetHashCode() => _hash;
}
