using System;
using System.Collections.Generic;
using MIR.DirectWriteForAvalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Vortice.DirectWrite;

#nullable enable

namespace MIR.Direct2D1ForAvalonia.Media
{
    internal class GlyphRunImpl : IGlyphRunImpl
    {
        private readonly GlyphTypeface _glyphTypeface;
        private readonly DirectWriteGlyphTypeface _glyphTypefaceImpl;

        private readonly ushort[] _glyphIndices;
        private readonly float[] _glyphAdvances;
        private readonly GlyphOffset[] _glyphOffsets;

        private Vortice.DirectWrite.GlyphRun? _glyphRun;

        public GlyphRunImpl(GlyphTypeface glyphTypeface, double fontRenderingEmSize,
            IReadOnlyList<GlyphInfo> glyphInfos, Point baselineOrigin)
        {
            _glyphTypeface = glyphTypeface;
            _glyphTypefaceImpl = (DirectWriteGlyphTypeface)glyphTypeface.PlatformTypeface;

            FontRenderingEmSize = fontRenderingEmSize;
            BaselineOrigin = baselineOrigin;

            var glyphCount = glyphInfos.Count;

            _glyphIndices = new ushort[glyphCount];

            for (var i = 0; i < glyphCount; i++)
            {
                _glyphIndices[i] = glyphInfos[i].GlyphIndex;
            }

            _glyphAdvances = new float[glyphCount];

            var width = 0.0;

            for (var i = 0; i < glyphCount; i++)
            {
                var advance = glyphInfos[i].GlyphAdvance;

                width += advance;

                _glyphAdvances[i] = (float)advance;
            }

            _glyphOffsets = new GlyphOffset[glyphCount];

            for (var i = 0; i < glyphCount; i++)
            {
                var (x, y) = glyphInfos[i].GlyphOffset;

                _glyphOffsets[i] = new GlyphOffset
                {
                    AdvanceOffset = (float)x,
                    AscenderOffset = (float)-y
                };
            }

            var scale = fontRenderingEmSize / glyphTypeface.Metrics.DesignEmHeight;

            var height = glyphTypeface.Metrics.LineSpacing * scale;

            Bounds = new Rect(baselineOrigin.X, 0, width, height);
        }

        public Vortice.DirectWrite.GlyphRun GlyphRun
        {
            get
            {
                if (_glyphRun != null)
                {
                    return _glyphRun;
                }

                _glyphRun = new Vortice.DirectWrite.GlyphRun
                {
                    FontFace = _glyphTypefaceImpl.FontFace,
                    FontEmSize = (float)FontRenderingEmSize,
                    Advances = _glyphAdvances,
                    Indices = _glyphIndices,
                    Offsets = _glyphOffsets
                };

                return _glyphRun;
            }
        }

        public GlyphTypeface GlyphTypeface => _glyphTypeface;

        public double FontRenderingEmSize { get; }

        public Point BaselineOrigin { get; }

        public Rect Bounds { get; }

        public IReadOnlyList<float> GetIntersections(float lowerBound, float upperBound)
        {
            // Used by text decoration (underline/strikethrough) to gap the line where it
            // crosses glyph bodies. The returned values are paired x-coordinates delimiting
            // spans where the glyph outline is filled within the [lowerBound, upperBound]
            // horizontal band. Skia exposes this as SKTextBlob.GetIntercepts; Direct2D has no
            // equivalent, so extract the combined glyph outline as a path geometry and run a
            // horizontal scan-line across the band using FillContainsPoint.
            if (_glyphIndices.Length == 0 || upperBound <= lowerBound)
                return Array.Empty<float>();

            using var pathGeometry = Direct2D1Platform.Direct2D1Factory.CreatePathGeometry();
            using (var sink = pathGeometry.Open())
            {
                _glyphTypefaceImpl.FontFace.GetGlyphRunOutline(
                    (float)FontRenderingEmSize,
                    _glyphIndices,
                    _glyphAdvances,
                    _glyphOffsets,
                    false,
                    false,
                    sink);
                sink.Close();
            }

            var b = pathGeometry.GetBounds();
            var midY = (lowerBound + upperBound) * 0.5f + (float)BaselineOrigin.Y;
            var left = b.Left;
            var right = b.Right;
            var bandWidth = right - left;
            if (bandWidth <= 0)
                return Array.Empty<float>();

            // Sample the fill at a sub-pixel pitch across the glyph width. The intersection
            // edges are resolved to the sample grid, which is fine for decoration gaps.
            const float samplePitch = 0.5f;
            var sampleCount = (int)Math.Ceiling(bandWidth / samplePitch) + 1;

            var intersections = new List<float>();
            var inside = false;
            for (var i = 0; i < sampleCount; i++)
            {
                var x = left + i * samplePitch;
                var filled = pathGeometry.FillContainsPoint(new System.Numerics.Vector2(x, midY));
                if (filled != inside)
                {
                    intersections.Add(x - (float)BaselineOrigin.X);
                    inside = filled;
                }
            }

            if (inside)
            {
                // Close an open trailing span so callers always see paired boundaries.
                intersections.Add(right - (float)BaselineOrigin.X);
            }

            return intersections;
        }

        public void Dispose()
        {
            //_glyphRun?.Dispose();

            _glyphRun = null;
        }
    }
}