using Vortice.Direct2D1;

namespace MIR.Direct2D1ForAvalonia.Media
{
    /// <summary>
    /// A Direct2D implementation of a <see cref="Avalonia.Media.LineGeometry"/>.
    /// </summary>
    internal class LineGeometryImpl : StreamGeometryImpl
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="StreamGeometryImpl"/> class.
        /// </summary>
        public LineGeometryImpl(Point p1, Point p2)
        {
            using (var sink = _pathGeometry.Open())
            {
                sink.BeginFigure(p1.ToVortice(), FigureBegin.Hollow);
                sink.AddLine(p2.ToVortice());
                sink.EndFigure(FigureEnd.Open);
                sink.Close();
            }
        }
    }
}