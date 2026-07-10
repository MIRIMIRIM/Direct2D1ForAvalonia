using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Vortice.Direct2D1;

namespace MIR.Direct2D1ForAvalonia.Media;

/// <summary>
/// Batches opaque solid rect / uniform-rounded-rect strokes and optionally replays a whole
/// simple-only session through an <see cref="ID2D1CommandList"/> (static subtree / stable frames).
/// <para>
/// <b>Correctness contract (does not change Avalonia API semantics when respected):</b>
/// </para>
/// <list type="bullet">
/// <item>Translucent fills/strokes (opacity &lt; 1) never enter the batch — strict issue order.</item>
/// <item>Deferred strokes flush before any later fill that intersects their outset bounds.</item>
/// <item>Any non-simple op / transform / clip / Clear / render-options change materialises the batch
/// and invalidates the command list (no stale replay across different content or state).</item>
/// <item>Command-list replay requires a byte-identical op stream (geometry + colors + target size).
/// Lists live on the <see cref="D2DDeviceResourceCache"/> (device-scoped) so composition
/// intermediate targets that allocate a new DC/bitmap each dirty paint still hit steady-state.</item>
/// </list>
/// Real apps: Avalonia re-issues full render-data for a dirty visual; hash mismatch forces rebuild.
/// Partial/incremental draw streams that differ from the cached list never hit replay.
/// </summary>
internal sealed class SolidRectBatch
{
    private const int MultiStrokeThreshold = 2;

    private readonly List<StrokeEntry> _strokes = new(64);
    private readonly List<SimpleRectOp> _ops = new(128);

    private long _sessionHash;
    private bool _sessionOnlySimple = true;
    private bool _deferSimpleSession;
    private bool _active;
    private int _pixelW;
    private int _pixelH;

    /// <summary>True when this session is logging simple rects without immediate GPU fills.</summary>
    public bool IsDeferredSimpleSession => _deferSimpleSession && _sessionOnlySimple;

    public bool HasDeferredStrokes => _strokes.Count > 0;

    /// <summary>Command-list cache hits this process (for diagnostics / benches).</summary>
    public int CommandListHits { get; private set; }

    /// <summary>Command-list rebuilds / misses this process.</summary>
    public int CommandListMisses { get; private set; }

    /// <summary>Debug: EndSession took deferred simple path.</summary>
    public int DebugDeferredEnds { get; private set; }

    /// <summary>Debug: EndSession took live stroke-flush path.</summary>
    public int DebugLiveEnds { get; private set; }

    /// <summary>Debug: ops recorded in last completed session.</summary>
    public int DebugLastOpCount { get; private set; }

    /// <summary>Debug: last command-list record failure message.</summary>
    public string? DebugLastRecordError { get; private set; }

    public void BeginSession(bool enableCommandListReuse, int pixelWidth = 0, int pixelHeight = 0)
    {
        _strokes.Clear();
        _ops.Clear();
        _pixelW = pixelWidth;
        _pixelH = pixelHeight;
        _sessionHash = unchecked((long)14695981039346656037);
        // Target size is part of the content fingerprint so a resize never replays a wrong CL.
        unchecked
        {
            _sessionHash ^= pixelWidth;
            _sessionHash *= 1099511628211;
            _sessionHash ^= pixelHeight;
            _sessionHash *= 1099511628211;
        }

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

        // Empty session (e.g. RenderOptions set before any draw): keep simple/CL eligibility.
        if (_ops.Count == 0 && _strokes.Count == 0)
            return;

        // Leaving the simple-only path: materialise anything deferred.
        if (_deferSimpleSession && _ops.Count > 0)
        {
            ExecuteOps(dc, resources, buildStrokeBatch: true);
            _ops.Clear();
        }
        else
        {
            FlushStrokes(dc, resources);
        }

        // Do not wipe the device-scoped CL cache — other visuals may still use those entries.
        _deferSimpleSession = false;
        _sessionOnlySimple = false;
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

        // Translucent (brush opacity or per-color alpha): keep strict issue order — no batch/CL.
        if (!IsFullyOpaque(fill) || !IsFullyOpaque(stroke))
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

        // Defer only while this session remains simple-only (CL candidate).
        if (_deferSimpleSession && _sessionOnlySimple)
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
    /// <param name="sessionTarget">
    /// The render target image that was bound for this session (preferred over reading dc.Target).
    /// </param>
    public void EndSession(
        ID2D1DeviceContext dc,
        D2DDeviceResourceCache resources,
        ID2D1Image? sessionTarget = null)
    {
        if (!_active)
            return;

        try
        {
            DebugLastOpCount = _ops.Count;
            if (_deferSimpleSession && _sessionOnlySimple && _ops.Count > 0)
            {
                DebugDeferredEnds++;
                if (resources.TryGetCommandList(_sessionHash, _pixelW, _pixelH, out var cached))
                {
                    // Steady-state: one DrawImage replaces N fills + strokes (composition intermediate
                    // dirty repaint when content hash matches a prior paint on this device).
                    CommandListHits++;
                    dc.DrawImage(cached);
                    return;
                }

                // Cold / content-changed: paint with multi-stroke batching, then store a CL for
                // the next identical session (including a brand-new DrawingContextImpl).
                CommandListMisses++;
                ExecuteOps(dc, resources, buildStrokeBatch: true);
                TryRecordOpsToCommandListStoreOnly(dc, resources, sessionTarget);
                return;
            }

            // Live path (no session deferral): flush deferred strokes only.
            DebugLiveEnds++;
            FlushStrokes(dc, resources);
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

    /// <summary>
    /// Records the session ops into a command list and stores it on the device cache without
    /// presenting again (caller already painted via <see cref="ExecuteOps"/>).
    /// </summary>
    private bool TryRecordOpsToCommandListStoreOnly(
        ID2D1DeviceContext dc,
        D2DDeviceResourceCache resources,
        ID2D1Image? sessionTarget)
    {
        // Ops list is still intact until EndSession finally-block.
        if (_ops.Count == 0)
            return false;

        ID2D1Image? previousTarget = sessionTarget;
        if (previousTarget is null)
        {
            try { previousTarget = dc.Target; }
            catch { return false; }
        }

        if (previousTarget is null)
            return false;

        ID2D1CommandList? cl = null;
        try
        {
            DebugLastRecordError = null;
            cl = dc.CreateCommandList();
            if (cl is null)
            {
                DebugLastRecordError = "CreateCommandList returned null";
                return false;
            }

            dc.Target = cl;
            // Re-walk ops into the command list (second pass, cold frame only).
            ExecuteOps(dc, resources, buildStrokeBatch: true);
            cl.Close();

            dc.Target = previousTarget;
            resources.StoreCommandList(_sessionHash, _pixelW, _pixelH, cl);
            cl = null;
            return true;
        }
        catch (Exception ex)
        {
            DebugLastRecordError = ex.GetType().Name + ": " + ex.Message;
            try { dc.Target = previousTarget; } catch { /* ignore */ }
            cl?.Dispose();
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

    /// <summary>
    /// Brush is absent, or both <see cref="ISolidColorBrush.Opacity"/> and color alpha are fully opaque.
    /// </summary>
    internal static bool IsFullyOpaque(ISolidColorBrush? brush)
        => brush is null
           || (brush.Opacity >= 0.999 && brush.Color.A >= 255);

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
