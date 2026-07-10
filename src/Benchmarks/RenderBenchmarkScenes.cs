using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Benchmarks;

/// <summary>
/// High-load rendering scenes designed to stress brush allocation, geometry
/// rasterisation, and the clip/layer pipeline — the areas where D2D and Skia
/// diverge most in per-frame allocation behaviour.
/// </summary>
internal static class RenderBenchmarkScenes
{
    public static readonly RenderBenchScene[] All =
    [
        new("SolidBrushGrid", new PixelSize(512, 512), new Vector(96, 96), 200, DrawSolidBrushGrid),
        new("GradientFill", new PixelSize(512, 512), new Vector(96, 96), 100, DrawGradientFill),
        new("RoundedRectGrid", new PixelSize(512, 512), new Vector(96, 96), 150, DrawRoundedRectGrid),
        new("ClipLayerHeavy", new PixelSize(512, 512), new Vector(96, 96), 80, DrawClipLayerHeavy),
        new("MixedScene", new PixelSize(512, 512), new Vector(96, 96), 100, DrawMixedScene),
    ];

    public static RenderBenchScene Get(string name)
    {
        foreach (var scene in All)
        {
            if (string.Equals(scene.Name, name, StringComparison.OrdinalIgnoreCase))
                return scene;
        }

        throw new ArgumentException(
            "Unknown scene. Supported scenes: " + string.Join(", ", All.Select(static x => x.Name)));
    }

    /// <summary>
    /// Draws a grid of solid-filled rectangles with 16 distinct colors repeated.
    /// Stress-tests solid-color brush allocation (the primary cache benefit).
    /// </summary>
    private static void DrawSolidBrushGrid(DrawingContext context)
    {
        context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 512, 512));

        var colors = new[]
        {
            Colors.Red, Colors.Lime, Colors.Blue, Colors.Yellow,
            Colors.Cyan, Colors.Magenta, Colors.Orange, Colors.Purple,
            Colors.Pink, Colors.Teal, Colors.Indigo, Colors.Gold,
            Colors.Coral, Colors.Navy, Colors.Olive, Colors.Maroon
        };

        var cellSize = 32;
        for (var y = 0; y < 512; y += cellSize)
        {
            for (var x = 0; x < 512; x += cellSize)
            {
                var color = colors[((x / cellSize) + (y / cellSize)) % colors.Length];
                context.DrawRectangle(new SolidColorBrush(color), null, new Rect(x, y, cellSize, cellSize));
            }
        }
    }

    /// <summary>
    /// Draws overlapping linear and radial gradient rectangles.
    /// Stress-tests gradient brush + gradient-stop-collection allocation.
    /// </summary>
    private static void DrawGradientFill(DrawingContext context)
    {
        context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 512, 512));

        for (var i = 0; i < 8; i++)
        {
            var offset = i * 60;
            context.DrawRectangle(
                new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromRgb((byte)(offset), 100, 200), 0),
                        new GradientStop(Color.FromRgb(200, (byte)(offset), 100), 1)
                    }
                },
                null,
                new Rect(offset, offset, 120, 120));
        }

        for (var i = 0; i < 6; i++)
        {
            var cx = 60 + i * 70;
            context.DrawRectangle(
                new RadialGradientBrush
                {
                    Center = RelativePoint.Center,
                    GradientOrigin = new RelativePoint(0.3, 0.3, RelativeUnit.Relative),
                    RadiusX = new RelativeScalar(0.6, RelativeUnit.Relative),
                    RadiusY = new RelativeScalar(0.6, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromRgb(255, 240, 100), 0),
                        new GradientStop(Color.FromRgb(20, 80, 180), 1)
                    }
                },
                null,
                new Rect(cx, 300, 60, 60));
        }
    }

    /// <summary>
    /// Draws a grid of rounded rectangles with stroke pens.
    /// Stress-tests rounded-rect geometry creation and pen/brush allocation.
    /// </summary>
    private static void DrawRoundedRectGrid(DrawingContext context)
    {
        context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 512, 512));

        var cellSize = 64;
        for (var y = 0; y < 512; y += cellSize)
        {
            for (var x = 0; x < 512; x += cellSize)
            {
                var hue = ((x / cellSize) * 16 + (y / cellSize) * 4) % 360;
                var color = HsvToColor(hue, 0.7, 0.9);
                context.DrawRectangle(
                    new SolidColorBrush(color),
                    new Pen(new SolidColorBrush(Colors.DarkSlateGray), 1.5),
                    new RoundedRect(new Rect(x + 4, y + 4, cellSize - 8, cellSize - 8), 8));
            }
        }
    }

    /// <summary>
    /// Draws many nested clip+opacity layers.
    /// Stress-tests the D2D layer pool and Skia stencil/clip path.
    /// </summary>
    private static void DrawClipLayerHeavy(DrawingContext context)
    {
        context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 512, 512));

        for (var i = 0; i < 12; i++)
        {
            var rect = new Rect(i * 8, i * 8, 512 - i * 16, 512 - i * 16);
            using (context.PushClip(new RoundedRect(rect, 12)))
            {
                using (context.PushOpacity(0.85))
                {
                    var color = HsvToColor(i * 30, 0.6, 0.9);
                    context.DrawRectangle(new SolidColorBrush(color), null, rect);
                }
            }
        }
    }

    /// <summary>
    /// A mixed scene combining solid fills, gradients, strokes, clips, and text.
    /// Approximates a real application frame composition.
    /// </summary>
    private static void DrawMixedScene(DrawingContext context)
    {
        context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 512, 512));

        // Solid-filled panels with borders
        for (var i = 0; i < 4; i++)
        {
            var rect = new Rect(16 + i * 124, 16, 112, 80);
            context.DrawRectangle(
                new SolidColorBrush(Color.FromArgb(220, (byte)(40 + i * 30), 100, 180)),
                new Pen(Brushes.Black, 1),
                new RoundedRect(rect, 6));
        }

        // Gradient bands
        for (var i = 0; i < 3; i++)
        {
            context.DrawRectangle(
                new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Colors.CornflowerBlue, 0),
                        new GradientStop(Colors.White, 0.5),
                        new GradientStop(Colors.CornflowerBlue, 1)
                    }
                },
                null,
                new Rect(16 + i * 170, 112, 160, 24));
        }

        // Clipped ellipse stack
        using (context.PushClip(new Rect(16, 152, 240, 120)))
        {
            for (var i = 0; i < 5; i++)
            {
                context.DrawEllipse(
                    new SolidColorBrush(Color.FromArgb(180, (byte)(200 - i * 20), 100, 50)),
                    new Pen(Brushes.DarkRed, 1),
                    new Rect(16 + i * 20, 152 + i * 10, 80, 80));
            }
        }

        // Opacity-layered shapes
        using (context.PushOpacity(0.5))
        {
            for (var i = 0; i < 4; i++)
            {
                context.DrawRectangle(
                    new SolidColorBrush(Colors.Gold),
                    null,
                    new RoundedRect(new Rect(280 + i * 50, 152, 40, 40), 6));
            }
        }

        // Line grid
        for (var y = 300; y < 500; y += 16)
        {
            context.DrawLine(new Pen(Brushes.LightGray, 1), new Point(16, y), new Point(496, y));
        }

        // Filled rounded rects
        for (var i = 0; i < 8; i++)
        {
            var color = HsvToColor(i * 45, 0.5, 0.85);
            context.DrawRectangle(
                new SolidColorBrush(color),
                null,
                new RoundedRect(new Rect(16 + i * 62, 430, 56, 56), 10));
        }
    }

    private static Color HsvToColor(double h, double s, double v)
    {
        h = h % 360;
        var c = v * s;
        var x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        var m = v - c;

        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}

internal sealed record RenderBenchScene(
    string Name,
    PixelSize Size,
    Vector Dpi,
    int Iterations,
    Action<DrawingContext> Render);
