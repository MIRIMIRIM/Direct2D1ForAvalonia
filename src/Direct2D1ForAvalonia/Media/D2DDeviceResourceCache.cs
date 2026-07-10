using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Vortice.Direct2D1;

namespace MIR.Direct2D1ForAvalonia.Media;

/// <summary>
/// A cache of device-dependent D2D resources that outlives a single frame.
/// <para>
/// A <see cref="DrawingContextImpl"/> is created and disposed once per frame, so any cache
/// stored on it is a frame-local cache — useless for scenes that draw the same brush at
/// different positions across frames. Gradient stop collections, however, depend only on the
/// stops and spread method (not the destination geometry), so the same collection is reusable
/// across frames. This cache is keyed on the owning render target's identity, so it lives as
/// long as the render target does and is dropped (and its COM resources released) when the
/// render target is collected — including on device loss, when a fresh render target is created.
/// </para>
/// </summary>
internal sealed class D2DDeviceResourceCache
{
    // Keyed on the render-target RCW identity. The render target wrapper instance is stable for
    // the lifetime of the target, so the same cache is returned every frame. Weak keys mean the
    // cache (and its cached COM resources) become eligible for collection once the render target
    // is gone; no explicit disposal hook is required.
    private static readonly ConditionalWeakTable<ID2D1RenderTarget, D2DDeviceResourceCache> s_caches = new();

    private readonly Dictionary<GradientStopKey, ID2D1GradientStopCollection> _gradientStops = new();

    public static D2DDeviceResourceCache For(ID2D1RenderTarget renderTarget)
        => s_caches.GetValue(renderTarget, static _ => new D2DDeviceResourceCache());

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
