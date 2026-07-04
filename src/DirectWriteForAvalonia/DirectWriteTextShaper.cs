using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.TextFormatting;
using Avalonia.Media.TextFormatting.Unicode;
using Avalonia.Platform;
using SharpGen.Runtime;
using Vortice.DirectWrite;
using AvaloniaFontFeature = Avalonia.Media.FontFeature;

namespace MIR.DirectWriteForAvalonia
{
    [SupportedOSPlatform("windows")]
    internal sealed class DirectWriteTextShaper : ITextShaperImpl
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

                    s_analyzer = DirectWritePlatform.DirectWriteFactory.CreateTextAnalyzer();
                    return s_analyzer;
                }
            }
        }

        public ShapedBuffer ShapeText(ReadOnlyMemory<char> text, TextShaperOptions options)
        {
            if (options.GlyphTypeface.PlatformTypeface is DirectWriteGlyphTypeface dw)
                return ShapeWithDirectWrite(text, options, dw);

            return ShapeSimple(text, options);
        }

        public ITextShaperTypeface CreateTypeface(GlyphTypeface glyphTypeface) => new DirectWriteTextShaperTypeface();

        private static ShapedBuffer ShapeWithDirectWrite(ReadOnlyMemory<char> text, TextShaperOptions options, DirectWriteGlyphTypeface typeface)
        {
            var span = text.Span;
            if (span.Length == 0)
                return new ShapedBuffer(text, 0, options.GlyphTypeface, options.FontRenderingEmSize, options.BidiLevel);

            var textString = TryGetString(text) ?? span.ToString();
            var culture = options.Culture ?? CultureInfo.CurrentCulture;
            var locale = string.IsNullOrWhiteSpace(culture.Name) ? CultureInfo.CurrentUICulture.Name : culture.Name;

            var analyzer = Analyzer;
            var runs = AnalyzeRuns(textString, locale, options.BidiLevel);
            if (runs.Count == 0)
                return new ShapedBuffer(text, 0, options.GlyphTypeface, options.FontRenderingEmSize, options.BidiLevel);

            var hasFeatures = options.FontFeatures is { Count: > 0 };

            // Fast path: single shaping run with no features → write directly to ShapedBuffer
            if (runs.Count == 1 && !hasFeatures)
                return ShapeSingleRun(text, textString, span, options, typeface, runs[0], locale, analyzer);

            // Multi-run / feature-segment path
            return ShapeMultiRun(text, textString, span, options, typeface, runs, locale, analyzer);
        }

        /// <summary>
        /// Fast path: shapes the entire text as a single run using analyzed ScriptAnalysis.
        /// Writes directly to ShapedBuffer (no intermediate List), avoiding extra allocations.
        /// </summary>
        private static ShapedBuffer ShapeSingleRun(
            ReadOnlyMemory<char> text,
            string textString,
            ReadOnlySpan<char> span,
            TextShaperOptions options,
            DirectWriteGlyphTypeface typeface,
            ShapingRun run,
            string locale,
            IDWriteTextAnalyzer analyzer)
        {
            var isRtl = run.IsRightToLeft;
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

                analyzer.GetGlyphs(
                    textString: textString,
                    textLength: (uint)span.Length,
                    fontFace: typeface.FontFace,
                    isSideways: false,
                    isRightToLeft: isRtl,
                    scriptAnalysis: run.ScriptAnalysis,
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
                    actualGlyphCount: out var glyphCount);

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
                    scriptAnalysis: run.ScriptAnalysis,
                    localeName: locale,
                    features: null,
                    featureRangeLengths: null,
                    featureRanges: 0,
                    glyphAdvances: advancesArr,
                    glyphOffsets: offsetsArr);

                // Write directly to ShapedBuffer (no intermediate List<GlyphInfo>)
                return BuildShapedBuffer(
                    text, options, span, isRtl,
                    glyphCount, clusterMapArr, glyphIndicesArr, advancesArr, offsetsArr);
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

        /// <summary>
        /// Builds a ShapedBuffer directly from DWrite output arrays (single-run fast path).
        /// Restores baseline glyph-order reversal for RTL text.
        /// </summary>
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
            var typeface = options.GlyphTypeface;
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
                    var cluster = glyphClusters[i];
                    var glyphId = glyphIndicesArr[i];

                    var advance = advancesArr[i];
                    if (isRtl)
                        advance = Math.Abs(advance);

                    advance += (float)options.LetterSpacing;

                    var offset = offsetsArr[i];
                    var dx = offset.AdvanceOffset;
                    var dy = -offset.AscenderOffset;

                    if (cluster >= 0 && cluster < span.Length && span[cluster] == '\t')
                    {
                        glyphId = GetSpaceGlyph(typeface);
                        advance = options.IncrementalTabWidth > 0
                            ? (float)options.IncrementalTabWidth
                            : (float)(4 * GetGlyphAdvance(typeface, glyphId) * (options.FontRenderingEmSize / typeface.Metrics.DesignEmHeight));
                        dx = 0;
                        dy = 0;
                    }
                    else if (cluster >= 0 && cluster < span.Length && new Codepoint(span[cluster]).IsBreakChar)
                    {
                        glyphId = GetSpaceGlyph(typeface);
                        advance = 0;
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

        /// <summary>
        /// Multi-run path: handles multiple script/bidi runs and/or feature segments.
        /// Uses pre-analyzed runs from AnalyzeRuns().
        /// </summary>
        private static ShapedBuffer ShapeMultiRun(
            ReadOnlyMemory<char> text,
            string textString,
            ReadOnlySpan<char> span,
            TextShaperOptions options,
            DirectWriteGlyphTypeface typeface,
            List<ShapingRun> runs,
            string locale,
            IDWriteTextAnalyzer analyzer)
        {

            var maxRunTextLength = 0;
            for (var i = 0; i < runs.Count; i++)
            {
                if (runs[i].Length > maxRunTextLength)
                    maxRunTextLength = runs[i].Length;
            }

            var maxGlyphCount = checked((uint)(maxRunTextLength * 3 / 2 + 16));

            ushort[]? clusterMapArr = null;
            ShapingTextProperties[]? textPropsArr = null;
            ushort[]? glyphIndicesArr = null;
            ShapingGlyphProperties[]? glyphPropsArr = null;
            float[]? advancesArr = null;
            GlyphOffset[]? offsetsArr = null;
            int[]? glyphClustersArr = null;

            try
            {
                clusterMapArr = ArrayPool<ushort>.Shared.Rent(maxRunTextLength);
                textPropsArr = ArrayPool<ShapingTextProperties>.Shared.Rent(maxRunTextLength);
                glyphIndicesArr = ArrayPool<ushort>.Shared.Rent((int)maxGlyphCount);
                glyphPropsArr = ArrayPool<ShapingGlyphProperties>.Shared.Rent((int)maxGlyphCount);
                advancesArr = ArrayPool<float>.Shared.Rent((int)maxGlyphCount);
                offsetsArr = ArrayPool<GlyphOffset>.Shared.Rent((int)maxGlyphCount);
                glyphClustersArr = ArrayPool<int>.Shared.Rent((int)maxGlyphCount);

                var glyphs = new List<GlyphInfo>(checked((int)maxGlyphCount));

                for (var runIndex = 0; runIndex < runs.Count; runIndex++)
                {
                    var run = runs[runIndex];
                    if (run.Length == 0)
                        continue;

                    var runText = run.Start == 0 && run.Length == textString.Length
                        ? textString
                        : textString.Substring(run.Start, run.Length);
                    var featureSegments = BuildFeatureSegments(options.FontFeatures, run.Start, run.Length);
                    if (featureSegments is null || featureSegments.Count == 0)
                    {
                        ShapeSegment(runText, span.Slice(run.Start, run.Length), run.Start, run.Length, null);
                    }
                    else
                    {
                        for (var segmentIndex = 0; segmentIndex < featureSegments.Count; segmentIndex++)
                        {
                            var segment = featureSegments[segmentIndex];
                            if (segment.Length == 0)
                                continue;

                            var segmentText = segment.Start == 0 && segment.Length == run.Length
                                ? runText
                                : runText.Substring(segment.Start, segment.Length);

                            ShapeSegment(
                                segmentText,
                                span.Slice(run.Start + segment.Start, segment.Length),
                                run.Start + segment.Start,
                                segment.Length,
                                segment.Features);
                        }
                    }

                    void ShapeSegment(
                        string segmentText,
                        ReadOnlySpan<char> segmentSpan,
                        int segmentTextOffset,
                        int segmentTextLength,
                        Vortice.DirectWrite.FontFeature[]? segmentFeatures)
                    {
                        using var dwriteFeatures = DWriteFeatureRanges.Create(segmentFeatures, segmentTextLength);

                        analyzer.GetGlyphs(
                            textString: segmentText,
                            textLength: (uint)segmentTextLength,
                            fontFace: typeface.FontFace,
                            isSideways: false,
                            isRightToLeft: run.IsRightToLeft,
                            scriptAnalysis: run.ScriptAnalysis,
                            localeName: locale,
                            numberSubstitution: null,
                            features: dwriteFeatures.Features,
                            featureRangeLengths: dwriteFeatures.FeatureRangeLengths,
                            featureRanges: dwriteFeatures.FeatureRanges,
                            maxGlyphCount: maxGlyphCount,
                            clusterMap: clusterMapArr,
                            textProps: textPropsArr,
                            glyphIndices: glyphIndicesArr,
                            glyphProps: glyphPropsArr,
                            actualGlyphCount: out var glyphCount);

                        analyzer.GetGlyphPlacements(
                            textString: segmentText,
                            clusterMap: clusterMapArr,
                            textProps: textPropsArr,
                            textLength: (uint)segmentTextLength,
                            glyphIndices: glyphIndicesArr,
                            glyphProps: glyphPropsArr,
                            glyphCount: glyphCount,
                            fontFace: typeface.FontFace,
                            fontEmSize: (float)options.FontRenderingEmSize,
                            isSideways: false,
                            isRightToLeft: run.IsRightToLeft,
                            scriptAnalysis: run.ScriptAnalysis,
                            localeName: locale,
                            features: dwriteFeatures.Features,
                            featureRangeLengths: dwriteFeatures.FeatureRangeLengths,
                            featureRanges: dwriteFeatures.FeatureRanges,
                            glyphAdvances: advancesArr,
                            glyphOffsets: offsetsArr);

                        AppendRunGlyphs(
                            segmentSpan,
                            segmentTextOffset,
                            run.IsRightToLeft,
                            glyphCount,
                            clusterMapArr,
                            glyphIndicesArr,
                            advancesArr,
                            offsetsArr,
                            glyphClustersArr,
                            options,
                            options.GlyphTypeface,
                            glyphs);
                    }
                }

                var shaped = new ShapedBuffer(text, glyphs.Count, options.GlyphTypeface, options.FontRenderingEmSize, options.BidiLevel);
                for (var i = 0; i < glyphs.Count; i++)
                    shaped[i] = glyphs[i];

                return shaped;
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
                if (glyphClustersArr is not null)
                    ArrayPool<int>.Shared.Return(glyphClustersArr);
            }
        }

        private static string? TryGetString(ReadOnlyMemory<char> text)
        {
            if (!MemoryMarshal.TryGetString(text, out var containingString, out var start, out var length))
                return null;

            if (containingString is null)
                return null;

            if (start == 0 && length == containingString.Length)
                return containingString;

            return containingString.Substring(start, length);
        }

        private static void AppendRunGlyphs(
            ReadOnlySpan<char> runText,
            int runTextOffset,
            bool isRtl,
            uint glyphCount,
            ushort[] clusterMapArr,
            ushort[] glyphIndicesArr,
            float[] advancesArr,
            GlyphOffset[] offsetsArr,
            int[] glyphClustersArr,
            TextShaperOptions options,
            GlyphTypeface typeface,
            List<GlyphInfo> output)
        {
            var length = checked((int)glyphCount);
            if (length == 0)
                return;

            Array.Fill(glyphClustersArr, -1, 0, length);

            for (var i = 0; i < runText.Length; i++)
            {
                var gi = clusterMapArr[i];
                if (gi < length && glyphClustersArr[gi] < 0)
                    glyphClustersArr[gi] = i;
            }

            var last = 0;
            for (var i = 0; i < length; i++)
            {
                if (glyphClustersArr[i] < 0)
                    glyphClustersArr[i] = last;
                else
                    last = glyphClustersArr[i];
            }

            for (var i = 0; i < length; i++)
            {
                var localCluster = glyphClustersArr[i];
                var cluster = localCluster + runTextOffset;
                var glyphId = glyphIndicesArr[i];

                var advance = advancesArr[i];
                if (isRtl)
                    advance = Math.Abs(advance);
                advance += (float)options.LetterSpacing;

                var offset = offsetsArr[i];
                var dx = offset.AdvanceOffset;
                var dy = -offset.AscenderOffset;

                if (localCluster >= 0 && localCluster < runText.Length && runText[localCluster] == '\t')
                {
                    glyphId = GetSpaceGlyph(typeface);
                    advance = options.IncrementalTabWidth > 0
                        ? (float)options.IncrementalTabWidth
                        : (float)(4 * GetGlyphAdvance(typeface, glyphId) * (options.FontRenderingEmSize / typeface.Metrics.DesignEmHeight));
                    dx = 0;
                    dy = 0;
                }
                else if (localCluster >= 0 && localCluster < runText.Length && new Codepoint(runText[localCluster]).IsBreakChar)
                {
                    glyphId = GetSpaceGlyph(typeface);
                    advance = 0;
                    dx = 0;
                    dy = 0;
                }

                output.Add(new GlyphInfo(glyphId, cluster, advance, new Vector(dx, dy)));
            }
        }

        [ThreadStatic]
        private static TextAnalysisSource? t_source;

        [ThreadStatic]
        private static TextAnalysisSink? t_sink;

        private static unsafe List<ShapingRun> AnalyzeRuns(string textString, string locale, sbyte defaultBidiLevel)
        {
            var textLength = textString.Length;
            var readingDirection = (defaultBidiLevel & 1) != 0
                ? ReadingDirection.RightToLeft
                : ReadingDirection.LeftToRight;

            fixed (char* textPtr = textString)
            fixed (char* localePtr = locale)
            {
                var source = t_source ??= new TextAnalysisSource();
                source.Initialize(textPtr, textLength, localePtr, readingDirection);

                var sink = t_sink ??= new TextAnalysisSink();
                sink.Reset();

                Analyzer.AnalyzeScript(source, 0, (uint)textLength, sink);
                Analyzer.AnalyzeBidi(source, 0, (uint)textLength, sink);

                return BuildShapingRuns(textLength, sink.ScriptRuns, sink.BidiRuns, unchecked((byte)defaultBidiLevel));
            }
        }

        private static List<ShapingRun> BuildShapingRuns(
            int textLength,
            List<ScriptRun> scriptRuns,
            List<BidiRun> bidiRuns,
            byte defaultBidiLevel)
        {
            if (textLength == 0)
                return new List<ShapingRun>(0);

            scriptRuns.Sort(static (x, y) => x.Start.CompareTo(y.Start));
            bidiRuns.Sort(static (x, y) => x.Start.CompareTo(y.Start));

            var boundaries = new List<int>(scriptRuns.Count * 2 + bidiRuns.Count * 2 + 2) { 0, textLength };

            for (var i = 0; i < scriptRuns.Count; i++)
            {
                AddBoundary(boundaries, scriptRuns[i].Start, textLength);
                AddBoundary(boundaries, scriptRuns[i].End, textLength);
            }

            for (var i = 0; i < bidiRuns.Count; i++)
            {
                AddBoundary(boundaries, bidiRuns[i].Start, textLength);
                AddBoundary(boundaries, bidiRuns[i].End, textLength);
            }

            boundaries.Sort();

            var distinctCount = 1;
            for (var i = 1; i < boundaries.Count; i++)
            {
                if (boundaries[i] != boundaries[distinctCount - 1])
                    boundaries[distinctCount++] = boundaries[i];
            }

            if (distinctCount <= 1)
                return new List<ShapingRun> { new(0, textLength, default, defaultBidiLevel) };

            var runs = new List<ShapingRun>(distinctCount - 1);
            var scriptIndex = 0;
            var bidiIndex = 0;

            for (var i = 0; i < distinctCount - 1; i++)
            {
                var start = boundaries[i];
                var end = boundaries[i + 1];
                if (end <= start)
                    continue;

                while (scriptIndex < scriptRuns.Count && scriptRuns[scriptIndex].End <= start)
                    scriptIndex++;
                while (bidiIndex < bidiRuns.Count && bidiRuns[bidiIndex].End <= start)
                    bidiIndex++;

                var script = scriptIndex < scriptRuns.Count && scriptRuns[scriptIndex].Contains(start)
                    ? scriptRuns[scriptIndex].ScriptAnalysis
                    : default;

                var bidiLevel = bidiIndex < bidiRuns.Count && bidiRuns[bidiIndex].Contains(start)
                    ? bidiRuns[bidiIndex].ResolvedLevel
                    : defaultBidiLevel;

                var length = end - start;
                if (runs.Count > 0)
                {
                    var previous = runs[runs.Count - 1];
                    if (previous.Start + previous.Length == start
                        && previous.BidiLevel == bidiLevel
                        && EqualityComparer<ScriptAnalysis>.Default.Equals(previous.ScriptAnalysis, script))
                    {
                        runs[runs.Count - 1] = new ShapingRun(previous.Start, previous.Length + length, script, bidiLevel);
                        continue;
                    }
                }

                runs.Add(new ShapingRun(start, length, script, bidiLevel));
            }

            return runs;
        }

        private static void AddBoundary(List<int> boundaries, int value, int textLength)
        {
            if (value > 0 && value < textLength)
                boundaries.Add(value);
        }

        private static bool TryParseFeatureTag(string tag, out FontFeatureTag dwriteTag)
        {
            dwriteTag = default;

            if (string.IsNullOrWhiteSpace(tag) || tag.Length != 4)
                return false;

            uint value = 0;
            for (var i = 0; i < tag.Length; i++)
            {
                var c = tag[i];
                if (c > byte.MaxValue)
                    return false;

                value |= (uint)(byte)c << (i * 8);
            }

            dwriteTag = (FontFeatureTag)(int)value;
            return true;
        }

        private static ShapedBuffer ShapeSimple(ReadOnlyMemory<char> text, TextShaperOptions options)
        {
            var typeface = options.GlyphTypeface;
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
                var codepoint = Codepoint.ReadAt(span, i, out var consumed);
                i += consumed;

                var glyphId = GetGlyph(typeface, codepoint);
                var offset = default(Vector);
                var advance = GetGlyphAdvance(typeface, glyphId) * scale + options.LetterSpacing;

                if ((int)codepoint == '\t')
                {
                    glyphId = GetSpaceGlyph(typeface);
                    advance = options.IncrementalTabWidth > 0
                        ? options.IncrementalTabWidth
                        : 4 * GetGlyphAdvance(typeface, glyphId) * scale;
                    offset = default;
                }
                else if (codepoint.IsBreakChar)
                {
                    glyphId = GetSpaceGlyph(typeface);
                    advance = 0;
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

        private static ushort GetGlyph(GlyphTypeface typeface, int codepoint)
        {
            return typeface.CharacterToGlyphMap[codepoint];
        }

        private static ushort GetSpaceGlyph(GlyphTypeface typeface) => GetGlyph(typeface, ' ');

        private static ushort GetGlyphAdvance(GlyphTypeface typeface, ushort glyphId)
        {
            return typeface.TryGetHorizontalGlyphAdvance(glyphId, out var advance) ? advance : (ushort)0;
        }

        private readonly record struct ScriptRun(int Start, int Length, ScriptAnalysis ScriptAnalysis)
        {
            public int End => Start + Length;

            public bool Contains(int position) => position >= Start && position < End;
        }

        private readonly record struct BidiRun(int Start, int Length, byte ResolvedLevel)
        {
            public int End => Start + Length;

            public bool Contains(int position) => position >= Start && position < End;
        }

        private readonly record struct ShapingRun(int Start, int Length, ScriptAnalysis ScriptAnalysis, byte BidiLevel)
        {
            public bool IsRightToLeft => (BidiLevel & 1) != 0;
        }

        private readonly record struct ParsedFeature(int Start, int End, FontFeatureTag Tag, uint Value)
        {
            public bool Contains(int index) => index >= Start && index < End;
        }

        private readonly record struct FeatureSegment(int Start, int Length, Vortice.DirectWrite.FontFeature[]? Features);

        private static List<FeatureSegment>? BuildFeatureSegments(
            IReadOnlyList<AvaloniaFontFeature>? fontFeatures,
            int runStart,
            int runLength)
        {
            if (fontFeatures is null || fontFeatures.Count == 0)
                return null;

            var parsed = new List<ParsedFeature>(fontFeatures.Count);
            var runEnd = runStart + runLength;

            for (var i = 0; i < fontFeatures.Count; i++)
            {
                var feature = fontFeatures[i];
                if (!TryParseFeatureTag(feature.Tag, out var dwriteTag))
                    continue;

                var featureStart = Math.Max(0, feature.Start);
                var featureEnd = feature.End < 0 ? int.MaxValue : feature.End;

                if (featureEnd <= featureStart)
                    continue;

                var overlapStart = Math.Max(featureStart, runStart);
                var overlapEnd = Math.Min(featureEnd, runEnd);
                if (overlapStart >= overlapEnd)
                    continue;

                var value = feature.Value < 0 ? 0u : unchecked((uint)feature.Value);
                parsed.Add(new ParsedFeature(overlapStart - runStart, overlapEnd - runStart, dwriteTag, value));
            }

            if (parsed.Count == 0)
                return null;

            var boundaries = new List<int>(parsed.Count * 2 + 2) { 0, runLength };

            for (var i = 0; i < parsed.Count; i++)
            {
                boundaries.Add(parsed[i].Start);
                boundaries.Add(parsed[i].End);
            }

            boundaries.Sort();

            var distinctCount = 1;
            for (var i = 1; i < boundaries.Count; i++)
            {
                if (boundaries[i] != boundaries[distinctCount - 1])
                    boundaries[distinctCount++] = boundaries[i];
            }

            if (distinctCount <= 1)
                return null;

            var segments = new List<FeatureSegment>(distinctCount - 1);
            Vortice.DirectWrite.FontFeature[]? lastFeatures = null;
            var featureScratch = new Vortice.DirectWrite.FontFeature[parsed.Count];

            for (var i = 0; i < distinctCount - 1; i++)
            {
                var segmentStart = boundaries[i];
                var segmentEnd = boundaries[i + 1];
                if (segmentEnd <= segmentStart)
                    continue;

                var segmentLength = segmentEnd - segmentStart;

                var activeFeatureCount = 0;
                for (var j = 0; j < parsed.Count; j++)
                {
                    if (!parsed[j].Contains(segmentStart))
                        continue;

                    featureScratch[activeFeatureCount++] = new Vortice.DirectWrite.FontFeature
                    {
                        NameTag = parsed[j].Tag,
                        Parameter = parsed[j].Value
                    };
                }

                var currentFeatures = CreateFeatureArray(featureScratch, activeFeatureCount);

                if (segments.Count > 0 && FeatureSetEquals(lastFeatures, currentFeatures))
                {
                    var previous = segments[segments.Count - 1];
                    segments[segments.Count - 1] = previous with { Length = previous.Length + segmentLength };
                }
                else
                {
                    segments.Add(new FeatureSegment(segmentStart, segmentLength, currentFeatures));
                    lastFeatures = currentFeatures;
                }
            }

            return segments;
        }

        private static Vortice.DirectWrite.FontFeature[]? CreateFeatureArray(
            Vortice.DirectWrite.FontFeature[] scratch,
            int count)
        {
            if (count == 0)
                return null;

            var features = new Vortice.DirectWrite.FontFeature[count];
            Array.Copy(scratch, features, count);
            return features;
        }

        private static bool FeatureSetEquals(Vortice.DirectWrite.FontFeature[]? left, Vortice.DirectWrite.FontFeature[]? right)
        {
            if (ReferenceEquals(left, right))
                return true;

            if (left is null || right is null)
                return false;

            if (left.Length != right.Length)
                return false;

            for (var i = 0; i < left.Length; i++)
            {
                if (left[i].NameTag != right[i].NameTag || left[i].Parameter != right[i].Parameter)
                    return false;
            }

            return true;
        }

        private sealed class DWriteFeatureRanges : IDisposable
        {
            public static readonly DWriteFeatureRanges Empty = new(null, null, 0, IntPtr.Zero, IntPtr.Zero);

            private readonly IntPtr _featurePointer;
            private readonly IntPtr _typographicFeaturesPointer;

            private DWriteFeatureRanges(
                IntPtr? features,
                uint[]? featureRangeLengths,
                uint featureRanges,
                IntPtr featurePointer,
                IntPtr typographicFeaturesPointer)
            {
                Features = features;
                FeatureRangeLengths = featureRangeLengths;
                FeatureRanges = featureRanges;
                _featurePointer = featurePointer;
                _typographicFeaturesPointer = typographicFeaturesPointer;
            }

            public IntPtr? Features { get; }
            public uint[]? FeatureRangeLengths { get; }
            public uint FeatureRanges { get; }

            public static DWriteFeatureRanges Create(IReadOnlyList<Vortice.DirectWrite.FontFeature>? fontFeatures, int textLength)
            {
                if (fontFeatures is null || fontFeatures.Count == 0 || textLength <= 0)
                    return Empty;

                var featurePointer = AllocateStructArray(fontFeatures);
                var typographicFeaturesPointer = IntPtr.Zero;

                try
                {
                    var typographicFeatures = new TypographicFeatures
                    {
                        Features = featurePointer,
                        FeatureCount = (uint)fontFeatures.Count
                    };

                    typographicFeaturesPointer = Marshal.AllocHGlobal(Marshal.SizeOf<TypographicFeatures>());
                    Marshal.StructureToPtr(typographicFeatures, typographicFeaturesPointer, false);

                    return new DWriteFeatureRanges(
                        typographicFeaturesPointer,
                        new[] { unchecked((uint)textLength) },
                        featureRanges: 1,
                        featurePointer,
                        typographicFeaturesPointer);
                }
                catch
                {
                    if (typographicFeaturesPointer != IntPtr.Zero)
                        Marshal.FreeHGlobal(typographicFeaturesPointer);
                    if (featurePointer != IntPtr.Zero)
                        Marshal.FreeHGlobal(featurePointer);
                    throw;
                }
            }

            public void Dispose()
            {
                if (_typographicFeaturesPointer != IntPtr.Zero)
                    Marshal.FreeHGlobal(_typographicFeaturesPointer);
                if (_featurePointer != IntPtr.Zero)
                    Marshal.FreeHGlobal(_featurePointer);
            }

            private static IntPtr AllocateStructArray<T>(IReadOnlyList<T> values) where T : struct
            {
                if (values.Count == 0)
                    return IntPtr.Zero;

                var size = Marshal.SizeOf<T>();
                var totalSize = checked(size * values.Count);
                var pointer = Marshal.AllocHGlobal(totalSize);

                for (var i = 0; i < values.Count; i++)
                {
                    Marshal.StructureToPtr(values[i], IntPtr.Add(pointer, i * size), false);
                }

                return pointer;
            }
        }

        private sealed class DirectWriteTextShaperTypeface : ITextShaperTypeface
        {
            public void Dispose()
            {
            }
        }

        private sealed unsafe class TextAnalysisSource : CallbackBase, IDWriteTextAnalysisSource
        {
            private int _textLength;
            private ReadingDirection _readingDirection;
            private nint _text;
            private nint _locale;

            public void Initialize(char* text, int textLength, char* locale, ReadingDirection readingDirection)
            {
                _textLength = textLength;
                _readingDirection = readingDirection;
                _text = (nint)text;
                _locale = (nint)locale;
            }

            uint IDWriteTextAnalysisSource.GetTextAtPosition(uint textPosition, nint textString)
            {
                if (textPosition >= _textLength || _text == 0)
                {
                    Marshal.WriteIntPtr(textString, IntPtr.Zero);
                    return 0;
                }

                Marshal.WriteIntPtr(textString, IntPtr.Add(_text, checked((int)textPosition * sizeof(char))));
                return (uint)(_textLength - textPosition);
            }

            uint IDWriteTextAnalysisSource.GetTextBeforePosition(uint textPosition, nint textString)
            {
                if (textPosition == 0 || _textLength == 0 || _text == 0)
                {
                    Marshal.WriteIntPtr(textString, IntPtr.Zero);
                    return 0;
                }

                var length = Math.Min((int)textPosition, _textLength);
                Marshal.WriteIntPtr(textString, _text);
                return (uint)length;
            }

            ReadingDirection IDWriteTextAnalysisSource.GetParagraphReadingDirection() => _readingDirection;

            uint IDWriteTextAnalysisSource.GetLocaleName(uint textPosition, nint localeName)
            {
                if (textPosition >= _textLength || _locale == 0)
                {
                    Marshal.WriteIntPtr(localeName, IntPtr.Zero);
                    return 0;
                }

                Marshal.WriteIntPtr(localeName, _locale);
                return (uint)(_textLength - textPosition);
            }

            void IDWriteTextAnalysisSource.GetNumberSubstitution(uint textPosition, out uint textLength, out IDWriteNumberSubstitution numberSubstitution)
            {
                textLength = textPosition >= _textLength ? 0 : (uint)(_textLength - textPosition);
                numberSubstitution = default!;
            }
        }

        private sealed class TextAnalysisSink : CallbackBase, IDWriteTextAnalysisSink
        {
            private readonly List<ScriptRun> _scriptRuns = new();
            private readonly List<BidiRun> _bidiRuns = new();

            public List<ScriptRun> ScriptRuns => _scriptRuns;
            public List<BidiRun> BidiRuns => _bidiRuns;

            public void Reset()
            {
                _scriptRuns.Clear();
                _bidiRuns.Clear();
            }

            void IDWriteTextAnalysisSink.SetScriptAnalysis(uint textPosition, uint textLength, ScriptAnalysis scriptAnalysis)
            {
                if (textLength == 0)
                    return;

                _scriptRuns.Add(new ScriptRun((int)textPosition, (int)textLength, scriptAnalysis));
            }

            void IDWriteTextAnalysisSink.SetLineBreakpoints(uint textPosition, LineBreakpoint[] lineBreakpoints)
            {
            }

            void IDWriteTextAnalysisSink.SetBidiLevel(uint textPosition, uint textLength, byte explicitLevel, byte resolvedLevel)
            {
                if (textLength == 0)
                    return;

                _bidiRuns.Add(new BidiRun((int)textPosition, (int)textLength, resolvedLevel));
            }

            void IDWriteTextAnalysisSink.SetNumberSubstitution(uint textPosition, uint textLength, IDWriteNumberSubstitution numberSubstitution)
            {
            }
        }
    }
}
