using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Media;
using Vortice.DirectWrite;
using FontMetrics = Avalonia.Media.FontMetrics;
using FontSimulations = Avalonia.Media.FontSimulations;
using GlyphMetrics = Avalonia.Media.GlyphMetrics;

namespace Avalonia.Direct2D1.Media
{
    internal sealed class GlyphTypefaceImpl : IGlyphTypeface
    {
        private bool _isDisposed;

        public GlyphTypefaceImpl(IDWriteFont font)
        {
            DWFont = font ?? throw new ArgumentNullException(nameof(font));

            FontFace = DWFont.CreateFontFace().QueryInterface<IDWriteFontFace1>();

            var m = FontFace.Metrics;

            var designEm = (short)Math.Clamp(m.DesignUnitsPerEm, 0, short.MaxValue);

            // Convert from font design coordinates (y-up) to Avalonia's y-down layout conventions.
            Metrics = new FontMetrics
            {
                DesignEmHeight = designEm,
                Ascent = unchecked((short)-m.Ascent),
                Descent = unchecked((short)m.Descent),
                LineGap = unchecked((short)m.LineGap),
                UnderlinePosition = unchecked((short)-m.UnderlinePosition),
                UnderlineThickness = unchecked((short)m.UnderlineThickness),
                StrikethroughPosition = unchecked((short)-m.StrikethroughPosition),
                StrikethroughThickness = unchecked((short)m.StrikethroughThickness),
                IsFixedPitch = FontFace.IsMonospacedFont
            };

            FamilyName = DWFont.FontFamily.FamilyNames.GetString(0);
            Weight = (Avalonia.Media.FontWeight)DWFont.Weight;
            Style = (Avalonia.Media.FontStyle)DWFont.Style;
            Stretch = (Avalonia.Media.FontStretch)DWFont.Stretch;

            GlyphCount = FontFace.GlyphCount;
        }

        public IDWriteFont DWFont { get; }

        public IDWriteFontFace1 FontFace { get; }

        public FontMetrics Metrics { get; }

        public int GlyphCount { get; }

        public FontSimulations FontSimulations => FontSimulations.None;

        public string FamilyName { get; }

        public Avalonia.Media.FontWeight Weight { get; }

        public Avalonia.Media.FontStyle Style { get; }

        public Avalonia.Media.FontStretch Stretch { get; }

        public ushort GetGlyph(uint codepoint)
        {
            ThrowIfDisposed();

            return FontFace.GetGlyphIndices([codepoint])[0];
        }

        public bool TryGetGlyph(uint codepoint, out ushort glyph)
        {
            glyph = GetGlyph(codepoint);
            return glyph != 0;
        }

        public ushort[] GetGlyphs(ReadOnlySpan<uint> codepoints)
        {
            ThrowIfDisposed();

            if (codepoints.Length == 0)
                return Array.Empty<ushort>();

            return FontFace.GetGlyphIndices(codepoints.ToArray());
        }

        public int GetGlyphAdvance(ushort glyph)
        {
            ThrowIfDisposed();

            var metrics = FontFace.GetDesignGlyphMetrics([glyph], false);
            return unchecked((int)metrics[0].AdvanceWidth);
        }

        public int[] GetGlyphAdvances(ReadOnlySpan<ushort> glyphs)
        {
            ThrowIfDisposed();

            if (glyphs.Length == 0)
                return Array.Empty<int>();

            var arr = glyphs.ToArray();
            var metrics = FontFace.GetDesignGlyphMetrics(arr, false);

            var advances = new int[arr.Length];
            for (var i = 0; i < arr.Length; i++)
                advances[i] = unchecked((int)metrics[i].AdvanceWidth);

            return advances;
        }

        public bool TryGetGlyphMetrics(ushort glyph, out GlyphMetrics metrics)
        {
            ThrowIfDisposed();

            metrics = default;

            Vortice.DirectWrite.GlyphMetrics gm;
            try
            {
                gm = FontFace.GetDesignGlyphMetrics([glyph], false)[0];
            }
            catch
            {
                return false;
            }

            // Approximate black-box metrics from design glyph metrics.
            var width = unchecked((int)gm.AdvanceWidth) - gm.LeftSideBearing - gm.RightSideBearing;
            if (width < 0) width = 0;

            // Convert y-up design values to Avalonia's y-down conventions.
            // FontMetrics.Ascent was negated in the ctor, so flip it back here.
            var ascent = -Metrics.Ascent;
            var descent = Metrics.Descent;

            var yBearing = ascent - gm.TopSideBearing;
            var height = ascent + descent - gm.TopSideBearing - gm.BottomSideBearing;
            if (height < 0) height = 0;

            metrics = new GlyphMetrics
            {
                XBearing = gm.LeftSideBearing,
                YBearing = yBearing,
                Width = width,
                Height = height
            };

            return true;
        }

        public bool TryGetTable(uint tag, out byte[] table)
        {
            ThrowIfDisposed();

            table = Array.Empty<byte>();

            var dwTag = SwapBytes(tag);
            if (!FontFace.TryGetFontTable(dwTag, out var tableData, out var tableContext))
                return false;

            try
            {
                table = tableData.ToArray();
                return table.Length != 0;
            }
            finally
            {
                FontFace.ReleaseFontTable(tableContext);
            }
        }

        private static uint SwapBytes(uint x)
        {
            x = (x >> 16) | (x << 16);
            return ((x & 0xFF00FF00) >> 8) | ((x & 0x00FF00FF) << 8);
        }

        private void ThrowIfDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(GlyphTypefaceImpl));
        }

        private void Dispose(bool disposing)
        {
            if (_isDisposed)
                return;

            _isDisposed = true;

            if (!disposing)
                return;

            FontFace.Dispose();
            DWFont.Dispose();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
