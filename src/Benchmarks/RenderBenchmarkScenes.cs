using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;

namespace Benchmarks;

/// <summary>
/// High-load rendering scenes designed to stress brush allocation, geometry
/// rasterisation, and the clip/layer pipeline — the areas where D2D and Skia
/// diverge most in per-frame allocation behaviour.
/// <para>
/// Mutable Avalonia brushes are pre-created where a real UI would also reuse them
/// (or use immutable brushes). Scenes still create enough distinct resources to
/// exercise backend caches without drowning measurements in per-cell managed noise.
/// </para>
/// </summary>
internal static class RenderBenchmarkScenes
{
    // Shared palette — mirrors real apps holding brush resources rather than allocating per cell.
    private static readonly IImmutableSolidColorBrush[] s_palette =
    [
        new ImmutableSolidColorBrush(Colors.Red),
        new ImmutableSolidColorBrush(Colors.Lime),
        new ImmutableSolidColorBrush(Colors.Blue),
        new ImmutableSolidColorBrush(Colors.Yellow),
        new ImmutableSolidColorBrush(Colors.Cyan),
        new ImmutableSolidColorBrush(Colors.Magenta),
        new ImmutableSolidColorBrush(Colors.Orange),
        new ImmutableSolidColorBrush(Colors.Purple),
        new ImmutableSolidColorBrush(Colors.Pink),
        new ImmutableSolidColorBrush(Colors.Teal),
        new ImmutableSolidColorBrush(Colors.Indigo),
        new ImmutableSolidColorBrush(Colors.Gold),
        new ImmutableSolidColorBrush(Colors.Coral),
        new ImmutableSolidColorBrush(Colors.Navy),
        new ImmutableSolidColorBrush(Colors.Olive),
        new ImmutableSolidColorBrush(Colors.Maroon),
    ];

    private static readonly IImmutableSolidColorBrush s_darkSlateGray = new ImmutableSolidColorBrush(Colors.DarkSlateGray);
    private static readonly IPen s_darkSlateGrayPen = new ImmutablePen(s_darkSlateGray, 1.5);
    private static readonly IPen s_blackPen = new ImmutablePen(Brushes.Black, 1);
    private static readonly IPen s_darkRedPen = new ImmutablePen(Brushes.DarkRed, 1);
    private static readonly IPen s_lightGrayPen = new ImmutablePen(Brushes.LightGray, 1);
    private static readonly IImmutableSolidColorBrush s_gold = new ImmutableSolidColorBrush(Colors.Gold);

    private static readonly IBrush[] s_linearBands = CreateLinearBands();
    private static readonly IBrush[] s_horizontalBands = CreateHorizontalBands();
    private static readonly IBrush[] s_radialCells = CreateRadialCells();
    private static readonly IBrush[] s_panelBrushes = CreatePanelBrushes();
    private static readonly IBrush[] s_clipLayerBrushes = CreateClipLayerBrushes();
    private static readonly IBrush[] s_roundedGridBrushes = CreateRoundedGridBrushes();
    private static readonly IBrush[] s_mixedBottomBrushes = CreateMixedBottomBrushes();
    private static readonly IBrush[] s_ellipseBrushes = CreateEllipseBrushes();

    // Shared synthetic image for ImageBlit — lazy so platform is initialised first.
    private static IImage? s_blitImage;

    public static readonly RenderBenchScene[] All =
    [
        new("SolidBrushGrid", new PixelSize(512, 512), new Vector(96, 96), 200, DrawSolidBrushGrid),
        new("GradientFill", new PixelSize(512, 512), new Vector(96, 96), 100, DrawGradientFill),
        new("RoundedRectGrid", new PixelSize(512, 512), new Vector(96, 96), 150, DrawRoundedRectGrid),
        new("ClipLayerHeavy", new PixelSize(512, 512), new Vector(96, 96), 80, DrawClipLayerHeavy),
        new("MixedScene", new PixelSize(512, 512), new Vector(96, 96), 100, DrawMixedScene),
        new("ImageBlit", new PixelSize(512, 512), new Vector(96, 96), 100, DrawImageBlit),
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
    /// Stress-tests solid-color brush resolution / device caching under high draw count.
    /// </summary>
    private static void DrawSolidBrushGrid(DrawingContext context)
    {
        context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 512, 512));

        var cellSize = 32;
        for (var y = 0; y < 512; y += cellSize)
        {
            for (var x = 0; x < 512; x += cellSize)
            {
                var brush = s_palette[((x / cellSize) + (y / cellSize)) % s_palette.Length];
                context.DrawRectangle(brush, null, new Rect(x, y, cellSize, cellSize));
            }
        }
    }

    /// <summary>
    /// Draws overlapping linear and radial gradient rectangles.
    /// Stress-tests gradient brush + gradient-stop-collection caching.
    /// </summary>
    private static void DrawGradientFill(DrawingContext context)
    {
        context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 512, 512));

        for (var i = 0; i < 8; i++)
        {
            var offset = i * 60;
            context.DrawRectangle(s_linearBands[i], null, new Rect(offset, offset, 120, 120));
        }

        for (var i = 0; i < 6; i++)
        {
            var cx = 60 + i * 70;
            context.DrawRectangle(s_radialCells[i], null, new Rect(cx, 300, 60, 60));
        }
    }

    /// <summary>
    /// Draws a grid of rounded rectangles with stroke pens.
    /// Stress-tests rounded-rect primitives and pen/brush caching.
    /// </summary>
    private static void DrawRoundedRectGrid(DrawingContext context)
    {
        context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 512, 512));

        var cellSize = 64;
        var index = 0;
        for (var y = 0; y < 512; y += cellSize)
        {
            for (var x = 0; x < 512; x += cellSize)
            {
                context.DrawRectangle(
                    s_roundedGridBrushes[index++],
                    s_darkSlateGrayPen,
                    new RoundedRect(new Rect(x + 4, y + 4, cellSize - 8, cellSize - 8), 8));
            }
        }
    }

    /// <summary>
    /// Draws many nested clip+opacity layers.
    /// Stress-tests D2D rounded-clip/opacity layering and Skia stencil/clip path.
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
                    context.DrawRectangle(s_clipLayerBrushes[i], null, rect);
                }
            }
        }
    }

    /// <summary>
    /// Tiles a shared bitmap across the surface. Stresses WIC→D2D GPU upload caching
    /// (<see cref="MIR.Direct2D1ForAvalonia.Media.WicBitmapImpl.GetDirect2DBitmap"/>).
    /// Steady-state should hit the device-scoped upload cache after the first frame.
    /// </summary>
    private static void DrawImageBlit(DrawingContext context)
    {
        context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 512, 512));

        var image = s_blitImage ??= CreateBlitImage();

        // 8×8 tiles of a 64×64 bitmap → 64 DrawImage calls per frame.
        for (var y = 0; y < 512; y += 64)
        {
            for (var x = 0; x < 512; x += 64)
            {
                context.DrawImage(image, new Rect(x, y, 64, 64));
            }
        }
    }

    private static IImage CreateBlitImage()
    {
        // WriteableBitmap → WriteableWicBitmapImpl (WIC), same path as loaded assets.
        const int size = 64;
        var bmp = new WriteableBitmap(
            new PixelSize(size, size),
            new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Premul);

        using (var fb = bmp.Lock())
        {
            var rowBytes = fb.RowBytes;
            var height = size;
            var width = size;
            // Fill a checker gradient without unsafe so AOT/unsafe project flags stay clean.
            var buffer = new byte[rowBytes * height];
            for (var y = 0; y < height; y++)
            {
                var row = y * rowBytes;
                for (var x = 0; x < width; x++)
                {
                    var i = row + x * 4;
                    buffer[i + 0] = (byte)(x * 4);
                    buffer[i + 1] = (byte)(y * 4);
                    buffer[i + 2] = (byte)(128 + (x ^ y));
                    buffer[i + 3] = 255;
                }
            }

            System.Runtime.InteropServices.Marshal.Copy(buffer, 0, fb.Address, buffer.Length);
        }

        return bmp;
    }

    /// <summary>
    /// A mixed scene combining solid fills, gradients, strokes, clips, and text.
    /// Approximates a real application frame composition.
    /// </summary>
    private static void DrawMixedScene(DrawingContext context)
    {
        context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 512, 512));

        for (var i = 0; i < 4; i++)
        {
            var rect = new Rect(16 + i * 124, 16, 112, 80);
            context.DrawRectangle(s_panelBrushes[i], s_blackPen, new RoundedRect(rect, 6));
        }

        for (var i = 0; i < 3; i++)
        {
            context.DrawRectangle(s_horizontalBands[i], null, new Rect(16 + i * 170, 112, 160, 24));
        }

        using (context.PushClip(new Rect(16, 152, 240, 120)))
        {
            for (var i = 0; i < 5; i++)
            {
                context.DrawEllipse(
                    s_ellipseBrushes[i],
                    s_darkRedPen,
                    new Rect(16 + i * 20, 152 + i * 10, 80, 80));
            }
        }

        using (context.PushOpacity(0.5))
        {
            for (var i = 0; i < 4; i++)
            {
                context.DrawRectangle(
                    s_gold,
                    null,
                    new RoundedRect(new Rect(280 + i * 50, 152, 40, 40), 6));
            }
        }

        for (var y = 300; y < 500; y += 16)
        {
            context.DrawLine(s_lightGrayPen, new Point(16, y), new Point(496, y));
        }

        for (var i = 0; i < 8; i++)
        {
            context.DrawRectangle(
                s_mixedBottomBrushes[i],
                null,
                new RoundedRect(new Rect(16 + i * 62, 430, 56, 56), 10));
        }
    }

    private static IBrush[] CreateLinearBands()
    {
        var brushes = new IBrush[8];
        for (var i = 0; i < brushes.Length; i++)
        {
            var offset = i * 60;
            brushes[i] = new ImmutableLinearGradientBrush(
                [
                    new ImmutableGradientStop(0, Color.FromRgb((byte)offset, 100, 200)),
                    new ImmutableGradientStop(1, Color.FromRgb(200, (byte)offset, 100)),
                ],
                opacity: 1,
                transform: null,
                transformOrigin: default,
                spreadMethod: GradientSpreadMethod.Pad,
                startPoint: new RelativePoint(0, 0, RelativeUnit.Relative),
                endPoint: new RelativePoint(1, 1, RelativeUnit.Relative));
        }
        return brushes;
    }

    private static IBrush[] CreateHorizontalBands()
    {
        var brushes = new IBrush[3];
        for (var i = 0; i < brushes.Length; i++)
        {
            brushes[i] = new ImmutableLinearGradientBrush(
                [
                    new ImmutableGradientStop(0, Colors.CornflowerBlue),
                    new ImmutableGradientStop(0.5, Colors.White),
                    new ImmutableGradientStop(1, Colors.CornflowerBlue),
                ],
                opacity: 1,
                transform: null,
                transformOrigin: default,
                spreadMethod: GradientSpreadMethod.Pad,
                startPoint: new RelativePoint(0, 0, RelativeUnit.Relative),
                endPoint: new RelativePoint(1, 0, RelativeUnit.Relative));
        }
        return brushes;
    }

    private static IBrush[] CreateRadialCells()
    {
        var brushes = new IBrush[6];
        for (var i = 0; i < brushes.Length; i++)
        {
            brushes[i] = new ImmutableRadialGradientBrush(
                [
                    new ImmutableGradientStop(0, Color.FromRgb(255, 240, 100)),
                    new ImmutableGradientStop(1, Color.FromRgb(20, 80, 180)),
                ],
                opacity: 1,
                transform: null,
                transformOrigin: default,
                spreadMethod: GradientSpreadMethod.Pad,
                center: RelativePoint.Center,
                gradientOrigin: new RelativePoint(0.3, 0.3, RelativeUnit.Relative),
                radius: 0.6);
        }
        return brushes;
    }

    private static IBrush[] CreatePanelBrushes()
    {
        var brushes = new IBrush[4];
        for (var i = 0; i < brushes.Length; i++)
            brushes[i] = new ImmutableSolidColorBrush(Color.FromArgb(220, (byte)(40 + i * 30), 100, 180));
        return brushes;
    }

    private static IBrush[] CreateClipLayerBrushes()
    {
        var brushes = new IBrush[12];
        for (var i = 0; i < brushes.Length; i++)
            brushes[i] = new ImmutableSolidColorBrush(HsvToColor(i * 30, 0.6, 0.9));
        return brushes;
    }

    private static IBrush[] CreateRoundedGridBrushes()
    {
        var brushes = new List<IBrush>(64);
        var cellSize = 64;
        for (var y = 0; y < 512; y += cellSize)
        {
            for (var x = 0; x < 512; x += cellSize)
            {
                var hue = ((x / cellSize) * 16 + (y / cellSize) * 4) % 360;
                brushes.Add(new ImmutableSolidColorBrush(HsvToColor(hue, 0.7, 0.9)));
            }
        }
        return brushes.ToArray();
    }

    private static IBrush[] CreateMixedBottomBrushes()
    {
        var brushes = new IBrush[8];
        for (var i = 0; i < brushes.Length; i++)
            brushes[i] = new ImmutableSolidColorBrush(HsvToColor(i * 45, 0.5, 0.85));
        return brushes;
    }

    private static IBrush[] CreateEllipseBrushes()
    {
        var brushes = new IBrush[5];
        for (var i = 0; i < brushes.Length; i++)
            brushes[i] = new ImmutableSolidColorBrush(Color.FromArgb(180, (byte)(200 - i * 20), 100, 50));
        return brushes;
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
