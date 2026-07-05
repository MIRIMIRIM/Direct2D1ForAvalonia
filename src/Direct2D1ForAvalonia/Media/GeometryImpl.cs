using System;
using System.Numerics;
using Avalonia.Media;
using Avalonia.Platform;
using Vortice.Direct2D1;

namespace MIR.Direct2D1ForAvalonia.Media
{
    /// <summary>
    /// The platform-specific interface for <see cref="Avalonia.Media.Geometry"/>.
    /// </summary>
    internal abstract class GeometryImpl(ID2D1Geometry geometry) : IGeometryImpl
    {
        private const float ContourApproximation = 0.0001f;

        /// <inheritdoc/>
        public Rect Bounds => Geometry.GetWidenedBounds(0).ToAvalonia();

        /// <inheritdoc />
        public double ContourLength => Geometry.ComputeLength(null, ContourApproximation);

        public ID2D1Geometry Geometry { get; } = geometry;

        /// <inheritdoc/>
        public Rect GetRenderBounds(IPen? pen)
        {
            if (pen == null || Math.Abs(pen.Thickness) < float.Epsilon)
                return Geometry.GetBounds().ToAvalonia();
            var originalBounds = Geometry.GetWidenedBounds((float)pen.Thickness).ToAvalonia();
            switch (pen.LineCap)
            {
                case PenLineCap.Flat:
                    return originalBounds;
                case PenLineCap.Round:
                    return originalBounds.Inflate(pen.Thickness / 2);
                case PenLineCap.Square:
                    return originalBounds.Inflate(pen.Thickness);
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public IGeometryImpl GetWidenedGeometry(IPen pen)
        {
            var result = Direct2D1Platform.Direct2D1Factory.CreatePathGeometry();

            using (var sink = result.Open())
            {
                Geometry.Widen(
                    (float)pen.Thickness,
                    pen.ToDirect2DStrokeStyle(Direct2D1Platform.Direct2D1Factory),
                    0.25f,
                    sink);
                sink.Close();
            }

            return new StreamGeometryImpl(result);
        }

        /// <inheritdoc/>
        public bool FillContains(Point point)
        {
            return Geometry.FillContainsPoint(point.ToVortice());
        }

        /// <inheritdoc/>
        public IGeometryImpl Intersect(IGeometryImpl geometry)
        {
            var result = Direct2D1Platform.Direct2D1Factory.CreatePathGeometry();
            using (var sink = result.Open())
            {
                Geometry.CombineWithGeometry(((GeometryImpl)geometry).Geometry, CombineMode.Intersect, sink);
                sink.Close();
            }
            return new StreamGeometryImpl(result);
        }

        /// <inheritdoc/>
        public bool StrokeContains(Avalonia.Media.IPen? pen, Point point)
        {
            return Geometry.StrokeContainsPoint(point.ToVortice(), (float)(pen?.Thickness ?? 0));
        }

        public ITransformedGeometryImpl WithTransform(Matrix transform)
        {
            return new TransformedGeometryImpl(
                Direct2D1Platform.Direct2D1Factory.CreateTransformedGeometry(
                    GetSourceGeometry(),
                    transform.ToDirect2D()),
                this);
        }

        /// <inheritdoc />
        public bool TryGetPointAtDistance(double distance, out Point point)
        {
            Geometry.ComputePointAtLength((float)distance, ContourApproximation, out var tangentVector);
            point = new Point(tangentVector.X, tangentVector.Y);
            return true;
        }

        /// <inheritdoc />
        public bool TryGetPointAndTangentAtDistance(double distance, out Point point, out Point tangent)
        {
            // The native ID2D1Geometry::ComputePointAtLength exposes a unit-tangent output,
            // but Vortice's binding drops it (it only returns the point). Approximate the
            // tangent by differencing two points straddling the requested length. The step is
            // tiny relative to ContourApproximation so the result is effectively the unit
            // tangent for smooth segments.
            Geometry.ComputePointAtLength((float)distance, ContourApproximation, out var p);
            point = new Point(p.X, p.Y);

            const float tangentEpsilon = 0.001f;
            Geometry.ComputePointAtLength((float)(distance + tangentEpsilon), ContourApproximation, out var pNext);

            var dx = pNext.X - p.X;
            var dy = pNext.Y - p.Y;
            var len = Math.Sqrt((dx * dx) + (dy * dy));
            if (len < float.Epsilon)
            {
                // Degenerate (end of contour or cusp): fall back to the previous point so the
                // direction is still meaningful when the forward difference vanishes.
                Geometry.ComputePointAtLength((float)Math.Max(distance - tangentEpsilon, 0), ContourApproximation, out var pPrev);
                dx = p.X - pPrev.X;
                dy = p.Y - pPrev.Y;
                len = Math.Sqrt((dx * dx) + (dy * dy));
            }

            tangent = len >= float.Epsilon ? new Point(dx / len, dy / len) : new Point(1, 0);
            return true;
        }

        public bool TryGetSegment(double startDistance, double stopDistance, bool startOnBeginFigure, out IGeometryImpl segmentGeometry)
        {
            var contourLength = ContourLength;
            if (contourLength <= 0 || stopDistance <= startDistance)
            {
                segmentGeometry = null!;
                return false;
            }

            var start = Math.Clamp(startDistance, 0, contourLength);
            var stop = Math.Clamp(stopDistance, 0, contourLength);
            if (stop <= start)
            {
                segmentGeometry = null!;
                return false;
            }

            var result = Direct2D1Platform.Direct2D1Factory.CreatePathGeometry();
            try
            {
                using (var sink = result.Open())
                {
                    Geometry.ComputePointAtLength((float)start, ContourApproximation, out var startPoint);
                    sink.BeginFigure(startPoint, FigureBegin.Hollow);

                    var length = stop - start;
                    var segmentCount = Math.Clamp((int)Math.Ceiling(length / 0.5), 1, 4096);
                    var previousPoint = startPoint;

                    for (var i = 1; i <= segmentCount; i++)
                    {
                        var distance = start + (length * i / segmentCount);
                        Geometry.ComputePointAtLength((float)distance, ContourApproximation, out var point);

                        if (!AreClose(previousPoint, point))
                        {
                            sink.AddLine(point);
                            previousPoint = point;
                        }
                    }

                    sink.EndFigure(FigureEnd.Open);
                    sink.Close();
                }

                segmentGeometry = new StreamGeometryImpl(result);
                return true;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        protected virtual ID2D1Geometry GetSourceGeometry() => Geometry;

        private static bool AreClose(Vector2 left, Vector2 right)
            => Math.Abs(left.X - right.X) < ContourApproximation &&
               Math.Abs(left.Y - right.Y) < ContourApproximation;
    }
}
