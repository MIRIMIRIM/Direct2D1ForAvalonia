using System.CommandLine;
using System.Globalization;

namespace ParityTools;

internal static class Program
{
    public static int Main(string[] args)
    {
        var rootCommand = new RootCommand("Direct2D1/DirectWrite parity validation tools.");
        rootCommand.Subcommands.Add(CreateTextCommand());
        rootCommand.Subcommands.Add(CreateRenderCommand());
        rootCommand.Subcommands.Add(CreateRenderWorkerCommand());

        return rootCommand.Parse(args).Invoke();
    }

    private static Command CreateTextCommand()
    {
        var fontFileOption = new Option<string?>("--font-file")
        {
            Description = "Path or file name of the font to use instead of each case default."
        };
        var fontFamilyOption = new Option<string?>("--font-family")
        {
            Description = "Installed font family name to use instead of each case default."
        };
        var cultureOption = new Option<string?>("--culture")
        {
            Description = "Culture name override, for example en-US or zh-CN."
        };
        var bidiOption = new Option<sbyte?>("--bidi")
        {
            Description = "Bidi level override."
        };
        var featuresOption = new Option<string?>("--features")
        {
            Description = "OpenType font features, separated by semicolon or comma."
        };
        var caseOption = new Option<string?>("--case")
        {
            Description = "Run cases whose names contain this value."
        };
        var outDirOption = new Option<string?>("--out-dir")
        {
            Description = "Directory for JSON and optional image artifacts."
        };
        var reportOption = new Option<string?>("--report")
        {
            Description = "Markdown report path."
        };
        var epsilonOption = new Option<double?>("--epsilon")
        {
            Description = "Glyph metric comparison epsilon."
        };
        var renderOption = new Option<bool>("--render")
        {
            Description = "Emit PNG comparison images for Tier-2 and failing cases."
        };
        var renderDpiOption = new Option<double?>("--render-dpi")
        {
            Description = "DPI used for diagnostic PNG comparison images."
        };

        var command = new Command("text", "Compare DirectWrite shaping with a HarfBuzz baseline.");
        command.Options.Add(fontFileOption);
        command.Options.Add(fontFamilyOption);
        command.Options.Add(cultureOption);
        command.Options.Add(bidiOption);
        command.Options.Add(featuresOption);
        command.Options.Add(caseOption);
        command.Options.Add(outDirOption);
        command.Options.Add(reportOption);
        command.Options.Add(epsilonOption);
        command.Options.Add(renderOption);
        command.Options.Add(renderDpiOption);

        command.SetAction(parseResult =>
        {
            var options = new TextParityCommand.CliOptions(
                FontFile: GetFullPathOrNull(parseResult.GetValue(fontFileOption)),
                FontFamily: parseResult.GetValue(fontFamilyOption),
                Culture: parseResult.GetValue(cultureOption),
                BidiLevel: parseResult.GetValue(bidiOption),
                FeaturesRaw: parseResult.GetValue(featuresOption),
                CaseName: parseResult.GetValue(caseOption),
                OutDir: GetFullPathOrNull(parseResult.GetValue(outDirOption)),
                ReportPath: GetFullPathOrNull(parseResult.GetValue(reportOption)),
                Epsilon: parseResult.GetValue(epsilonOption),
                RenderImages: parseResult.GetValue(renderOption),
                RenderDpi: parseResult.GetValue(renderDpiOption));

            try
            {
                var summary = TextParityCommand.Run(options, Console.Out);
                return summary.Tier1Failures == 0 ? 0 : 1;
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }
        });

        return command;
    }

    private static Command CreateRenderCommand()
    {
        var sceneOption = new Option<string?>("--scene")
        {
            Description = "Comma-separated scene names. Omit to run every scene."
        };
        var outDirOption = new Option<string?>("--out-dir")
        {
            Description = "Directory for PNG, raw BGRA and JSON report artifacts."
        };
        var reportJsonOption = new Option<string?>("--report-json")
        {
            Description = "JSON report path."
        };
        var pixelToleranceOption = new Option<int>("--pixel-tolerance")
        {
            Description = "Per-channel tolerance before a pixel is counted as over tolerance.",
            DefaultValueFactory = _ => 64
        };
        var maxMeanDeltaOption = new Option<double>("--max-mean-delta")
        {
            Description = "Maximum allowed mean per-channel delta.",
            DefaultValueFactory = _ => 8.0
        };
        var maxPixelsOverTolerancePercentOption = new Option<double>("--max-pixels-over-tolerance-percent")
        {
            Description = "Maximum percent of pixels whose max channel delta exceeds tolerance.",
            DefaultValueFactory = _ => 5.0
        };

        var command = new Command("render", "Compare Direct2D/DirectWrite rendering with Skia/HarfBuzz rendering.");
        command.Options.Add(sceneOption);
        command.Options.Add(outDirOption);
        command.Options.Add(reportJsonOption);
        command.Options.Add(pixelToleranceOption);
        command.Options.Add(maxMeanDeltaOption);
        command.Options.Add(maxPixelsOverTolerancePercentOption);

        command.SetAction(parseResult =>
        {
            var options = new RenderParityOptions(
                Backend: null,
                Scenes: ParseScenes(parseResult.GetValue(sceneOption)),
                OutputDirectory: GetFullPathOrNull(parseResult.GetValue(outDirOption)),
                ReportJson: GetFullPathOrNull(parseResult.GetValue(reportJsonOption)),
                PngPath: null,
                RawPath: null,
                PixelTolerance: parseResult.GetValue(pixelToleranceOption),
                MaxMeanChannelDelta: parseResult.GetValue(maxMeanDeltaOption),
                MaxPixelsOverTolerancePercent: parseResult.GetValue(maxPixelsOverTolerancePercentOption));

            return RenderParityCommand.Run(options);
        });

        return command;
    }

    private static Command CreateRenderWorkerCommand()
    {
        var backendOption = new Option<string>("--backend")
        {
            Description = "Renderer backend: d2d or skia.",
            Required = true
        };
        var sceneOption = new Option<string>("--scene")
        {
            Description = "Single scene to render.",
            Required = true
        };
        var pngOption = new Option<string>("--png")
        {
            Description = "PNG output path.",
            Required = true
        };
        var rawOption = new Option<string>("--raw")
        {
            Description = "Raw BGRA output path.",
            Required = true
        };

        var command = new Command("render-worker", "Internal render parity worker process.");
        command.Options.Add(backendOption);
        command.Options.Add(sceneOption);
        command.Options.Add(pngOption);
        command.Options.Add(rawOption);

        command.SetAction(parseResult =>
        {
            var options = new RenderParityOptions(
                Backend: parseResult.GetValue(backendOption),
                Scenes: ParseScenes(parseResult.GetValue(sceneOption)),
                OutputDirectory: null,
                ReportJson: null,
                PngPath: GetFullPathOrNull(parseResult.GetValue(pngOption)),
                RawPath: GetFullPathOrNull(parseResult.GetValue(rawOption)),
                PixelTolerance: 64,
                MaxMeanChannelDelta: 8.0,
                MaxPixelsOverTolerancePercent: 5.0);

            return RenderParityCommand.Run(options);
        });

        return command;
    }

    private static HashSet<string> ParseScenes(string? scenes)
    {
        var parsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(scenes))
        {
            return parsed;
        }

        foreach (var part in scenes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            parsed.Add(part);
        }

        return parsed;
    }

    private static string? GetFullPathOrNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);
    }
}
