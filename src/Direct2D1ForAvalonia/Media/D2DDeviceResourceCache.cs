using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Vortice.Direct2D1;

namespace MIR.Direct2D1ForAvalonia.Media;

/// <summary>
/// A cache of device-dependent D2D resources that outlives a single frame.
/// <para>
/// Prefer keying on <see cref="ID2D1Device"/> when the render target is a device context:
/// composition replay opens short-lived intermediate targets every frame, so a per-render-target
/// cache would go cold every frame. Device-level solid brushes / stroke styles / gradient stops
/// are valid across all device contexts created from the same D2D device.
/// </para>
/// <para>
/// Classic WIC / factory render targets (no device) keep a per-render-target cache.
/// </para>
/// </summary>
internal sealed class D2DDeviceResourceCache
{
    // Shared by all ID2D1DeviceContext instances from the same ID2D1Device (window surface +
    // composition intermediates + GPU-compatible layers).
    private static readonly ConditionalWeakTable<ID2D1Device, D2DDeviceResourceCache> s_deviceCaches = new();

    // Fallback for non-device-context targets (e.g. pure WIC RT without a device association).
    private static readonly ConditionalWeakTable<ID2D1RenderTarget, D2DDeviceResourceCache> s_renderTargetCaches = new();

    private readonly Dictionary<GradientStopKey, ID2D1GradientStopCollection> _gradientStops = new();
    private readonly Dictionary<SolidBrushKey, SolidColorBrushImpl> _solidBrushes = new();
    private readonly Dictionary<StrokeStyleKey, ID2D1StrokeStyle> _strokeStyles = new();

    public static D2DDeviceResourceCache For(ID2D1RenderTarget renderTarget)
    {
        // Device-context path: share one cache for the whole D2D device.
        if (renderTarget is ID2D1DeviceContext deviceContext)
        {
            try
            {
                var device = deviceContext.Device;
                if (device is not null)
                    return s_deviceCaches.GetValue(device, static _ => new D2DDeviceResourceCache());
            }
            catch
            {
                // Fall through to per-RT cache if Device is unavailable.
            }
        }

        return s_renderTargetCaches.GetValue(renderTarget, static _ => new D2DDeviceResourceCache());
    }

    /// <summary>
    /// Returns a cached <see cref="ID2D1GradientStopCollection"/> for the given stops and spread
    /// method, creating one on first use. The returned collection is owned by the cache — callers
    /// must not dispose it. D2D gradient brushes AddRef the collection internally, so the cached
    /// RCW stays valid for as long as any brush references it or the cache is alive.
    /// </summary>
    public ID2D1GradientStopCollection GetOrCreateGradientStops(
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
    /// Solid brushes ignore pattern transforms, so they are safe to reuse under any world transform.
    /// Device-context brushes may be used with any device context from the same D2D device.
    /// </summary>
    public SolidColorBrushImpl GetOrCreateSolidBrush(ID2D1RenderTarget target, Color color, double opacity)
    {
        var key = new SolidBrushKey(color, opacity);
        if (_solidBrushes.TryGetValue(key, out var cached))
            return cached;

        var impl = new SolidColorBrushImpl(color, opacity, target)
        {
            IsCached = true
        };
        _solidBrushes[key] = impl;
        return impl;
    }

    /// <summary>
    /// Returns a cached stroke style for the given pen properties, or <c>null</c> when the pen
    /// matches D2D's default stroke (solid, flat caps, miter join, miter limit 10) so callers can
    /// omit the stroke-style argument entirely — cheaper for the common solid-outline case.
    /// Non-null styles are immutable factory resources owned by the cache — do not dispose.
    /// </summary>
    public ID2D1StrokeStyle? GetOrCreateStrokeStyle(IPen pen, ID2D1RenderTarget target)
    {
        if (IsDefaultSolidStroke(pen))
            return null;

        var key = new StrokeStyleKey(pen);
        if (_strokeStyles.TryGetValue(key, out var cached))
            return cached;

        var style = pen.ToDirect2DStrokeStyle(target);
        _strokeStyles[key] = style;
        return style;
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
        // Avalonia default miter limit is 10 — same as D2D's default when style is null.
        return Math.Abs(pen.MiterLimit - 10.0) < 0.001;
    }
}

/// <summary>
/// A composite key identifying a gradient stop collection by its ordered stops and spread method.
/// Stores the exact stop values (packed color + offset) so lookups are collision-free — a hash-only
/// key could return the wrong collection and render the wrong gradient colors.
/// </summary>
internal readonly struct GradientStopKey : IEquatable<GradientStopKey>
{
    private readonly GradientSpreadMethod _spreadMethod;
    // One entry per stop: ARGB in the upper 32 bits, quantized offset in the lower 32 bits.
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
            // Quantize offset to 1/65535 to avoid float key drift, same scheme as SolidBrushKey.
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
