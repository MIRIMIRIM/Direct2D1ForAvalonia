using System;
using System.Buffers;
using System.Globalization;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Vortice.DirectWrite;

namespace Avalonia.Direct2D1.Media
{
    internal sealed class TextShaperImpl : ITextShaperImpl
    {
        private static readonly object s_gate = new();
        private static IDWriteTextAnalyzer? s_analyzer;

        private static IDWriteTextAnalyzer Analyzer
        {
            get
            {
                if (s_analyzer is not null)
                    return s_analyzer;

                lock (s_gate)
                {
                    if (s_analyzer is not null)
                        return s_analyzer;

                    s_analyzer = Direct2D1Platform.DirectWriteFactory.CreateTextAnalyzer();
                    return s_analyzer;
                }
            }
        }

        public ShapedBuffer ShapeText(ReadOnlyMemory<char> text, TextShaperOptions options)
        {
            if (options.Typeface is GlyphTypefaceImpl dw)
                return ShapeWithDirectWrite(text, options, dw);

            return ShapeSimple(text, options);
        }

        private static ShapedBuffer ShapeWithDirectWrite(ReadOnlyMemory<char> text, TextShaperOptions options, GlyphTypefaceImpl typeface)
        {
            var span = text.Span;
            if (span.Length == 0)
                return new ShapedBuffer(text, 0, options.Typeface, options.FontRenderingEmSize, options.BidiLevel);

            var textString = span.ToString();
            var isRtl = (options.BidiLevel & 1) != 0;
            var culture = options.Culture ?? CultureInfo.CurrentCulture;
            var locale = string.IsNullOrWhiteSpace(culture.Name) ? CultureInfo.CurrentUICulture.Name : culture.Name;

            var analyzer = Analyzer;

            var maxGlyphCount = checked((uint)(span.Length * 3 / 2 + 16));

            ushort[]? clusterMapArr = null;
            ShapingTextProperties[]? textPropsArr = null;
            ushort[]? glyphIndicesArr = null;
            ShapingGlyphProperties[]? glyphPropsArr = null;
            float[]? advancesArr = null;
            GlyphOffset[]? offsetsArr = null;

            try
            {
                clusterMapArr = ArrayPool<ushort>.Shared.Rent(span.Length);
                textPropsArr = ArrayPool<ShapingTextProperties>.Shared.Rent(span.Length);
                glyphIndicesArr = ArrayPool<ushort>.Shared.Rent((int)maxGlyphCount);
                glyphPropsArr = ArrayPool<ShapingGlyphProperties>.Shared.Rent((int)maxGlyphCount);

                uint glyphCount;

                var scriptAnalysis = default(ScriptAnalysis);

                analyzer.GetGlyphs(
                    textString: textString,
                    textLength: (uint)span.Length,
                    fontFace: typeface.FontFace,
                    isSideways: false,
                    isRightToLeft: isRtl,
                    scriptAnalysis: scriptAnalysis,
                    localeName: locale,
                    numberSubstitution: null,
                    features: null,
                    featureRangeLengths: null,
                    featureRanges: 0,
                    maxGlyphCount: maxGlyphCount,
                    clusterMap: clusterMapArr,
                    textProps: textPropsArr,
                    glyphIndices: glyphIndicesArr,
                    glyphProps: glyphPropsArr,
                    actualGlyphCount: out glyphCount);

                advancesArr = ArrayPool<float>.Shared.Rent((int)glyphCount);
                offsetsArr = ArrayPool<GlyphOffset>.Shared.Rent((int)glyphCount);

                analyzer.GetGlyphPlacements(
                    textString: textString,
                    clusterMap: clusterMapArr,
                    textProps: textPropsArr,
                    textLength: (uint)span.Length,
                    glyphIndices: glyphIndicesArr,
                    glyphProps: glyphPropsArr,
                    glyphCount: glyphCount,
                    fontFace: typeface.FontFace,
                    fontEmSize: (float)options.FontRenderingEmSize,
                    isSideways: false,
                    isRightToLeft: isRtl,
                    scriptAnalysis: scriptAnalysis,
                    localeName: locale,
                    features: null,
                    featureRangeLengths: null,
                    featureRanges: 0,
                    glyphAdvances: advancesArr,
                    glyphOffsets: offsetsArr);

                return BuildShapedBuffer(text, options, span, isRtl, glyphCount, clusterMapArr, glyphIndicesArr, advancesArr, offsetsArr);
            }
            finally
            {
                if (clusterMapArr is not null)
                    ArrayPool<ushort>.Shared.Return(clusterMapArr);
                if (textPropsArr is not null)
                    ArrayPool<ShapingTextProperties>.Shared.Return(textPropsArr);
                if (glyphIndicesArr is not null)
                    ArrayPool<ushort>.Shared.Return(glyphIndicesArr);
                if (glyphPropsArr is not null)
                    ArrayPool<ShapingGlyphProperties>.Shared.Return(glyphPropsArr);
                if (advancesArr is not null)
                    ArrayPool<float>.Shared.Return(advancesArr);
                if (offsetsArr is not null)
                    ArrayPool<GlyphOffset>.Shared.Return(offsetsArr);
            }
        }

        private static ShapedBuffer BuildShapedBuffer(
            ReadOnlyMemory<char> text,
            TextShaperOptions options,
            ReadOnlySpan<char> span,
            bool isRtl,
            uint glyphCount,
            ushort[] clusterMapArr,
            ushort[] glyphIndicesArr,
            float[] advancesArr,
            GlyphOffset[] offsetsArr)
        {
            var typeface = options.Typeface;
            var length = checked((int)glyphCount);
            var shaped = new ShapedBuffer(text, length, typeface, options.FontRenderingEmSize, options.BidiLevel);

            if (length == 0)
                return shaped;

            var glyphClusters = ArrayPool<int>.Shared.Rent(length);
            try
            {
                Array.Fill(glyphClusters, -1, 0, length);

                for (var i = 0; i < span.Length; i++)
                {
                    var gi = clusterMapArr[i];
                    if (gi < length && glyphClusters[gi] < 0)
                        glyphClusters[gi] = i;
                }

                var last = 0;
                for (var i = 0; i < length; i++)
                {
                    if (glyphClusters[i] < 0)
                        glyphClusters[i] = last;
                    else
                        last = glyphClusters[i];
                }

                for (var i = 0; i < length; i++)
                {
                    var srcIndex = isRtl ? (length - 1 - i) : i;

                    var cluster = glyphClusters[srcIndex];
                    var glyphId = glyphIndicesArr[srcIndex];

                    var advance = advancesArr[srcIndex];
                    if (isRtl)
                        advance = Math.Abs(advance);

                    advance += (float)options.LetterSpacing;

                    var offset = offsetsArr[srcIndex];
                    var dx = offset.AdvanceOffset;
                    var dy = -offset.AscenderOffset;

                    if (cluster >= 0 && cluster < span.Length && span[cluster] == '\t')
                    {
                        glyphId = (ushort)typeface.GetGlyph(' ');
                        advance = options.IncrementalTabWidth > 0
                            ? (float)options.IncrementalTabWidth
                            : (float)(4 * typeface.GetGlyphAdvance(glyphId) * (options.FontRenderingEmSize / typeface.Metrics.DesignEmHeight));
                        dx = 0;
                        dy = 0;
                    }

                    shaped[i] = new GlyphInfo(glyphId, cluster, advance, new Vector(dx, dy));
                }

                return shaped;
            }
            finally
            {
                ArrayPool<int>.Shared.Return(glyphClusters);
            }
        }

        private static ShapedBuffer ShapeSimple(ReadOnlyMemory<char> text, TextShaperOptions options)
        {
            var typeface = options.Typeface;
            var span = text.Span;

            var glyphCount = CountScalars(span);
            var shaped = new ShapedBuffer(text, glyphCount, typeface, options.FontRenderingEmSize, options.BidiLevel);

            var scale = typeface.Metrics.DesignEmHeight > 0
                ? options.FontRenderingEmSize / typeface.Metrics.DesignEmHeight
                : 0;

            var rtl = (options.BidiLevel & 1) != 0;

            var i = 0;
            var outIndex = 0;
            while (i < span.Length)
            {
                var cluster = i;
                var c = span[i];
                var codepoint = (int)c;
                var consumed = 1;
                if (char.IsHighSurrogate(c)
                    && i + 1 < span.Length
                    && char.IsLowSurrogate(span[i + 1]))
                {
                    codepoint = char.ConvertToUtf32(c, span[i + 1]);
                    consumed = 2;
                }

                i += consumed;

                var glyphId = (ushort)typeface.GetGlyph((uint)codepoint);
                var offset = default(Vector);
                var advance = typeface.GetGlyphAdvance(glyphId) * scale + options.LetterSpacing;

                if (codepoint == '\t')
                {
                    glyphId = (ushort)typeface.GetGlyph(' ');
                    advance = options.IncrementalTabWidth > 0
                        ? options.IncrementalTabWidth
                        : 4 * typeface.GetGlyphAdvance(glyphId) * scale;
                    offset = default;
                }

                var dst = rtl ? (glyphCount - 1 - outIndex) : outIndex;
                shaped[dst] = new GlyphInfo(glyphId, cluster, advance, offset);
                outIndex++;
            }

            return shaped;
        }

        private static int CountScalars(ReadOnlySpan<char> text)
        {
            var count = 0;
            var i = 0;
            while (i < text.Length)
            {
                var consumed = 1;
                var c = text[i];
                if (char.IsHighSurrogate(c)
                    && i + 1 < text.Length
                    && char.IsLowSurrogate(text[i + 1]))
                {
                    consumed = 2;
                }

                i += consumed;
                count++;
            }

            return count;
        }
    }
}
