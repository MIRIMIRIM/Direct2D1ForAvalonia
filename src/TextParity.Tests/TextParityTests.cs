namespace TextParity.Tests;

[TestClass]
public sealed class TextParityTests
{
    private static readonly string[] s_expectedKnownDivergences =
    [
        "Devanagari",
        "RTL Trailing CRLF",
        "Surrogate Pair (Emoji kerning)",
        "Variable font Latin",
    ];

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public void DefaultCases_HaveNoUnexpectedTextParityFailures()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("TextParity exercises DirectWrite and Windows font assets.");
        }

        var runRoot = Path.Combine(Path.GetTempPath(), "TextParity.Tests", Guid.NewGuid().ToString("N"));
        var outputDirectory = Path.Combine(runRoot, "out");
        var reportPath = Path.Combine(runRoot, "report.md");

        var summary = Program.Run(
            new Program.CliOptions(
                OutDir: outputDirectory,
                ReportPath: reportPath),
            TextWriter.Null);

        Assert.AreEqual(0, summary.Tier1Failures, FormatUnexpectedFailures(summary));
        Assert.AreEqual(0, summary.Tier2Failures, FormatUnexpectedFailures(summary));
        Assert.AreEqual(0, summary.Skipped, FormatSkipped(summary));
        Assert.AreEqual(s_expectedKnownDivergences.Length, summary.Known, FormatUnexpectedFailures(summary));

        var knownNames = summary.Results
            .Where(static result => !result.Passed && !result.Skipped)
            .Select(static result => result.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(s_expectedKnownDivergences, knownNames);
        Assert.IsTrue(File.Exists(reportPath), "TextParity should still emit a CLI-compatible report.");
    }

    private static string FormatUnexpectedFailures(RunSummary summary)
    {
        var unexpected = summary.Results
            .Where(static result => !result.Passed && !result.Skipped)
            .Where(static result => !s_expectedKnownDivergences.Contains(result.Name, StringComparer.Ordinal))
            .Select(static result => $"{result.Tier} {result.Name}: {result.Message}")
            .ToArray();

        return unexpected.Length == 0
            ? "No unexpected TextParity failures were captured."
            : string.Join(Environment.NewLine, unexpected);
    }

    private static string FormatSkipped(RunSummary summary)
    {
        var skipped = summary.Results
            .Where(static result => result.Skipped)
            .Select(static result => $"{result.Tier} {result.Name}: {result.Message}")
            .ToArray();

        return skipped.Length == 0
            ? "No TextParity cases were skipped."
            : string.Join(Environment.NewLine, skipped);
    }
}
