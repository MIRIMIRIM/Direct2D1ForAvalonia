using Avalonia.Platform;
using Vortice.Direct2D1;

namespace MIR.Direct2D1ForAvalonia.Media
{
    /// <summary>
    /// A Direct2D implementation of a <see cref="Avalonia.Media.StreamGeometry"/>.
    /// </summary>
    internal class StreamGeometryImpl : GeometryImpl, IStreamGeometryImpl
    {
        private protected readonly ID2D1PathGeometry _pathGeometry;

        /// <summary>
        /// Initializes a new instance of the <see cref="StreamGeometryImpl"/> class.
        /// </summary>
        public StreamGeometryImpl()
            : this(CreateGeometry())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="StreamGeometryImpl"/> class.
        /// </summary>
        /// <param name="geometry">An existing Direct2D <see cref="PathGeometry"/>.</param>
        public StreamGeometryImpl(ID2D1PathGeometry geometry)
            : base(geometry)
        {
            _pathGeometry = geometry;
        }

        /// <inheritdoc/>
        public IStreamGeometryImpl Clone()
        {
            var result = Direct2D1Platform.Direct2D1Factory.CreatePathGeometry();
            using (var sink = result.Open())
            {
                _pathGeometry.Stream(sink);
                sink.Close();
            }

            return new StreamGeometryImpl(result);
        }

        /// <inheritdoc/>
        public IStreamGeometryContextImpl Open()
        {
            return new StreamGeometryContextImpl(_pathGeometry.Open());
        }

        private static ID2D1PathGeometry CreateGeometry()
        {
            return Direct2D1Platform.Direct2D1Factory.CreatePathGeometry();
        }
    }
}