using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Vortice.Direct2D1;

namespace MIR.Direct2D1ForAvalonia.Media;

/// <summary>
/// Batches opaque solid rect / uniform-rounded-rect strokes and optionally replays a whole
/// simple-only session through an <see cref="ID2D1CommandList"/> (static subtree / stable frames).
/// </summary>
internal sealed class SolidRectBatch
{
    private const int MultiStrokeThreshold = 2;

    private readonly List<StrokeEntry> _strokes = new(64);
    private readonly List<SimpleRectOp> _ops = new(128);

    private ID2D1CommandList? _cachedCommandList;
    private long _cachedHash;

    private long _sessionHash;
    private bool _sessionOnlySimple = true;
    private bool _deferSimpleSession;
    private bool _active;

    /// <summary>True when this session is logging simple rects without immediate GPU fills.</summary>
    public bool IsDeferredSimpleSession => _deferSimpleSession && _sessionOnlySimple;

    public bool HasDeferredStrokes => _strokes.Count > 0;

    public void BeginSession(bool enableCommandListReuse)
    {
        _strokes.Clear();
        _ops.Clear();
        _sessionHash = unchecked((long)14695981039346656037);
        _sessionOnlySimple = true;
        // When the host reuses this DC across frames, defer simple rects so EndSession can
        // DrawImage(commandList) on cache hit, or record+draw once on miss (first frame / change).
        _deferSimpleSession = enableCommandListReuse;
        _active = true;
    }

    public void MarkNonSimple(ID2D1DeviceContext dc, D2DDeviceResourceCache resources)
    {
        if (!_active)
            return;

        // Leaving the simple-only path: materialise anything deferred, drop CL cache.
        if (_deferSimpleSession && _ops.Count > 0)
        {
            _deferSimpleSession = false;
            ExecuteOps(dc, resources, buildStrokeBatch: true);
            _ops.Clear();
        }
        else
        {
            FlushStrokes(dc, resources);
        }

        _sessionOnlySimple = false;
        InvalidateCommandListCache();
    }

    public void InvalidateCommandListCache()
    {
        _cachedCommandList?.Dispose();
        _cachedCommandList = null;
        _cachedHash = 0;
    }

    /// <summary>
    /// Handles a simple solid rect. Returns <c>true</c> if fully handled (including deferred).
    /// When deferred, nothing is drawn yet — <see cref="EndSession"/> will replay or execute.
    /// </summary>
    public bool HandleSimpleRect(
        ID2D1DeviceContext dc,
        D2DDeviceResourceCache resources,
        RoundedRect rrect,
        bool isRounded,
        float radiusX,
        float radiusY,
        ISolidColorBrush? fill,
        ISolidColorBrush? stroke,
        float strokeThickness,
        ID2D1StrokeStyle? strokeStyle)
    {
        if (!_active)
            BeginSession(enableCommandListReuse: false);

        // Translucent: force live path (caller should use non-batch immediate draw).
        if (fill is { Opacity: < 0.999 } || stroke is { Opacity: < 0.999 })
            return false;

        var op = new SimpleRectOp(
            rrect.Rect,
            isRounded,
            radiusX,
            radiusY,
            fill is not null,
            fill?.Color ?? default,
            fill?.Opacity ?? 1,
            stroke is not null && strokeThickness > 0,
            stroke?.Color ?? default,
            stroke?.Opacity ?? 1,
            strokeThickness,
            strokeStyle);

        MixHash(op);
        _ops.Add(op);

        if (_deferSimpleSession)
        {
            // Only logging — EndSession will DrawImage(CL) or execute+rebuild.
            return true;
        }

        // Live path: fill now, defer stroke for multi-figure merge.
        if (op.HasFill)
        {
            NotifyFillBounds(op.Rect, dc, resources);
            FillOne(dc, resources, op);
        }

        if (op.HasStroke)
            EnqueueStroke(op, dc, resources);

        return true;
    }

    public void FlushStrokes(ID2D1DeviceContext dc, D2DDeviceResourceCache resources)
    {
        if (_strokes.Count == 0)
            return;

        if (_strokes.Count < MultiStrokeThreshold)
        {
            for (var i = 0; i < _strokes.Count; i++)
                StrokeOne(dc, resources, _strokes[i]);
            _strokes.Clear();
            return;
        }

        var s0 = _strokes[0];
        var brush = resources.GetOrCreateSolidBrush(dc, s0.Color, s0.Opacity);
        if (brush.PlatformBrush is null)
        {
            _strokes.Clear();
            return;
        }

        using var group = BuildStrokeGeometryGroup();
        if (group is null)
        {
            for (var i = 0; i < _strokes.Count; i++)
                StrokeOne(dc, resources, _strokes[i]);
        }
        else if (s0.Style is null)
        {
            dc.DrawGeometry(group, brush.PlatformBrush, s0.Thickness);
        }
        else
        {
            dc.DrawGeometry(group, brush.PlatformBrush, s0.Thickness, s0.Style);
        }

        _strokes.Clear();
    }

    /// <summary>
    /// Completes the session: command-list replay, or flush strokes / rebuild CL for next frame.
    /// </summary>
    public void EndSession(ID2D1DeviceContext dc, D2DDeviceResourceCache resources)
    {
        if (!_active)
            return;

        try
        {
            if (_deferSimpleSession && _sessionOnlySimple && _ops.Count > 0)
            {
                if (_cachedCommandList is not null && _cachedHash == _sessionHash)
                {
                    dc.DrawImage(_cachedCommandList);
                    return;
                }

                // Hash miss or first build after invalidate: record into CL and draw once.
                if (!TryRecordOpsToCommandListAndDraw(dc, resources))
                {
                    ExecuteOps(dc, resources, buildStrokeBatch: true);
                }

                return;
            }

            // Live path (no session deferral): flush deferred strokes only.
            FlushStrokes(dc, resources);

            if (!_sessionOnlySimple)
                InvalidateCommandListCache();
        }
        finally
        {
            _strokes.Clear();
            _ops.Clear();
            _active = false;
            _deferSimpleSession = false;
        }
    }

    private void NotifyFillBounds(Rect fillBounds, ID2D1DeviceContext dc, D2DDeviceResourceCache resources)
    {
        if (_strokes.Count == 0)
            return;

        for (var i = 0; i < _strokes.Count; i++)
        {
            if (Intersects(fillBounds, _strokes[i].Bounds))
            {
                FlushStrokes(dc, resources);
                return;
            }
        }
    }

    private void EnqueueStroke(in SimpleRectOp op, ID2D1DeviceContext dc, D2DDeviceResourceCache resources)
    {
        if (_strokes.Count > 0)
        {
            var s0 = _strokes[0];
            if (!ColorEquals(s0.Color, op.StrokeColor)
                || Math.Abs(s0.Opacity - op.StrokeOpacity) > 0.0001
                || Math.Abs(s0.Thickness - op.StrokeThickness) > 0.0001
                || !ReferenceEquals(s0.Style, op.StrokeStyle))
            {
                FlushStrokes(dc, resources);
            }
        }

        _strokes.Add(new StrokeEntry(
            op.Rect.Inflate(op.StrokeThickness * 0.5),
            op.Rect,
            op.IsRounded,
            op.RadiusX,
            op.RadiusY,
            op.StrokeColor,
            op.StrokeOpacity,
            op.StrokeThickness,
            op.StrokeStyle));
    }

    private void ExecuteOps(ID2D1DeviceContext dc, D2DDeviceResourceCache resources, bool buildStrokeBatch)
    {
        _strokes.Clear();
        for (var i = 0; i < _ops.Count; i++)
        {
            var op = _ops[i];
            if (op.HasFill)
            {
                if (buildStrokeBatch)
                    NotifyFillBounds(op.Rect, dc, resources);
                FillOne(dc, resources, op);
            }

            if (op.HasStroke)
            {
                if (buildStrokeBatch)
                    EnqueueStroke(op, dc, resources);
                else
                    StrokeOne(dc, resources, new StrokeEntry(
                        op.Rect.Inflate(op.StrokeThickness * 0.5),
                        op.Rect,
                        op.IsRounded,
                        op.RadiusX,
                        op.RadiusY,
                        op.StrokeColor,
                        op.StrokeOpacity,
                        op.StrokeThickness,
                        op.StrokeStyle));
            }
        }

        if (buildStrokeBatch)
            FlushStrokes(dc, resources);
    }

    private bool TryRecordOpsToCommandListAndDraw(
        ID2D1DeviceContext dc,
        D2DDeviceResourceCache resources)
    {
        ID2D1Image? previousTarget;
        try
        {
            previousTarget = dc.Target;
        }
        catch
        {
            return false;
        }

        if (previousTarget is null)
            return false;

        var cl = dc.CreateCommandList();
        try
        {
            dc.Target = cl;
            ExecuteOps(dc, resources, buildStrokeBatch: true);
            cl.Close();

            dc.Target = previousTarget;
            dc.DrawImage(cl);

            _cachedCommandList?.Dispose();
            _cachedCommandList = cl;
            _cachedHash = _sessionHash;
            return true;
        }
        catch
        {
            try { dc.Target = previousTarget; } catch { /* ignore */ }
            cl.Dispose();
            InvalidateCommandListCache();
            return false;
        }
    }

    private ID2D1GeometryGroup? BuildStrokeGeometryGroup()
    {
        var factory = Direct2D1Platform.Direct2D1Factory;
        var geos = new ID2D1Geometry[_strokes.Count];
        try
        {
            for (var i = 0; i < _strokes.Count; i++)
            {
                var e = _strokes[i];
                if (e.IsRounded)
                {
                    geos[i] = factory.CreateRoundedRectangleGeometry(new RoundedRectangle
                    {
                        Rect = e.Rect.ToDirect2D(),
                        RadiusX = e.RadiusX,
                        RadiusY = e.RadiusY
                    });
                }
                else
                {
                    geos[i] = factory.CreateRectangleGeometry(e.Rect.ToDirect2D());
                }
            }

            return factory.CreateGeometryGroup(FillMode.Winding, geos);
        }
        catch
        {
            return null;
        }
        finally
        {
            for (var i = 0; i < geos.Length; i++)
                geos[i]?.Dispose();
        }
    }

    private static void FillOne(ID2D1DeviceContext dc, D2DDeviceResourceCache resources, in SimpleRectOp op)
    {
        var brush = resources.GetOrCreateSolidBrush(dc, op.FillColor, op.FillOpacity);
        if (brush.PlatformBrush is null)
            return;

        if (op.IsRounded)
        {
            dc.FillRoundedRectangle(new RoundedRectangle
            {
                Rect = op.Rect.ToDirect2D(),
                RadiusX = op.RadiusX,
                RadiusY = op.RadiusY
            }, brush.PlatformBrush);
        }
        else
        {
            dc.FillRectangle(op.Rect.ToDirect2D(), brush.PlatformBrush);
        }
    }

    private static void StrokeOne(ID2D1DeviceContext dc, D2DDeviceResourceCache resources, in StrokeEntry e)
    {
        var brush = resources.GetOrCreateSolidBrush(dc, e.Color, e.Opacity);
        if (brush.PlatformBrush is null)
            return;

        if (e.IsRounded)
        {
            var rr = new RoundedRectangle
            {
                Rect = e.Rect.ToDirect2D(),
                RadiusX = e.RadiusX,
                RadiusY = e.RadiusY
            };
            if (e.Style is null)
                dc.DrawRoundedRectangle(rr, brush.PlatformBrush, e.Thickness);
            else
                dc.DrawRoundedRectangle(rr, brush.PlatformBrush, e.Thickness, e.Style);
        }
        else
        {
            var rc = e.Rect.ToDirect2D();
            if (e.Style is null)
                dc.DrawRectangle(rc, brush.PlatformBrush, e.Thickness);
            else
                dc.DrawRectangle(rc, brush.PlatformBrush, e.Thickness, e.Style);
        }
    }

    private void MixHash(in SimpleRectOp op)
    {
        unchecked
        {
            _sessionHash ^= op.Rect.X.GetHashCode();
            _sessionHash *= 1099511628211;
            _sessionHash ^= op.Rect.Y.GetHashCode();
            _sessionHash *= 1099511628211;
            _sessionHash ^= op.Rect.Width.GetHashCode();
            _sessionHash *= 1099511628211;
            _sessionHash ^= op.Rect.Height.GetHashCode();
            _sessionHash *= 1099511628211;
            if (op.HasFill)
            {
                _sessionHash ^= op.FillColor.ToUInt32();
                _sessionHash *= 1099511628211;
            }

            if (op.HasStroke)
            {
                _sessionHash ^= op.StrokeColor.ToUInt32();
                _sessionHash *= 1099511628211;
                _sessionHash ^= BitConverter.SingleToInt32Bits(op.StrokeThickness);
                _sessionHash *= 1099511628211;
            }

            _sessionHash ^= op.IsRounded ? 1 : 0;
            _sessionHash *= 1099511628211;
            _sessionHash ^= BitConverter.SingleToInt32Bits(op.RadiusX);
            _sessionHash *= 1099511628211;
        }
    }

    private static bool Intersects(Rect a, Rect b)
        => a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    private static bool ColorEquals(Color a, Color b)
        => a.A == b.A && a.R == b.R && a.G == b.G && a.B == b.B;

    private readonly struct StrokeEntry
    {
        public StrokeEntry(
            Rect bounds,
            Rect rect,
            bool isRounded,
            float radiusX,
            float radiusY,
            Color color,
            double opacity,
            float thickness,
            ID2D1StrokeStyle? style)
        {
            Bounds = bounds;
            Rect = rect;
            IsRounded = isRounded;
            RadiusX = radiusX;
            RadiusY = radiusY;
            Color = color;
            Opacity = opacity;
            Thickness = thickness;
            Style = style;
        }

        public Rect Bounds { get; }
        public Rect Rect { get; }
        public bool IsRounded { get; }
        public float RadiusX { get; }
        public float RadiusY { get; }
        public Color Color { get; }
        public double Opacity { get; }
        public float Thickness { get; }
        public ID2D1StrokeStyle? Style { get; }
    }

    private readonly struct SimpleRectOp
    {
        public SimpleRectOp(
            Rect rect,
            bool isRounded,
            float radiusX,
            float radiusY,
            bool hasFill,
            Color fillColor,
            double fillOpacity,
            bool hasStroke,
            Color strokeColor,
            double strokeOpacity,
            float strokeThickness,
            ID2D1StrokeStyle? strokeStyle)
        {
            Rect = rect;
            IsRounded = isRounded;
            RadiusX = radiusX;
            RadiusY = radiusY;
            HasFill = hasFill;
            FillColor = fillColor;
            FillOpacity = fillOpacity;
            HasStroke = hasStroke;
            StrokeColor = strokeColor;
            StrokeOpacity = strokeOpacity;
            StrokeThickness = strokeThickness;
            StrokeStyle = strokeStyle;
        }

        public Rect Rect { get; }
        public bool IsRounded { get; }
        public float RadiusX { get; }
        public float RadiusY { get; }
        public bool HasFill { get; }
        public Color FillColor { get; }
        public double FillOpacity { get; }
        public bool HasStroke { get; }
        public Color StrokeColor { get; }
        public double StrokeOpacity { get; }
        public float StrokeThickness { get; }
        public ID2D1StrokeStyle? StrokeStyle { get; }
    }
}
