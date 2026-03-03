using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Media.TextFormatting.Unicode;
using HarfBuzzSharp;
using Buffer = HarfBuzzSharp.Buffer;
using GlyphInfo = HarfBuzzSharp.GlyphInfo;

namespace TextParity.Baseline
{
    /// <summary>
    /// Baseline HarfBuzz shaping logic adapted from:
    /// repo: https://github.com/hez2010/Avalonia
    /// branch: vortice
    /// commit: 003e2d5063eed98d48a8a66d17b6c604a3401f49
    /// file: src/Windows/Avalonia.Direct2D1/Media/TextShaperImpl.cs
    ///
    /// Copyright and license follow upstream Avalonia terms.
    /// Used solely for parity testing against DirectWrite.
    /// </summary>
    internal static class HarfBuzzShaper
    {
        private static readonly ConcurrentDictionary<int, Language> s_cachedLanguage = new();

        public static IReadOnlyList<Avalonia.Media.TextFormatting.GlyphInfo> ShapeText(
            ReadOnlyMemory<char> text,
            HarfBuzzSharp.Font font,
            IGlyphTypeface typeface,
            double fontRenderingEmSize,
            sbyte bidiLevel,
            CultureInfo culture,
            IReadOnlyList<FontFeature>? fontFeatures = null,
            double letterSpacing = 0,
            double incrementalTabWidth = 0)
        {
            var textSpan = text.Span;

            using (var buffer = new Buffer())
            {
                var containingText = GetContainingMemory(text, out var start, out var length).Span;
                buffer.AddUtf16(containingText, start, length);

                MergeBreakPair(buffer);

                buffer.GuessSegmentProperties();

                buffer.Direction = (bidiLevel & 1) == 0 ? Direction.LeftToRight : Direction.RightToLeft;

                var usedCulture = culture ?? CultureInfo.CurrentCulture;
                buffer.Language = s_cachedLanguage.GetOrAdd(usedCulture.LCID, _ => new Language(usedCulture));

                font.Shape(buffer, GetFeatures(fontFeatures));

                if (buffer.Direction == Direction.RightToLeft)
                {
                    buffer.Reverse();
                }

                font.GetScale(out var scaleX, out _);
                var textScale = fontRenderingEmSize / scaleX;

                var bufferLength = buffer.Length;
                var glyphInfos = buffer.GetGlyphInfoSpan();
                var glyphPositions = buffer.GetGlyphPositionSpan();

                var results = new Avalonia.Media.TextFormatting.GlyphInfo[bufferLength];

                for (var i = 0; i < bufferLength; i++)
                {
                    var sourceInfo = glyphInfos[i];
                    var glyphIndex = (ushort)sourceInfo.Codepoint;
                    var glyphCluster = (int)(sourceInfo.Cluster);
                    
                    var glyphAdvance = GetGlyphAdvance(glyphPositions, i, textScale) + letterSpacing;
                    var glyphOffset = GetGlyphOffset(glyphPositions, i, textScale);

                    if (glyphCluster < containingText.Length && containingText[glyphCluster] == '\t')
                    {
                        glyphIndex = typeface.GetGlyph(' ');
                        glyphAdvance = incrementalTabWidth > 0 ?
                            incrementalTabWidth :
                            4 * typeface.GetGlyphAdvance(glyphIndex) * textScale;
                    }

                    results[i] = new Avalonia.Media.TextFormatting.GlyphInfo(glyphIndex, glyphCluster, glyphAdvance, glyphOffset);
                }

                return results;
            }
        }

        private static void MergeBreakPair(Buffer buffer)
        {
            var length = buffer.Length;
            if (length == 0) return;

            var glyphInfos = buffer.GetGlyphInfoSpan();
            var second = glyphInfos[length - 1];

            if (!new Codepoint(second.Codepoint).IsBreakChar)
            {
                return;
            }

            if (length > 1 && glyphInfos[length - 2].Codepoint == '\r' && second.Codepoint == '\n')
            {
                var first = glyphInfos[length - 2];
                first.Codepoint = '\u200C';
                second.Codepoint = '\u200C';
                second.Cluster = first.Cluster;

                unsafe
                {
                    fixed (GlyphInfo* p = &glyphInfos[length - 2]) { *p = first; }
                    fixed (GlyphInfo* p = &glyphInfos[length - 1]) { *p = second; }
                }
            }
            else
            {
                second.Codepoint = '\u200C';
                unsafe
                {
                    fixed (GlyphInfo* p = &glyphInfos[length - 1]) { *p = second; }
                }
            }
        }

        private static Vector GetGlyphOffset(ReadOnlySpan<GlyphPosition> glyphPositions, int index, double textScale)
        {
            var position = glyphPositions[index];
            var offsetX = position.XOffset * textScale;
            var offsetY = position.YOffset * textScale;
            return new Vector(offsetX, offsetY);
        }

        private static double GetGlyphAdvance(ReadOnlySpan<GlyphPosition> glyphPositions, int index, double textScale)
        {
            return glyphPositions[index].XAdvance * textScale;
        }

        private static ReadOnlyMemory<char> GetContainingMemory(ReadOnlyMemory<char> memory, out int start, out int length)
        {
            if (MemoryMarshal.TryGetString(memory, out var containingString, out start, out length))
            {
                return containingString.AsMemory();
            }
            if (MemoryMarshal.TryGetArray(memory, out var segment))
            {
                start = segment.Offset;
                length = segment.Count;
                if (segment.Array is not null)
                    return segment.Array.AsMemory();
            }
            if (MemoryMarshal.TryGetMemoryManager(memory, out MemoryManager<char>? memoryManager, out start, out length)
                && memoryManager is not null)
            {
                return memoryManager.Memory;
            }
            throw new InvalidOperationException("Memory not backed by string, array or manager");
        }

        private static Feature[] GetFeatures(IReadOnlyList<FontFeature>? fontFeatures)
        {
            if (fontFeatures is null || fontFeatures.Count == 0)
                return Array.Empty<Feature>();

            var features = new Feature[fontFeatures.Count];
            var featureIndex = 0;

            for (var i = 0; i < fontFeatures.Count; i++)
            {
                var feature = fontFeatures[i];

                if (string.IsNullOrWhiteSpace(feature.Tag) || feature.Tag.Length != 4)
                    continue;

                var start = feature.Start < 0 ? 0u : (uint)feature.Start;
                var end = feature.End < 0 ? uint.MaxValue : (uint)Math.Max(feature.End, feature.Start);
                var value = feature.Value < 0 ? 0u : (uint)feature.Value;

                features[featureIndex++] = new Feature(Tag.Parse(feature.Tag), value, start, end);
            }

            if (featureIndex == features.Length)
                return features;

            var trimmed = new Feature[featureIndex];
            Array.Copy(features, trimmed, featureIndex);
            return trimmed;
        }
    }
}
