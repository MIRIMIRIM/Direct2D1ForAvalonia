using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Controls;

if (!OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("AotSmoke targets Windows only.");

AppBuilder.Configure<SmokeApp>()
    .UsePlatformDetect()
    .UseDirect2D1()
    .UseHarfBuzz()
    .SetupWithoutStarting();

using var bitmap = new RenderTargetBitmap(new PixelSize(256, 128), new Vector(96, 96));
using (var context = bitmap.CreateDrawingContext(false))
{
    context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 256, 128));
    context.DrawLine(new Pen(Brushes.Black, 1), new Point(0, 0), new Point(255, 127));

    var text = new FormattedText(
        "AOT Smoke",
        CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight,
        new Typeface("Segoe UI"),
        18,
        Brushes.DarkBlue);

    context.DrawText(text, new Point(8, 8));
}

Console.WriteLine("AOT smoke finished.");

internal sealed class SmokeApp : Application
{
}
