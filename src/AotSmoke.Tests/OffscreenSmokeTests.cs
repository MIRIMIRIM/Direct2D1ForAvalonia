using Avalonia;
using Avalonia.Win32;
using MIR.Direct2D1ForAvalonia;
using MIR.DirectWriteForAvalonia;

namespace AotSmoke.Tests;

[TestClass]
public sealed class OffscreenSmokeTests
{
    private static int s_initialized;

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public void Run_RendersAndVerifiesOffscreenImages()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("AotSmoke exercises Direct2D, DirectWrite and Windows imaging APIs.");
        }

        InitializeAvalonia();

        var outputDirectory = Path.Combine(Path.GetTempPath(), "AotSmoke.Tests", Guid.NewGuid().ToString("N"));
        var options = new SmokeOptions(
            AutoExit: true,
            MinFrames: 1,
            Timeout: TimeSpan.FromSeconds(10),
            OutputDirectory: outputDirectory,
            UiRepro: false,
            AfsRepro: false);

        OffscreenSmoke.Run(options);

        Assert.IsTrue(File.Exists(Path.Combine(outputDirectory, "offscreen.png")), "Expected the primary offscreen PNG.");
        Assert.IsTrue(File.Exists(Path.Combine(outputDirectory, "offscreen-edge.png")), "Expected the edge-case offscreen PNG.");
        Assert.IsTrue(File.Exists(Path.Combine(outputDirectory, "offscreen-dpi192.png")), "Expected the high-DPI offscreen PNG.");
        Assert.IsTrue(File.Exists(Path.Combine(outputDirectory, "offscreen-q15.jpg")), "Expected the low-quality JPEG.");
        Assert.IsTrue(File.Exists(Path.Combine(outputDirectory, "offscreen-q95.jpg")), "Expected the high-quality JPEG.");
    }

    private static void InitializeAvalonia()
    {
        if (Interlocked.Exchange(ref s_initialized, 1) != 0)
            return;

        AppBuilder.Configure<Application>()
            .UseWin32()
            .UseDirect2D1()
            .UseDirectWrite()
            .SetupWithoutStarting();
    }
}
