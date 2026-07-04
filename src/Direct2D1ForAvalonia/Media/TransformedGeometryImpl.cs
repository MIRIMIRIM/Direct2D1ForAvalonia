using Avalonia.Platform;
using Vortice.Direct2D1;

namespace MIR.Direct2D1ForAvalonia.Media
{
    internal class TransformedGeometryImpl : GeometryImpl, ITransformedGeometryImpl
    {
        private readonly ID2D1TransformedGeometry _transformedGeometry;

        /// <summary>
        /// Initializes a new instance of the <see cref="StreamGeometryImpl"/> class.
        /// </summary>
        /// <param name="source">The source geometry.</param>
        /// <param name="geometry">An existing Direct2D <see cref="ID2D1TransformedGeometry"/>.</param>
        public TransformedGeometryImpl(ID2D1TransformedGeometry geometry, GeometryImpl source)
            : base(geometry)
        {
            _transformedGeometry = geometry;
            SourceGeometry = source;
        }

        public IGeometryImpl SourceGeometry { get; }

        /// <inheritdoc/>
        public Matrix Transform => _transformedGeometry.Transform.ToAvalonia();

        protected override ID2D1Geometry GetSourceGeometry() => _transformedGeometry.SourceGeometry;
    }
}