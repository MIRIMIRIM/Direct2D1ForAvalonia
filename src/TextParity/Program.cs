using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Direct2D1;
using Avalonia.Direct2D1.Media;
using Avalonia.DirectWrite;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Vortice.DirectWrite;
using TextParity.Baseline;
using AvaloniaFontFeature = Avalonia.Media.FontFeature;

namespace TextParity;

internal enum CaseTier
{
    Tier1,
    Tier2
}

internal sealed record TestCase(
    string Name,
    string Text,
    string DefaultFontFile,
    CaseTier Tier,
    sbyte BidiLevel = 0,
    string Culture = "en-US",
    IReadOnlyList<AvaloniaFontFeature>? Features = null);

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

internal static class Program
{
    private const double DefaultEpsilon = 0.01;
    private const double DefaultRenderDpi = 384.0;

    public static int Main(string[] args)
    {
        if (!TryParseArguments(args, out var options, out var parseError))
        {
            Console.Error.WriteLine(parseError);
            PrintUsage();
            return 2;
        }

        Console.WriteLine("Initializing Avalonia.Direct2D1 Platform...");
        Direct2D1Platform.InitializeDirect2D();
        Avalonia.DirectWrite.DirectWritePlatform.InitializeDirectWrite();

        var toolRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        var outDir = options.OutDir ?? Path.Combine(toolRoot, "out");
        var reportPath = options.ReportPath ?? Path.Combine(toolRoot, "report.md");
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? toolRoot);

        var allCases = GetDefaultCases();
        var selectedCases = FilterCases(allCases, options.CaseName);
        if (selectedCases.Count == 0)
        {
            Console.Error.WriteLine($"No test case matched '{options.CaseName}'.");
            return 2;
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

            Console.WriteLine($"Testing: {testCase.Name}");

            if (fontPath is null || !File.Exists(fontPath))
            {
                var missing = options.FontFile ?? options.FontFamily ?? testCase.DefaultFontFile;
                Console.WriteLine($"  SKIP (Font not found: {missing})");
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

            Console.WriteLine(caseResult.Passed
                ? "  PASS"
                : caseResult.Skipped
                    ? $"  SKIP: {caseResult.Message}"
                    : $"  FAIL: {caseResult.Message}");

            results.Add(caseResult);
        }

        WriteReport(reportPath, results, epsilon, options, globalFeatures);

        var tier1Failures = results.Count(x => x.Tier == CaseTier.Tier1 && !x.Skipped && !x.Passed);
        var tier2Failures = results.Count(x => x.Tier == CaseTier.Tier2 && !x.Skipped && !x.Passed);
        var passed = results.Count(x => x.Passed);
        var skipped = results.Count(x => x.Skipped);

        Console.WriteLine();
        Console.WriteLine($"Done. Passed: {passed}, Failed Tier1: {tier1Failures}, Failed Tier2: {tier2Failures}, Skipped: {skipped}");
        Console.WriteLine($"Report: {reportPath}");

        return tier1Failures == 0 ? 0 : 1;
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
            ? new TextShaperOptions(glyphTypeface, 32.0, bidiLevel, culture, 0, 0)
            : new TextShaperOptions(glyphTypeface, 32.0, bidiLevel, culture, 0, 0, features);

        var shaperDWrite = new DirectWriteTextShaper();
        var dwBuffer = shaperDWrite.ShapeText(testCase.Text.AsMemory(), shaperOptions);
        var hbBuffer = HarfBuzzShaper.ShapeText(testCase.Text.AsMemory(), hbFont, glyphTypeface, shaperOptions.FontRenderingEmSize, bidiLevel, culture, features);

        var directWriteGlyphs = dwBuffer
            .Select(g => new GlyphDump(g.GlyphIndex, g.GlyphCluster, g.GlyphAdvance, g.GlyphOffset.X, g.GlyphOffset.Y))
            .ToArray();
        var harfBuzzGlyphs = hbBuffer
            .Select(g => new GlyphDump(g.GlyphIndex, g.GlyphCluster, g.GlyphAdvance, g.GlyphOffset.X, g.GlyphOffset.Y))
            .ToArray();

        var prefix = SanitizeFileName(testCase.Name);
        File.WriteAllText(Path.Combine(outDir, $"{prefix}_DW.json"), JsonSerializer.Serialize(directWriteGlyphs, JsonOptions()));
        File.WriteAllText(Path.Combine(outDir, $"{prefix}_HB.json"), JsonSerializer.Serialize(harfBuzzGlyphs, JsonOptions()));

        var passed = TryCompareGlyphRuns(directWriteGlyphs, harfBuzzGlyphs, bidiLevel, epsilon, out var mismatch);

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
        double epsilon,
        out string mismatch)
    {
        mismatch = string.Empty;

        if (directWriteGlyphs.Count != harfBuzzGlyphs.Count)
        {
            mismatch = $"Count mismatch. DW={directWriteGlyphs.Count}, HB={harfBuzzGlyphs.Count}";
            return false;
        }

        var rtl = (bidiLevel & 1) != 0;

        Console.WriteLine($"--- Diagnostic Dump ---");
        Console.WriteLine($"rtl={rtl}, Length={directWriteGlyphs.Count}");
        for (int i=0; i<directWriteGlyphs.Count; i++) {
            Console.WriteLine($"  {i}: DW=[{directWriteGlyphs[i].GlyphIndex}, cl:{directWriteGlyphs[i].GlyphCluster}]  HB=[{harfBuzzGlyphs[i].GlyphIndex}, cl:{harfBuzzGlyphs[i].GlyphCluster}]");
        }
        Console.WriteLine($"-----------------------");

        for (var i = 0; i < directWriteGlyphs.Count; i++)
        {
            var dw = directWriteGlyphs[i];
            var hb = harfBuzzGlyphs[i];

            var dwAdvance = rtl ? Math.Abs(dw.GlyphAdvance) : dw.GlyphAdvance;
            var hbAdvance = rtl ? Math.Abs(hb.GlyphAdvance) : hb.GlyphAdvance;

            if (dw.GlyphIndex != hb.GlyphIndex
                || dw.GlyphCluster != hb.GlyphCluster
                || Math.Abs(dwAdvance - hbAdvance) > epsilon
                || Math.Abs(dw.OffsetX - hb.OffsetX) > epsilon
                || Math.Abs(dw.OffsetY - hb.OffsetY) > epsilon)
            {
                mismatch = $"Mismatch at glyph {i}. DW=[id:{dw.GlyphIndex}, cluster:{dw.GlyphCluster}, adv:{dw.GlyphAdvance:0.######}, off:({dw.OffsetX:0.######},{dw.OffsetY:0.######})], HB=[id:{hb.GlyphIndex}, cluster:{hb.GlyphCluster}, adv:{hb.GlyphAdvance:0.######}, off:({hb.OffsetX:0.######},{hb.OffsetY:0.######})]";
                return false;
            }
        }

        return true;
    }

    private static List<TestCase> GetDefaultCases()
    {
        return
        [
            new TestCase("Latin (liga on)", "ffi fl ffl", "segoeui.ttf", CaseTier.Tier1),
            new TestCase(
                "Latin (liga off)",
                "ffi fl ffl",
                "segoeui.ttf",
                CaseTier.Tier1,
                Features: [AvaloniaFontFeature.Parse("liga=0")]),
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
            new TestCase("Hebrew (RTL)", "שלום עולם", "arial.ttf", CaseTier.Tier1, 1, "he-IL"),
            new TestCase("Combining Marks", "A\u030AA\u030A", "segoeui.ttf", CaseTier.Tier1),
            new TestCase(
                "Surrogate Pair (Emoji, kern off)",
                "Hello \uD83D\uDE00 World",
                "seguiemj.ttf",
                CaseTier.Tier1,
                Features: [AvaloniaFontFeature.Parse("kern=0")]),
            new TestCase("Tab Behavior", "A\tB", "segoeui.ttf", CaseTier.Tier1),
            new TestCase("CJK Chinese", "你好世界", "msyh.ttc", CaseTier.Tier1, Culture: "zh-CN"),
            new TestCase("CJK Japanese", "こんにちは世界", "msgothic.ttc", CaseTier.Tier1, Culture: "ja-JP"),
            new TestCase("CJK Korean", "안녕하세요 세계", "malgun.ttf", CaseTier.Tier1, Culture: "ko-KR"),
            new TestCase("Surrogate Pair (Emoji kerning)", "Hello \uD83D\uDE00 World", "seguiemj.ttf", CaseTier.Tier2),
            new TestCase("Mixed Script Digits", "Invoice ١٢٣-ABC", "segoeui.ttf", CaseTier.Tier2)
        ];
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
        var failed = results.Count(x => !x.Passed && !x.Skipped);
        var skipped = results.Count(x => x.Skipped);

        report.WriteLine($"Summary: passed={passed}, failed={failed}, skipped={skipped}");
        report.WriteLine();

        foreach (var result in results)
        {
            var mark = result.Passed ? "PASS" : result.Skipped ? "SKIP" : "FAIL";
            report.WriteLine($"### {mark} {result.Name}");
            report.WriteLine($"- Font: `{result.FontPath}`");
            report.WriteLine($"- Culture: `{result.Culture}`, Bidi: `{result.BidiLevel}`");
            report.WriteLine($"- Message: {result.Message}");

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

    private static bool TryParseArguments(string[] args, out CliOptions options, out string error)
    {
        options = new CliOptions();
        error = string.Empty;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string NextValue()
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException($"Missing value for {arg}");
                i++;
                return args[i];
            }

            try
            {
                switch (arg)
                {
                    case "--font-file":
                        options = options with { FontFile = NextValue() };
                        break;
                    case "--font-family":
                        options = options with { FontFamily = NextValue() };
                        break;
                    case "--culture":
                        options = options with { Culture = NextValue() };
                        break;
                    case "--bidi":
                        options = options with { BidiLevel = sbyte.Parse(NextValue(), CultureInfo.InvariantCulture) };
                        break;
                    case "--features":
                        options = options with { FeaturesRaw = NextValue() };
                        break;
                    case "--case":
                        options = options with { CaseName = NextValue() };
                        break;
                    case "--out-dir":
                        options = options with { OutDir = Path.GetFullPath(NextValue()) };
                        break;
                    case "--report":
                        options = options with { ReportPath = Path.GetFullPath(NextValue()) };
                        break;
                    case "--epsilon":
                        options = options with { Epsilon = double.Parse(NextValue(), CultureInfo.InvariantCulture) };
                        break;
                    case "--render":
                        options = options with { RenderImages = true };
                        break;
                    case "--render-dpi":
                        options = options with { RenderDpi = double.Parse(NextValue(), CultureInfo.InvariantCulture) };
                        break;
                    case "--help":
                    case "-h":
                        PrintUsage();
                        Environment.Exit(0);
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument: {arg}");
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        return true;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("TextParity options:");
        Console.WriteLine("  --font-file <path-or-font-file-name>");
        Console.WriteLine("  --font-family <installed-family-name>");
        Console.WriteLine("  --culture <culture-name>");
        Console.WriteLine("  --bidi <sbyte-level>");
        Console.WriteLine("  --features <feature1;feature2>");
        Console.WriteLine("  --case <case-name-contains>");
        Console.WriteLine("  --out-dir <directory>");
        Console.WriteLine("  --report <report-path>");
        Console.WriteLine("  --epsilon <float>");
        Console.WriteLine("  --render (emit PNG comparison images for Tier-2 and failing cases)");
        Console.WriteLine($"  --render-dpi <float> (default: {DefaultRenderDpi:0.#})");
    }

    private sealed record CliOptions(
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
