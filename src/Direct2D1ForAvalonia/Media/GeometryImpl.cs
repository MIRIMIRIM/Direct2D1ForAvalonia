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
        private const double SegmentFlatnessTolerance = 0.125;
        private const double SegmentLengthTolerance = 0.125;
        private const int MaxSegmentLineCount = 4096;
        private const int MaxSegmentSubdivisionDepth = 16;
        private double _contourLength = double.NaN;

        /// <inheritdoc/>
        public Rect Bounds => Geometry.GetWidenedBounds(0).ToAvalonia();

        /// <inheritdoc />
        public double ContourLength
        {
            get
            {
                if (double.IsNaN(_contourLength))
                {
                    _contourLength = Geometry.ComputeLength(null, ContourApproximation);
                }

                return _contourLength;
            }
        }

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
            var p = Geometry.ComputePointAtLength((float)distance, ContourApproximation, out _);
            point = new Point(p.X, p.Y);
            return IsFinite(p);
        }

        /// <inheritdoc />
        public bool TryGetPointAndTangentAtDistance(double distance, out Point point, out Point tangent)
        {
            var p = Geometry.ComputePointAtLength((float)distance, ContourApproximation, out var tangentVector);
            point = new Point(p.X, p.Y);
            tangent = new Point(tangentVector.X, tangentVector.Y);
            return IsFinite(p) && IsFinite(tangentVector);
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

            var startPoint = ComputePointAtDistance(start);
            var stopPoint = ComputePointAtDistance(stop);
            if (!IsFinite(startPoint) || !IsFinite(stopPoint))
            {
                segmentGeometry = null!;
                return false;
            }

            var result = Direct2D1Platform.Direct2D1Factory.CreatePathGeometry();
            try
            {
                using (var sink = result.Open())
                {
                    sink.BeginFigure(startPoint, FigureBegin.Hollow);

                    var previousPoint = startPoint;
                    var lineCount = 0;

                    AddSegmentLines(
                        sink,
                        start,
                        startPoint,
                        stop,
                        stopPoint,
                        depth: 0,
                        ref previousPoint,
                        ref lineCount);

                    if (lineCount == 0 || !AreClose(previousPoint, stopPoint))
                    {
                        sink.AddLine(stopPoint);
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

        private void AddSegmentLines(
            ID2D1GeometrySink sink,
            double startDistance,
            Vector2 startPoint,
            double stopDistance,
            Vector2 stopPoint,
            int depth,
            ref Vector2 previousPoint,
            ref int lineCount)
        {
            if (ShouldSplitSegment(startDistance, startPoint, stopDistance, stopPoint, depth, lineCount, out var midDistance, out var midPoint))
            {
                AddSegmentLines(
                    sink,
                    startDistance,
                    startPoint,
                    midDistance,
                    midPoint,
                    depth + 1,
                    ref previousPoint,
                    ref lineCount);
                AddSegmentLines(
                    sink,
                    midDistance,
                    midPoint,
                    stopDistance,
                    stopPoint,
                    depth + 1,
                    ref previousPoint,
                    ref lineCount);
                return;
            }

            AddLine(sink, stopPoint, ref previousPoint, ref lineCount);
        }

        private bool ShouldSplitSegment(
            double startDistance,
            Vector2 startPoint,
            double stopDistance,
            Vector2 stopPoint,
            int depth,
            int lineCount,
            out double midDistance,
            out Vector2 midPoint)
        {
            if (depth >= MaxSegmentSubdivisionDepth || lineCount >= MaxSegmentLineCount)
            {
                midDistance = 0;
                midPoint = default;
                return false;
            }

            midDistance = (startDistance + stopDistance) * 0.5;
            midPoint = ComputePointAtDistance(midDistance);
            if (!IsFinite(midPoint))
            {
                return false;
            }

            var length = stopDistance - startDistance;
            var chordLength = Distance(startPoint, stopPoint);
            var lengthError = Math.Max(0, length - chordLength);
            if (lengthError > SegmentLengthTolerance)
            {
                return true;
            }

            return DistanceToLineSegment(midPoint, startPoint, stopPoint) > SegmentFlatnessTolerance;
        }

        private static void AddLine(
            ID2D1GeometrySink sink,
            Vector2 point,
            ref Vector2 previousPoint,
            ref int lineCount)
        {
            if (lineCount >= MaxSegmentLineCount)
            {
                return;
            }

            if (!AreClose(previousPoint, point))
            {
                sink.AddLine(point);
                previousPoint = point;
                lineCount++;
            }
        }

        private static bool AreClose(Vector2 left, Vector2 right)
            => Math.Abs(left.X - right.X) < ContourApproximation &&
               Math.Abs(left.Y - right.Y) < ContourApproximation;

        private Vector2 ComputePointAtDistance(double distance)
            => Geometry.ComputePointAtLength((float)distance, ContourApproximation, out _);

        private static bool IsFinite(Vector2 point)
            => float.IsFinite(point.X) && float.IsFinite(point.Y);

        private static double Distance(Vector2 left, Vector2 right)
        {
            var dx = left.X - right.X;
            var dy = left.Y - right.Y;
            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        private static double DistanceToLineSegment(Vector2 point, Vector2 start, Vector2 end)
        {
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var lengthSquared = (dx * dx) + (dy * dy);

            if (lengthSquared < float.Epsilon)
            {
                return Distance(point, start);
            }

            var t = (((point.X - start.X) * dx) + ((point.Y - start.Y) * dy)) / lengthSquared;
            t = Math.Clamp(t, 0, 1);

            return Distance(point, new Vector2((float)(start.X + (dx * t)), (float)(start.Y + (dy * t))));
        }
    }
}
