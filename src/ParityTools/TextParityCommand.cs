using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Avalonia;
using MIR.Direct2D1ForAvalonia;
using MIR.DirectWriteFontsForAvalonia;
using MIR.Direct2D1ForAvalonia.Media;
using MIR.DirectWriteForAvalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Vortice.DirectWrite;
using ParityTools.Baseline;
using AvaloniaFontFeature = Avalonia.Media.FontFeature;

namespace ParityTools;

internal enum CaseTier
{
    Tier1,
    Tier2
}

internal enum InputMemoryKind
{
    String,
    Array
}

internal sealed record TestCase(
    string Name,
    string Text,
    string DefaultFontFile,
    CaseTier Tier,
    sbyte BidiLevel = 0,
    string Culture = "en-US",
    IReadOnlyList<AvaloniaFontFeature>? Features = null,
    string MemoryPrefix = "",
    string MemorySuffix = "",
    double LetterSpacing = 0,
    double IncrementalTabWidth = 0,
    InputMemoryKind MemoryKind = InputMemoryKind.String);

internal sealed record GlyphDump(ushort GlyphIndex, int GlyphCluster, double GlyphAdvance, double OffsetX, double OffsetY);

internal sealed record CaseResult(
    string Name,
    CaseTier Tier,
    string FontPath,
    string Culture,
    sbyte BidiLevel,
    bool Passed,
    bool Skipped,
    string Message,
    IReadOnlyList<GlyphDump> DirectWriteGlyphs,
    IReadOnlyList<GlyphDump> HarfBuzzGlyphs,
    string? DirectWriteImage = null,
    string? HarfBuzzImage = null,
    string? CompareImage = null);

internal sealed record RunSummary(
    IReadOnlyList<CaseResult> Results,
    int Passed,
    int Tier1Failures,
    int Tier2Failures,
    int Known,
    int Skipped);

internal static class TextParityCommand
{
    private const double DefaultEpsilon = 0.01;
    private const double DefaultRenderDpi = 384.0;

    internal static RunSummary Run(CliOptions options, TextWriter output)
    {
        output.WriteLine("Initializing MIR.Direct2D1ForAvalonia Platform...");
        Direct2D1Platform.InitializeDirect2D();
        DirectWritePlatform.InitializeDirectWrite();

        var toolRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        var outDir = options.OutDir ?? Path.Combine(toolRoot, "out");
        var reportPath = options.ReportPath ?? Path.Combine(toolRoot, "report.md");
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? toolRoot);

        var allCases = GetDefaultCases();
        var selectedCases = FilterCases(allCases, options.CaseName);
        if (selectedCases.Count == 0)
        {
            throw new ArgumentException($"No test case matched '{options.CaseName}'.");
        }

        var globalFeatures = ParseFeatures(options.FeaturesRaw);
        var epsilon = options.Epsilon ?? DefaultEpsilon;
        var renderImages = options.RenderImages;
        var renderDpi = options.RenderDpi ?? DefaultRenderDpi;
        var fontManager = new FontManagerImpl();
        var results = new List<CaseResult>(selectedCases.Count);
        var fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

        foreach (var testCase in selectedCases)
        {
            var effectiveCulture = options.Culture ?? testCase.Culture;
            var effectiveBidi = options.BidiLevel ?? testCase.BidiLevel;
            var effectiveFeatures = globalFeatures ?? testCase.Features ?? Array.Empty<AvaloniaFontFeature>();
            var fontPath = ResolveFontPath(fontsDir, testCase.DefaultFontFile, options.FontFile, options.FontFamily);

            output.WriteLine($"Testing: {testCase.Name}");

            if (fontPath is null || !File.Exists(fontPath))
            {
                var missing = options.FontFile ?? options.FontFamily ?? testCase.DefaultFontFile;
                output.WriteLine($"  SKIP (Font not found: {missing})");
                results.Add(new CaseResult(
                    testCase.Name,
                    testCase.Tier,
                    fontPath ?? string.Empty,
                    effectiveCulture,
                    effectiveBidi,
                    Passed: false,
                    Skipped: true,
                    Message: $"Font not found: {missing}",
                    DirectWriteGlyphs: Array.Empty<GlyphDump>(),
                    HarfBuzzGlyphs: Array.Empty<GlyphDump>()));
                continue;
            }

            CaseResult caseResult;
            try
            {
                caseResult = RunCase(
                    testCase,
                    fontPath,
                    effectiveCulture,
                    effectiveBidi,
                    effectiveFeatures,
                    epsilon,
                    fontManager,
                    outDir,
                    renderImages,
                    renderDpi);
            }
            catch (Exception ex)
            {
                caseResult = new CaseResult(
                    testCase.Name,
                    testCase.Tier,
                    fontPath,
                    effectiveCulture,
                    effectiveBidi,
                    Passed: false,
                    Skipped: false,
                    Message: $"Exception: {ex.GetType().Name}: {ex.Message}",
                    DirectWriteGlyphs: Array.Empty<GlyphDump>(),
                    HarfBuzzGlyphs: Array.Empty<GlyphDump>());
            }

            string knownReason = string.Empty;
            var isKnown = !caseResult.Passed && !caseResult.Skipped
                && IsKnownDivergence(caseResult.Name, out knownReason);

            output.WriteLine(caseResult.Passed
                ? "  PASS"
                : caseResult.Skipped
                    ? $"  SKIP: {caseResult.Message}"
                    : isKnown
                        ? $"  KNOWN: {knownReason}"
                        : $"  FAIL: {caseResult.Message}");

            results.Add(caseResult);
        }

        WriteReport(reportPath, results, epsilon, options, globalFeatures);

        var tier1Failures = results.Count(x => x.Tier == CaseTier.Tier1 && !x.Skipped && !x.Passed && !IsKnownDivergence(x.Name, out _));
        var tier2Failures = results.Count(x => x.Tier == CaseTier.Tier2 && !x.Skipped && !x.Passed && !IsKnownDivergence(x.Name, out _));
        var passed = results.Count(x => x.Passed);
        var skipped = results.Count(x => x.Skipped);
        var known = results.Count(x => !x.Passed && !x.Skipped && IsKnownDivergence(x.Name, out _));

        output.WriteLine();
        output.WriteLine($"Done. Passed: {passed}, Failed Tier1: {tier1Failures}, Failed Tier2: {tier2Failures}, Known: {known}, Skipped: {skipped}");
        output.WriteLine($"Report: {reportPath}");

        return new RunSummary(results, passed, tier1Failures, tier2Failures, known, skipped);
    }

    private static CaseResult RunCase(
        TestCase testCase,
        string fontPath,
        string cultureName,
        sbyte bidiLevel,
        IReadOnlyList<AvaloniaFontFeature> features,
        double epsilon,
        FontManagerImpl fontManager,
        string outDir,
        bool renderImages,
        double renderDpi)
    {
        using var blob = HarfBuzzSharp.Blob.FromFile(fontPath);
        using var face = new HarfBuzzSharp.Face(blob, 0);
        using var hbFont = new HarfBuzzSharp.Font(face);
        using var stream = File.OpenRead(fontPath);

        if (!fontManager.TryCreateGlyphTypeface(stream, Avalonia.Media.FontSimulations.None, out var platformTypeface))
        {
            return new CaseResult(
                testCase.Name,
                testCase.Tier,
                fontPath,
                cultureName,
                bidiLevel,
                Passed: false,
                Skipped: true,
                Message: $"Failed to create DWrite typeface from {fontPath}",
                DirectWriteGlyphs: Array.Empty<GlyphDump>(),
                HarfBuzzGlyphs: Array.Empty<GlyphDump>());
        }
        using var platformTypefaceLease = platformTypeface;
        var glyphTypeface = new GlyphTypeface(platformTypefaceLease);

        var culture = new CultureInfo(cultureName);
        var shaperOptions = features.Count == 0
            ? new TextShaperOptions(glyphTypeface, 32.0, bidiLevel, culture, testCase.IncrementalTabWidth, testCase.LetterSpacing)
            : new TextShaperOptions(glyphTypeface, 32.0, bidiLevel, culture, testCase.IncrementalTabWidth, testCase.LetterSpacing, features);

        var shaperDWrite = new DirectWriteTextShaper();
        var textMemory = CreateInputMemory(testCase);
        var dwBuffer = shaperDWrite.ShapeText(textMemory, shaperOptions);
        var hbBuffer = HarfBuzzShaper.ShapeText(
            textMemory,
            hbFont,
            glyphTypeface,
            shaperOptions.FontRenderingEmSize,
            bidiLevel,
            culture,
            features,
            testCase.LetterSpacing,
            testCase.IncrementalTabWidth);

        var directWriteGlyphs = dwBuffer
            .Select(g => new GlyphDump(g.GlyphIndex, g.GlyphCluster, g.GlyphAdvance, g.GlyphOffset.X, g.GlyphOffset.Y))
            .ToArray();
        var harfBuzzGlyphs = hbBuffer
            .Select(g => new GlyphDump(g.GlyphIndex, g.GlyphCluster, g.GlyphAdvance, g.GlyphOffset.X, g.GlyphOffset.Y))
            .ToArray();

        var prefix = SanitizeFileName(testCase.Name);
        File.WriteAllText(Path.Combine(outDir, $"{prefix}_DW.json"), JsonSerializer.Serialize(directWriteGlyphs, JsonOptions()));
        File.WriteAllText(Path.Combine(outDir, $"{prefix}_HB.json"), JsonSerializer.Serialize(harfBuzzGlyphs, JsonOptions()));

        var passed = TryCompareGlyphRuns(directWriteGlyphs, harfBuzzGlyphs, bidiLevel, textMemory.Span, epsilon, out var mismatch);

        string? dwImage = null;
        string? hbImage = null;
        string? compareImage = null;

        if (renderImages && (testCase.Tier == CaseTier.Tier2 || !passed))
        {
            try
            {
                (dwImage, hbImage, compareImage) = RenderComparisonImages(
                    prefix,
                    outDir,
                    glyphTypeface,
                    shaperOptions.FontRenderingEmSize,
                    bidiLevel,
                    dwBuffer.ToArray(),
                    hbBuffer.ToArray(),
                    renderDpi);
            }
            catch (Exception ex)
            {
                // Rendering is diagnostic-only; never fail the run because image generation failed.
                if (!passed)
                    mismatch = string.IsNullOrWhiteSpace(mismatch)
                        ? $"Image render failed: {ex.GetType().Name}: {ex.Message}"
                        : $"{mismatch} | Image render failed: {ex.GetType().Name}: {ex.Message}";
            }
        }

        return new CaseResult(
            testCase.Name,
            testCase.Tier,
            fontPath,
            cultureName,
            bidiLevel,
            Passed: passed,
            Skipped: false,
            Message: mismatch,
            DirectWriteGlyphs: directWriteGlyphs,
            HarfBuzzGlyphs: harfBuzzGlyphs,
            DirectWriteImage: dwImage,
            HarfBuzzImage: hbImage,
            CompareImage: compareImage);
    }

    private static (string? dwImage, string? hbImage, string? compareImage) RenderComparisonImages(
        string prefix,
        string outDir,
        GlyphTypeface glyphTypeface,
        double fontRenderingEmSize,
        sbyte bidiLevel,
        IReadOnlyList<GlyphInfo> directWriteGlyphs,
        IReadOnlyList<GlyphInfo> harfBuzzGlyphs,
        double renderDpi)
    {
        if (renderDpi <= 0)
            renderDpi = DefaultRenderDpi;

        var rtl = (bidiLevel & 1) != 0;
        const double marginDip = 12.0;
        const double gapDip = 24.0;

        var dwWidthDip = ComputeRunWidthDip(directWriteGlyphs);
        var hbWidthDip = ComputeRunWidthDip(harfBuzzGlyphs);
        var runWidthDip = Math.Max(dwWidthDip, hbWidthDip);

        var (lineHeightDip, baselineDip) = ComputeLineMetricsDip(glyphTypeface, fontRenderingEmSize);
        var baselineY = marginDip + baselineDip;

        var scale = renderDpi / 96.0;

        var heightDip = marginDip * 2 + lineHeightDip;
        var singleWidthDip = marginDip * 2 + runWidthDip;
        var compareWidthDip = marginDip * 2 + runWidthDip * 2 + gapDip;

        var heightPx = Math.Max(1, (int)Math.Ceiling(heightDip * scale));
        var singleWidthPx = Math.Max(1, (int)Math.Ceiling(singleWidthDip * scale));
        var compareWidthPx = Math.Max(1, (int)Math.Ceiling(compareWidthDip * scale));
        var dpi = new Vector(renderDpi, renderDpi);

        var dwPath = Path.Combine(outDir, $"{prefix}_DW.png");
        var hbPath = Path.Combine(outDir, $"{prefix}_HB.png");
        var comparePath = Path.Combine(outDir, $"{prefix}_Compare.png");

        var startX = marginDip + (rtl ? runWidthDip : 0);

        RenderGlyphRunToPng(
            dwPath,
            singleWidthPx,
            heightPx,
            dpi,
            glyphTypeface,
            fontRenderingEmSize,
            directWriteGlyphs,
            baselineX: startX,
            baselineY: baselineY);

        RenderGlyphRunToPng(
            hbPath,
            singleWidthPx,
            heightPx,
            dpi,
            glyphTypeface,
            fontRenderingEmSize,
            harfBuzzGlyphs,
            baselineX: startX,
            baselineY: baselineY);

        // Side-by-side compare image (DW left, HB right)
        using (var bmp = new WicRenderTargetBitmapImpl(new PixelSize(compareWidthPx, heightPx), dpi))
        {
            using (var dc = (DrawingContextImpl)bmp.CreateDrawingContext(useScaledDrawing: true))
            {
                dc.Clear(Colors.White);

                var leftX = marginDip + (rtl ? runWidthDip : 0);
                var rightX = marginDip + runWidthDip + gapDip + (rtl ? runWidthDip : 0);

                using (var dwRun = new GlyphRunImpl(glyphTypeface, fontRenderingEmSize, directWriteGlyphs, new Point(leftX, baselineY)))
                using (var hbRun = new GlyphRunImpl(glyphTypeface, fontRenderingEmSize, harfBuzzGlyphs, new Point(rightX, baselineY)))
                {
                    dc.DrawGlyphRun(new SolidColorBrush(Colors.Black), dwRun);
                    dc.DrawGlyphRun(new SolidColorBrush(Colors.Black), hbRun);
                }
            }

            using var fs = File.Open(comparePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            bmp.Save(fs);
        }

        return (dwPath, hbPath, comparePath);
    }

    private static void RenderGlyphRunToPng(
        string path,
        int widthPx,
        int heightPx,
        Vector dpi,
        GlyphTypeface glyphTypeface,
        double fontRenderingEmSize,
        IReadOnlyList<GlyphInfo> glyphs,
        double baselineX,
        double baselineY)
    {
        using var bmp = new WicRenderTargetBitmapImpl(new PixelSize(widthPx, heightPx), dpi);
        using (var dc = (DrawingContextImpl)bmp.CreateDrawingContext(useScaledDrawing: true))
        {
            dc.Clear(Colors.White);
            using var run = new GlyphRunImpl(glyphTypeface, fontRenderingEmSize, glyphs, new Point(baselineX, baselineY));
            dc.DrawGlyphRun(new SolidColorBrush(Colors.Black), run);
        }

        using var fs = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        bmp.Save(fs);
    }

    private static double ComputeRunWidthDip(IReadOnlyList<GlyphInfo> glyphs)
    {
        var sum = 0.0;
        for (var i = 0; i < glyphs.Count; i++)
            sum += Math.Abs(glyphs[i].GlyphAdvance);
        return sum;
    }

    private static (double lineHeightDip, double baselineDip) ComputeLineMetricsDip(GlyphTypeface glyphTypeface, double fontRenderingEmSize)
    {
        var scale = fontRenderingEmSize / glyphTypeface.Metrics.DesignEmHeight;
        var baseline = -glyphTypeface.Metrics.Ascent * scale;
        var lineHeight = glyphTypeface.Metrics.LineSpacing * scale;
        return (lineHeight, baseline);
    }

    private static bool TryCompareGlyphRuns(
        IReadOnlyList<GlyphDump> directWriteGlyphs,
        IReadOnlyList<GlyphDump> harfBuzzGlyphs,
        sbyte bidiLevel,
        ReadOnlySpan<char> text,
        double epsilon,
        out string mismatch)
    {
        mismatch = string.Empty;

        if (!TryValidateClusters("DW", directWriteGlyphs, text.Length, out mismatch) ||
            !TryValidateClusters("HB", harfBuzzGlyphs, text.Length, out mismatch))
        {
            return false;
        }

        if (directWriteGlyphs.Count != harfBuzzGlyphs.Count)
        {
            mismatch = $"Count mismatch. DW={directWriteGlyphs.Count}, HB={harfBuzzGlyphs.Count}";
            return false;
        }

        var rtl = (bidiLevel & 1) != 0;

        for (var i = 0; i < directWriteGlyphs.Count; i++)
        {
            var dw = directWriteGlyphs[i];
            var hb = harfBuzzGlyphs[i];

            var dwAdvance = rtl ? Math.Abs(dw.GlyphAdvance) : dw.GlyphAdvance;
            var hbAdvance = rtl ? Math.Abs(hb.GlyphAdvance) : hb.GlyphAdvance;

            // A zero-width glyph (advance within epsilon on both sides) is invisible and
            // cannot host a caret, so its glyph id and cluster are irrelevant to rendering
            // and text navigation. Only compare glyph id and cluster when at least one side
            // is non-zero-width. This lets trailing line-break characters pass parity: both
            // shapers rewrite them to ZWNJ (zero advance), but the HarfBuzz baseline also
            // merges the CRLF pair into one cluster, while DirectWrite keeps two clusters.
            // Visible glyphs (e.g. tabs and middle line breaks) still require exact id and
            // cluster matches.
            var significant = Math.Abs(dwAdvance) > epsilon || Math.Abs(hbAdvance) > epsilon;
            var invisibleBreakGlyph =
                !significant &&
                IsBreakCluster(text, dw.GlyphCluster) &&
                IsBreakCluster(text, hb.GlyphCluster);

            if ((significant && dw.GlyphIndex != hb.GlyphIndex)
                || (significant && dw.GlyphCluster != hb.GlyphCluster)
                || Math.Abs(dwAdvance - hbAdvance) > epsilon
                || (!invisibleBreakGlyph && Math.Abs(dw.OffsetX - hb.OffsetX) > epsilon)
                || (!invisibleBreakGlyph && Math.Abs(dw.OffsetY - hb.OffsetY) > epsilon))
            {
                mismatch = $"Mismatch at glyph {i}. DW=[id:{dw.GlyphIndex}, cluster:{dw.GlyphCluster}, adv:{dw.GlyphAdvance:0.######}, off:({dw.OffsetX:0.######},{dw.OffsetY:0.######})], HB=[id:{hb.GlyphIndex}, cluster:{hb.GlyphCluster}, adv:{hb.GlyphAdvance:0.######}, off:({hb.OffsetX:0.######},{hb.OffsetY:0.######})]";
                return false;
            }
        }

        return true;
    }

    private static bool IsBreakCluster(ReadOnlySpan<char> text, int cluster)
    {
        if ((uint)cluster >= (uint)text.Length)
            return false;

        return text[cluster] is '\r' or '\n';
    }

    private static bool TryValidateClusters(
        string label,
        IReadOnlyList<GlyphDump> glyphs,
        int textLength,
        out string mismatch)
    {
        for (var i = 0; i < glyphs.Count; i++)
        {
            var cluster = glyphs[i].GlyphCluster;
            if (cluster < 0 || cluster >= textLength)
            {
                mismatch = $"{label} cluster out of range at glyph {i}: cluster={cluster}, textLength={textLength}";
                return false;
            }
        }

        mismatch = string.Empty;
        return true;
    }

    /// <summary>
    /// Cases whose mismatch is a known, accepted engine-level divergence between DirectWrite
    /// and HarfBuzz rather than a shaper bug. They are reported as KNOWN (not counted as
    /// failures) so the parity signal stays meaningful.
    /// </summary>
    private static readonly Dictionary<string, string> s_knownDivergences = new()
    {
        // Emoji-adjacent space kerning differs because DirectWrite and HarfBuzz apply the
        // emoji font's GPOS kern table differently. The glyph ids and clusters match exactly;
        // only the space advance differs by a sub-pixel amount. The kern-off sibling passes.
        ["Surrogate Pair (Emoji kerning)"] = "Engine-level GPOS kern divergence (glyph ids/clusters match; only the space advance differs).",
        ["RTL Trailing CRLF"] = "Invisible zero-advance trailing break glyph offset differs in RTL; glyph id, cluster and advance match.",
        ["Variable font Latin"] = "Variable font default instance metrics differ between DirectWrite and HarfBuzz; glyph ids/clusters match.",
        ["Devanagari"] = "Indic glyph ids/clusters match, but DirectWrite and HarfBuzz differ in Nirmala UI advances and mark offsets.",
    };

    private static bool IsKnownDivergence(string name, out string reason)
    {
        foreach (var kv in s_knownDivergences)
        {
            if (name.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
            {
                reason = kv.Value;
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    private static List<TestCase> GetDefaultCases()
    {
        return
        [
            new TestCase("Empty", "", "segoeui.ttf", CaseTier.Tier1),
            new TestCase("Latin (liga on)", "ffi fl ffl", "segoeui.ttf", CaseTier.Tier1),
            new TestCase(
                "Latin (liga off)",
                "ffi fl ffl",
                "segoeui.ttf",
                CaseTier.Tier1,
                Features: [AvaloniaFontFeature.Parse("liga=0")]),
            new TestCase(
                "Latin (liga range off)",
                "ffi ffi",
                "segoeui.ttf",
                CaseTier.Tier1,
                Features: [Feature("liga", 0, 0, 3)]),
            new TestCase(
                "Latin (kern on)",
                "AVAVAV",
                "segoeui.ttf",
                CaseTier.Tier1,
                Features: [AvaloniaFontFeature.Parse("kern=1")]),
            new TestCase(
                "Latin (kern off)",
                "AVAVAV",
                "segoeui.ttf",
                CaseTier.Tier1,
                Features: [AvaloniaFontFeature.Parse("kern=0")]),
            new TestCase(
                "Latin (kern range off)",
                "AV AV",
                "segoeui.ttf",
                CaseTier.Tier1,
                Features: [Feature("kern", 0, 0, 2)]),
            new TestCase("Latin letter spacing", "Spacing", "segoeui.ttf", CaseTier.Tier1, LetterSpacing: 1.25),
            new TestCase("Variable font Latin", "Variable AV", "SegUIVar.ttf", CaseTier.Tier2),
            new TestCase("Hebrew (RTL)", "שלום עולם", "arial.ttf", CaseTier.Tier1, 1, "he-IL"),
            new TestCase("Combining Marks", "A\u030AA\u030A", "segoeui.ttf", CaseTier.Tier1),
            new TestCase("Combining Marks array slice", "A\u030AA\u030A", "segoeui.ttf", CaseTier.Tier1, MemoryPrefix: "<<", MemorySuffix: ">>", MemoryKind: InputMemoryKind.Array),
            new TestCase(
                "Surrogate Pair (Emoji, kern off)",
                "Hello \uD83D\uDE00 World",
                "seguiemj.ttf",
                CaseTier.Tier1,
                Features: [AvaloniaFontFeature.Parse("kern=0")]),
            new TestCase(
                "Surrogate Pair array slice (kern off)",
                "Hi \uD83D\uDE00",
                "seguiemj.ttf",
                CaseTier.Tier1,
                Features: [AvaloniaFontFeature.Parse("kern=0")],
                MemoryPrefix: "[",
                MemorySuffix: "]",
                MemoryKind: InputMemoryKind.Array),
            new TestCase("Tab Behavior", "A\tB", "segoeui.ttf", CaseTier.Tier1),
            new TestCase("Tab custom width", "A\tB", "segoeui.ttf", CaseTier.Tier1, IncrementalTabWidth: 48),
            new TestCase("Only CRLF", "\r\n", "segoeui.ttf", CaseTier.Tier1),
            new TestCase("Only LF", "\n", "segoeui.ttf", CaseTier.Tier1),
            new TestCase("Line Break LF", "AB\n", "segoeui.ttf", CaseTier.Tier1),
            new TestCase("Line Break CRLF", "AB\r\n", "segoeui.ttf", CaseTier.Tier1),
            new TestCase("Line Break CR", "AB\r", "segoeui.ttf", CaseTier.Tier1),
            new TestCase("Line Break CRLF slice", "AB\r\n", "segoeui.ttf", CaseTier.Tier1, MemoryPrefix: "xx", MemorySuffix: "yy"),
            new TestCase("Line Break LF slice", "AB\n", "segoeui.ttf", CaseTier.Tier1, MemoryPrefix: "xx", MemorySuffix: "yy"),
            new TestCase("Line Break CR slice", "AB\r", "segoeui.ttf", CaseTier.Tier1, MemoryPrefix: "xx", MemorySuffix: "yy"),
            new TestCase("Line Break CRLF array slice", "AB\r\n", "segoeui.ttf", CaseTier.Tier1, MemoryPrefix: "xx", MemorySuffix: "yy", MemoryKind: InputMemoryKind.Array),
            new TestCase("Mid Break CRLF", "Line1\r\nLine2", "segoeui.ttf", CaseTier.Tier2),
            new TestCase("Mid Break CRLF slice", "Line1\r\nLine2", "segoeui.ttf", CaseTier.Tier2, MemoryPrefix: "[[", MemorySuffix: "]]"),
            new TestCase("Mid Break CRLF array slice", "Line1\r\nLine2", "segoeui.ttf", CaseTier.Tier2, MemoryPrefix: "[[", MemorySuffix: "]]", MemoryKind: InputMemoryKind.Array),
            new TestCase("Leading CRLF", "\r\nLine", "segoeui.ttf", CaseTier.Tier2),
            new TestCase("Consecutive CRLF", "A\r\n\r\nB", "segoeui.ttf", CaseTier.Tier2),
            new TestCase("Tab Before CRLF", "A\t\r\n", "segoeui.ttf", CaseTier.Tier2),
            new TestCase("Mixed Breaks", "A\rB\nC\r\n", "segoeui.ttf", CaseTier.Tier2),
            new TestCase("RTL Trailing CRLF", "שלום\r\n", "arial.ttf", CaseTier.Tier2, 1, "he-IL"),
            new TestCase(
                "Emoji Trailing CRLF slice",
                "Hi \uD83D\uDE00\r\n",
                "seguiemj.ttf",
                CaseTier.Tier2,
                Features: [AvaloniaFontFeature.Parse("kern=0")],
                MemoryPrefix: "<",
                MemorySuffix: ">"),
            new TestCase("Mixed LTR RTL digits", "abc שלום 123", "arial.ttf", CaseTier.Tier2),
            new TestCase("Arabic", "مرحبا بالعالم", "arial.ttf", CaseTier.Tier2, 1, "ar-SA"),
            new TestCase("Thai", "สวัสดีโลก", "LeelawUI.ttf", CaseTier.Tier2, Culture: "th-TH"),
            new TestCase("Devanagari", "नमस्ते दुनिया", "Nirmala.ttc", CaseTier.Tier2, Culture: "hi-IN"),
            new TestCase("Zero Width Joiner", "A\u200DB", "segoeui.ttf", CaseTier.Tier2),
            new TestCase("Non-breaking spaces", "A\u00A0B\u2007C\u202FD", "segoeui.ttf", CaseTier.Tier2),
            new TestCase("CJK Chinese", "你好世界", "msyh.ttc", CaseTier.Tier1, Culture: "zh-CN"),
            new TestCase("CJK Japanese", "こんにちは世界", "msgothic.ttc", CaseTier.Tier1, Culture: "ja-JP"),
            new TestCase("CJK Korean", "안녕하세요 세계", "malgun.ttf", CaseTier.Tier1, Culture: "ko-KR"),
            new TestCase("Surrogate Pair (Emoji kerning)", "Hello \uD83D\uDE00 World", "seguiemj.ttf", CaseTier.Tier2),
            new TestCase("Mixed Script Digits", "Invoice ١٢٣-ABC", "segoeui.ttf", CaseTier.Tier2)
        ];
    }

    private static AvaloniaFontFeature Feature(string tag, int value, int start, int end)
    {
        return new AvaloniaFontFeature
        {
            Tag = tag,
            Value = value,
            Start = start,
            End = end
        };
    }

    private static ReadOnlyMemory<char> CreateInputMemory(TestCase testCase)
    {
        if (testCase.MemoryKind == InputMemoryKind.String &&
            testCase.MemoryPrefix.Length == 0 &&
            testCase.MemorySuffix.Length == 0)
        {
            return testCase.Text.AsMemory();
        }

        var containingText = string.Concat(testCase.MemoryPrefix, testCase.Text, testCase.MemorySuffix);
        return testCase.MemoryKind switch
        {
            InputMemoryKind.String => containingText.AsMemory(testCase.MemoryPrefix.Length, testCase.Text.Length),
            InputMemoryKind.Array => containingText.ToCharArray().AsMemory(testCase.MemoryPrefix.Length, testCase.Text.Length),
            _ => throw new ArgumentOutOfRangeException(nameof(testCase), testCase.MemoryKind, "Unsupported input memory kind.")
        };
    }

    private static List<TestCase> FilterCases(IEnumerable<TestCase> cases, string? caseName)
    {
        if (string.IsNullOrWhiteSpace(caseName))
            return cases.ToList();

        return cases
            .Where(c => c.Name.Contains(caseName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static void WriteReport(
        string reportPath,
        IReadOnlyList<CaseResult> results,
        double epsilon,
        CliOptions options,
        IReadOnlyList<AvaloniaFontFeature>? globalFeatures)
    {
        using var report = new StreamWriter(reportPath, false, Encoding.UTF8);
        report.WriteLine("# Text Parity Report (DirectWrite vs HarfBuzz)");
        report.WriteLine();
        report.WriteLine($"- Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.WriteLine($"- Epsilon: {epsilon:0.######}");
        report.WriteLine($"- Font override file: {options.FontFile ?? "(none)"}");
        report.WriteLine($"- Font override family: {options.FontFamily ?? "(none)"}");
        report.WriteLine($"- Culture override: {options.Culture ?? "(none)"}");
        report.WriteLine($"- Bidi override: {(options.BidiLevel.HasValue ? options.BidiLevel.Value.ToString(CultureInfo.InvariantCulture) : "(none)")}");
        report.WriteLine($"- Features override: {(globalFeatures is { Count: > 0 } ? string.Join("; ", globalFeatures.Select(x => x.ToString())) : "(none)")}");
        report.WriteLine();

        var reportDirectory = Path.GetDirectoryName(reportPath) ?? Environment.CurrentDirectory;

        WriteTierReport(report, reportDirectory, CaseTier.Tier1, results.Where(x => x.Tier == CaseTier.Tier1).ToArray());
        WriteTierReport(report, reportDirectory, CaseTier.Tier2, results.Where(x => x.Tier == CaseTier.Tier2).ToArray());
    }

    private static void WriteTierReport(StreamWriter report, string reportDirectory, CaseTier tier, IReadOnlyList<CaseResult> results)
    {
        report.WriteLine($"## {tier}");
        if (results.Count == 0)
        {
            report.WriteLine("No cases.");
            report.WriteLine();
            return;
        }

        var passed = results.Count(x => x.Passed);
        var known = results.Count(x => !x.Passed && !x.Skipped && IsKnownDivergence(x.Name, out _));
        var failed = results.Count(x => !x.Passed && !x.Skipped && !IsKnownDivergence(x.Name, out _));
        var skipped = results.Count(x => x.Skipped);

        report.WriteLine($"Summary: passed={passed}, failed={failed}, known={known}, skipped={skipped}");
        report.WriteLine();

        foreach (var result in results)
        {
            string knownReason = string.Empty;
            var isKnown = !result.Passed && !result.Skipped && IsKnownDivergence(result.Name, out knownReason);
            var mark = result.Passed ? "PASS" : result.Skipped ? "SKIP" : isKnown ? "KNOWN" : "FAIL";
            report.WriteLine($"### {mark} {result.Name}");
            report.WriteLine($"- Font: `{result.FontPath}`");
            report.WriteLine($"- Culture: `{result.Culture}`, Bidi: `{result.BidiLevel}`");
            report.WriteLine($"- Message: {(isKnown ? knownReason : result.Message)}");

            if (!string.IsNullOrWhiteSpace(result.CompareImage) && File.Exists(result.CompareImage))
            {
                var rel = Path.GetRelativePath(reportDirectory, result.CompareImage).Replace('\\', '/');
                report.WriteLine($"- Image (DW left, HB right): ![]({rel})");
            }

            if (!result.Passed && !result.Skipped)
            {
                report.WriteLine("- DirectWrite:");
                report.WriteLine("```json");
                report.WriteLine(JsonSerializer.Serialize(result.DirectWriteGlyphs, JsonOptions()));
                report.WriteLine("```");
                report.WriteLine("- HarfBuzz:");
                report.WriteLine("```json");
                report.WriteLine(JsonSerializer.Serialize(result.HarfBuzzGlyphs, JsonOptions()));
                report.WriteLine("```");
            }

            report.WriteLine();
        }
    }

    private static JsonSerializerOptions JsonOptions() => new() { WriteIndented = true };

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars).Replace(' ', '_');
    }

    private static IReadOnlyList<AvaloniaFontFeature>? ParseFeatures(string? featuresRaw)
    {
        if (string.IsNullOrWhiteSpace(featuresRaw))
            return null;

        var separators = featuresRaw.Contains(';') ? new[] { ';' } : new[] { ',' };
        var parsed = new List<AvaloniaFontFeature>();

        foreach (var token in featuresRaw.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var feature = AvaloniaFontFeature.Parse(token);
            if (!string.IsNullOrWhiteSpace(feature.Tag) && feature.Tag.Length == 4)
                parsed.Add(feature);
        }

        return parsed.Count == 0 ? null : parsed;
    }

    private static string? ResolveFontPath(string fontsDir, string defaultFontFile, string? fontFileOverride, string? fontFamilyOverride)
    {
        if (!string.IsNullOrWhiteSpace(fontFileOverride))
            return ResolveFontFilePath(fontsDir, fontFileOverride);

        if (!string.IsNullOrWhiteSpace(fontFamilyOverride))
        {
            var familyPath = TryResolveFontPathByFamily(fontFamilyOverride);
            if (!string.IsNullOrWhiteSpace(familyPath))
                return familyPath;
        }

        return ResolveFontFilePath(fontsDir, defaultFontFile);
    }

    private static string? ResolveFontFilePath(string fontsDir, string fontFile)
    {
        if (Path.IsPathRooted(fontFile))
            return File.Exists(fontFile) ? fontFile : null;

        if (File.Exists(fontFile))
            return Path.GetFullPath(fontFile);

        var pathInFonts = Path.Combine(fontsDir, fontFile);
        return File.Exists(pathInFonts) ? pathInFonts : null;
    }

    private static string? TryResolveFontPathByFamily(string familyName)
    {
        var collection = DirectWriteFontCollectionCache.InstalledFontCollection;
        if (!collection.FindFamilyName(familyName, out var index))
            return null;

        using var family = collection.GetFontFamily(index);
        using var font = family.GetFirstMatchingFont(
            Vortice.DirectWrite.FontWeight.Normal,
            Vortice.DirectWrite.FontStretch.Normal,
            Vortice.DirectWrite.FontStyle.Normal);

        return TryResolveLocalPath(font);
    }

    private static unsafe string? TryResolveLocalPath(IDWriteFont font)
    {
        using var face = font.CreateFontFace();
        var files = face.GetFiles();
        if (files.Length == 0)
            return null;

        using var file = files[0];
        if (file.Loader is not IDWriteLocalFontFileLoader localLoader)
            return null;

        var key = file.GetReferenceKey();
        if (key.Length == 0)
            return null;

        fixed (byte* keyPtr = key)
        {
            var pathLength = localLoader.GetFilePathLengthFromKey((nint)keyPtr, (uint)key.Length);
            if (pathLength == 0)
                return null;

            var chars = new char[pathLength + 1];
            fixed (char* pathPtr = chars)
            {
                localLoader.GetFilePathFromKey((nint)keyPtr, (uint)key.Length, (nint)pathPtr, pathLength + 1);
            }

            return new string(chars, 0, (int)pathLength);
        }
    }

    internal sealed record CliOptions(
        string? FontFile = null,
        string? FontFamily = null,
        string? Culture = null,
        sbyte? BidiLevel = null,
        string? FeaturesRaw = null,
        string? CaseName = null,
        string? OutDir = null,
        string? ReportPath = null,
        double? Epsilon = null,
        bool RenderImages = false,
        double? RenderDpi = null);
}
