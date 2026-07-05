using System;
using System.Collections.Generic;
using Avalonia.Platform;
using Vortice.Direct2D1;

namespace MIR.Direct2D1ForAvalonia.Media
{
    internal sealed class Direct2DRegionImpl : IPlatformRenderInterfaceRegion, IDisposable
    {
        private readonly List<LtrbPixelRect> _rects = new();

        public bool IsEmpty => _rects.Count == 0;

        public LtrbPixelRect Bounds
        {
            get
            {
                if (_rects.Count == 0)
                    return default;

                var left = _rects[0].Left;
                var top = _rects[0].Top;
                var right = _rects[0].Right;
                var bottom = _rects[0].Bottom;
                for (var i = 1; i < _rects.Count; i++)
                {
                    var rect = _rects[i];
                    left = Math.Min(left, rect.Left);
                    top = Math.Min(top, rect.Top);
                    right = Math.Max(right, rect.Right);
                    bottom = Math.Max(bottom, rect.Bottom);
                }

                var bounds = default(LtrbPixelRect);
                bounds.Left = left;
                bounds.Top = top;
                bounds.Right = right;
                bounds.Bottom = bottom;
                return bounds;
            }
        }

        public IList<LtrbPixelRect> Rects => _rects;

        public void AddRect(LtrbPixelRect rect)
        {
            if (rect.Right <= rect.Left || rect.Bottom <= rect.Top)
                return;

            _rects.Add(rect);
        }

        public void Reset() => _rects.Clear();

        public bool Intersects(LtrbRect rect)
        {
            foreach (var regionRect in _rects)
            {
                if (regionRect.Left < rect.Right &&
                    regionRect.Right > rect.Left &&
                    regionRect.Top < rect.Bottom &&
                    regionRect.Bottom > rect.Top)
                {
                    return true;
                }
            }

            return false;
        }

        public bool Contains(Point point)
        {
            var x = (int)point.X;
            var y = (int)point.Y;

            foreach (var rect in _rects)
            {
                if (x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom)
                    return true;
            }

            return false;
        }

        public ID2D1Geometry CreateGeometry()
        {
            if (_rects.Count == 0)
                return Direct2D1Platform.Direct2D1Factory.CreateRectangleGeometry(default);

            if (_rects.Count == 1)
                return Direct2D1Platform.Direct2D1Factory.CreateRectangleGeometry(ToRect(_rects[0]).ToDirect2D());

            var geometries = new ID2D1Geometry[_rects.Count];
            try
            {
                for (var i = 0; i < _rects.Count; i++)
                {
                    geometries[i] = Direct2D1Platform.Direct2D1Factory.CreateRectangleGeometry(ToRect(_rects[i]).ToDirect2D());
                }

                return Direct2D1Platform.Direct2D1Factory.CreateGeometryGroup(
                    FillMode.Winding,
                    geometries,
                    (uint)geometries.Length);
            }
            finally
            {
                foreach (var geometry in geometries)
                {
                    geometry?.Dispose();
                }
            }
        }

        public void Dispose() => Reset();

        public static Rect ToRect(LtrbPixelRect rect)
            => new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }
}
