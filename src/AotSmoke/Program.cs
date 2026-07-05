using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.Win32;
using MIR.Direct2D1ForAvalonia;
using MIR.DirectWriteForAvalonia;

if (!OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("AotSmoke targets Windows only.");

var options = SmokeOptions.Parse(args);

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    Console.Error.WriteLine("Unhandled exception: " + e.ExceptionObject);
};

return AppBuilder.Configure(() => new SmokeApp(options))
    .UseWin32()
    .UseDirect2D1()
    .UseDirectWrite()
    .WithInterFont()
    .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);

internal static class OffscreenSmoke
{
    public static void Run(SmokeOptions options)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(256, 128), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext(false))
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 256, 128));
            context.DrawLine(new Pen(Brushes.Black, 1), new Point(0, 0), new Point(255, 127));
            context.DrawRectangle(
                new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromRgb(40, 120, 220), 0),
                        new GradientStop(Color.FromRgb(95, 210, 160), 1)
                    }
                },
                null,
                new RoundedRect(new Rect(12, 52, 232, 54), 12));

            context.DrawRectangle(
                new ConicGradientBrush
                {
                    Center = RelativePoint.Center,
                    GradientStops =
                    {
                        new GradientStop(Color.FromRgb(230, 70, 70), 0),
                        new GradientStop(Color.FromRgb(255, 220, 80), 0.33),
                        new GradientStop(Color.FromRgb(80, 170, 240), 0.66),
                        new GradientStop(Color.FromRgb(230, 70, 70), 1)
                    }
                },
                null,
                new RoundedRect(new Rect(156, 76, 40, 34), 8));

            context.DrawRectangle(
                new SolidColorBrush(Color.FromRgb(255, 248, 210)),
                null,
                new RoundedRect(new Rect(84, 78, 54, 28), 7),
                new BoxShadows(new BoxShadow
                {
                    OffsetX = 5,
                    OffsetY = 4,
                    Blur = 8,
                    Spread = 1,
                    Color = Color.FromArgb(120, 20, 32, 48)
                }));

            var path = Geometry.Parse("M 14,122 C 54,82 104,134 146,96");
            if (!path.TryGetSegment(12, 100, true, out var segment))
                throw new InvalidOperationException("Geometry.TryGetSegment returned false in offscreen smoke.");
            context.DrawGeometry(null, new Pen(Brushes.Black, 2), segment);

            var text = new FormattedText(
                "AOT Direct2D",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                18,
                Brushes.DarkBlue);

            context.DrawText(text, new Point(8, 8));
            context.DrawRectangle(SmokeAssets.ImageBrush, null, new Rect(196, 16, 48, 32));
        }

        Directory.CreateDirectory(options.OutputDirectory);
        var outputPath = Path.Combine(options.OutputDirectory, "offscreen.png");
        bitmap.Save(outputPath);
        ScreenshotVerifier.VerifyPng(outputPath, "offscreen");
        ScreenshotVerifier.VerifyImageBrushMarker(outputPath, "offscreen image brush", 196, 16, 48, 32);
        VerifyJpegQuality(bitmap, options.OutputDirectory);
        Console.WriteLine($"Offscreen smoke passed. screenshot={outputPath}");
    }

    private static void VerifyJpegQuality(RenderTargetBitmap bitmap, string outputDirectory)
    {
        var lowQualityPath = Path.Combine(outputDirectory, "offscreen-q15.jpg");
        var highQualityPath = Path.Combine(outputDirectory, "offscreen-q95.jpg");

        bitmap.Save(lowQualityPath, quality: 15);
        bitmap.Save(highQualityPath, quality: 95);

        var lowQualityLength = new FileInfo(lowQualityPath).Length;
        var highQualityLength = new FileInfo(highQualityPath).Length;

        if (highQualityLength <= lowQualityLength)
        {
            throw new InvalidOperationException(
                $"JPEG quality smoke expected q95 to be larger than q15, but q15={lowQualityLength} and q95={highQualityLength}.");
        }
    }
}

internal sealed class SmokeApp : Application
{
    private readonly SmokeOptions _options;

    public SmokeApp(SmokeOptions options)
    {
        _options = options;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new FluentTheme());

        FontFallbackSmoke.Run();
        OffscreenSmoke.Run(_options);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (_options.UiRepro)
            {
                Window repro = _options.AfsRepro
                    ? new AssFontSubsetReproWindow(x: 0, y: 0)
                    : new UiReproWindow(x: 80, y: 80);
                desktop.MainWindow = repro;
                repro.Show();

                if (_options.AutoExit)
                {
                    StartUiReproAutoExitTimer(desktop, repro);
                }

                base.OnFrameworkInitializationCompleted();
                return;
            }

            var normal = new SmokeWindow("Direct2D1 normal", transparent: false, x: 80, y: 80);
            var transparent = new SmokeWindow("Direct2D1 transparent", transparent: true, x: 500, y: 120);

            desktop.MainWindow = normal;
            normal.Show();
            transparent.Show();

            if (_options.AutoExit)
            {
                StartAutoExitTimer(desktop, normal, transparent);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void StartAutoExitTimer(
        IClassicDesktopStyleApplicationLifetime desktop,
        SmokeWindow normal,
        SmokeWindow transparent)
    {
        var started = DateTimeOffset.UtcNow;
        var captured = false;
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };

        timer.Tick += (_, _) =>
        {
            normal.Surface.Advance();
            transparent.Surface.Advance();

            var elapsed = DateTimeOffset.UtcNow - started;
            if (!captured &&
                normal.Surface.RenderCount >= _options.MinFrames &&
                transparent.Surface.RenderCount >= _options.MinFrames &&
                elapsed >= TimeSpan.FromMilliseconds(500))
            {
                captured = true;
                try
                {
                    Directory.CreateDirectory(_options.OutputDirectory);

                    var normalPath = Path.Combine(_options.OutputDirectory, "normal-window.png");
                    var transparentPath = Path.Combine(_options.OutputDirectory, "transparent-window.png");

                    WindowScreenshot.Capture(normal, normalPath);
                    WindowScreenshot.Capture(transparent, transparentPath);
                    ScreenshotVerifier.VerifyPng(normalPath, "normal window");
                    ScreenshotVerifier.VerifyPng(transparentPath, "transparent window");
                    ScreenshotVerifier.VerifyImageBrushMarker(normalPath, "normal window image brush");
                    ScreenshotVerifier.VerifyImageBrushMarker(transparentPath, "transparent window image brush");

                    timer.Stop();
                    Console.WriteLine(
                        $"Window smoke passed. normalFrames={normal.Surface.RenderCount}, " +
                        $"transparentFrames={transparent.Surface.RenderCount}, " +
                        $"actualTransparency={transparent.ActualTransparencyLevel}, " +
                        $"normalScreenshot={normalPath}, transparentScreenshot={transparentPath}");
                    desktop.Shutdown(0);
                    return;
                }
                catch (Exception ex)
                {
                    timer.Stop();
                    Console.Error.WriteLine("Window smoke screenshot failed: " + ex);
                    desktop.Shutdown(3);
                    return;
                }
            }

            if (elapsed >= _options.Timeout)
            {
                timer.Stop();
                Console.Error.WriteLine(
                    $"Window smoke timed out. normalFrames={normal.Surface.RenderCount}, " +
                    $"transparentFrames={transparent.Surface.RenderCount}, " +
                    $"actualTransparency={transparent.ActualTransparencyLevel}");
                desktop.Shutdown(2);
            }
        };

        timer.Start();
    }

    private void StartUiReproAutoExitTimer(
        IClassicDesktopStyleApplicationLifetime desktop,
        Window window)
    {
        var started = DateTimeOffset.UtcNow;
        var ticks = 0;
        var captured = false;
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };

        timer.Tick += (_, _) =>
        {
            ticks++;
            if (window.Content is Control control)
            {
                control.InvalidateVisual();
            }

            var elapsed = DateTimeOffset.UtcNow - started;
            if (!captured && ticks >= _options.MinFrames && elapsed >= TimeSpan.FromMilliseconds(500))
            {
                captured = true;
                try
                {
                    Directory.CreateDirectory(_options.OutputDirectory);
                    var reproPath = Path.Combine(_options.OutputDirectory, window is AssFontSubsetReproWindow ? "afs-repro-window.png" : "ui-repro-window.png");

                    WindowScreenshot.Capture(window, reproPath);
                    ScreenshotVerifier.VerifyPng(reproPath, "UI repro window");
                    ScreenshotVerifier.VerifyBottomMarker(reproPath, IReproMarker.MarkerColor);

                    timer.Stop();
                    Console.WriteLine($"UI repro smoke passed. ticks={ticks}, screenshot={reproPath}");
                    desktop.Shutdown(0);
                    return;
                }
                catch (Exception ex)
                {
                    timer.Stop();
                    Console.Error.WriteLine("UI repro screenshot failed: " + ex);
                    desktop.Shutdown(3);
                    return;
                }
            }

            if (elapsed >= _options.Timeout)
            {
                timer.Stop();
                Console.Error.WriteLine($"UI repro smoke timed out. ticks={ticks}");
                desktop.Shutdown(2);
            }
        };

        timer.Start();
    }
}

internal static class FontFallbackSmoke
{
    public static void Run()
    {
        var culture = TryGetCulture("zh-CN");
        if (!FontManager.Current.TryMatchCharacter(
                '后',
                FontStyle.Normal,
                FontWeight.Normal,
                FontStretch.Normal,
                new FontFamily("Inter"),
                culture,
                out var typeface))
        {
            throw new InvalidOperationException("DirectWrite font fallback failed for Chinese character U+540E.");
        }

        Console.WriteLine(
            $"Font fallback smoke passed. culture={(string.IsNullOrWhiteSpace(culture.Name) ? "Invariant" : culture.Name)}, " +
            $"matchedFamily={typeface.FontFamily.Name}");
    }

    private static CultureInfo TryGetCulture(string name)
    {
        try
        {
            return CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }
}

internal sealed class SmokeWindow : Window
{
    public SmokeWindow(string title, bool transparent, int x, int y)
    {
        Title = title;
        Width = 360;
        Height = 240;
        Position = new PixelPoint(x, y);
        ShowActivated = true;
        Topmost = true;
        CanResize = false;

        if (transparent)
        {
            WindowDecorations = WindowDecorations.None;
            Background = Brushes.Transparent;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        }
        else
        {
            Background = Brushes.White;
        }

        Surface = new SmokeSurface(transparent);
        Content = Surface;
    }

    public SmokeSurface Surface { get; }
}

internal sealed class UiReproWindow : Window
{
    public UiReproWindow(int x, int y)
    {
        Title = "Direct2D1 UI repro";
        Width = 760;
        Height = 540;
        Position = new PixelPoint(x, y);
        ShowActivated = true;
        Topmost = true;
        CanResize = false;
        Background = Brushes.White;
        Content = CreateContent();
    }

    private static Control CreateContent()
    {
        var root = new DockPanel
        {
            LastChildFill = true,
            Background = Brushes.White
        };

        var marker = new Border
        {
            Height = 14,
            Background = new SolidColorBrush(IReproMarker.MarkerColor),
            Opacity = 0.98
        };
        DockPanel.SetDock(marker, Dock.Bottom);
        root.Children.Add(marker);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(18, 10, 18, 12),
            VerticalAlignment = VerticalAlignment.Center
        };
        footer.Children.Add(new RadioButton { Content = "保留源样式", IsChecked = true });
        footer.Children.Add(new RadioButton { Content = "仅子集化" });
        footer.Children.Add(new CheckBox { Content = "自动加载字体", IsChecked = true });
        footer.Children.Add(new ProgressBar { Width = 120, Height = 8, Minimum = 0, Maximum = 100, Value = 72 });
        footer.Children.Add(new Button { Content = "生成子集", MinWidth = 96 });
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var header = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(18, 14, 18, 8)
        };
        header.Children.Add(new TextBlock
        {
            Text = "Direct2D1 字体和控件复现",
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            FontFamily = new FontFamily("Inter")
        });
        header.Children.Add(new TextBlock
        {
            Text = "Inter 首选字体包含中文回退: 后备字体、路径选择、滚动区域和底部操作栏必须完整显示。",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(70, 70, 70)),
            TextWrapping = TextWrapping.Wrap
        });
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var body = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(18, 0, 18, 0),
            Opacity = 0.995
        };

        body.Children.Add(new TextBox
        {
            Text = "字幕文件: source.ass\n首选字体: Inter\n中文样例: 后备字体不应和 Skia 明显不一致",
            AcceptsReturn = true,
            Height = 86,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Inter")
        });

        var options = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12
        };
        options.Children.Add(new CheckBox { Content = "压缩字体名", IsChecked = true });
        options.Children.Add(new CheckBox { Content = "保留 OpenType 特性", IsChecked = true });
        options.Children.Add(new CheckBox { Content = "导出报告" });
        body.Children.Add(options);

        var log = new StackPanel { Spacing = 4 };
        for (var i = 1; i <= 14; i++)
        {
            log.Children.Add(new TextBlock
            {
                Text = $"{i:00}  字体回退检查: Inter -> 中文字体，样例字符 后 字 幕 渲 染。",
                FontFamily = new FontFamily("Inter"),
                FontSize = 14
            });
        }

        body.Children.Add(new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(190, 198, 210)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
            Child = log
        });

        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = body
        });

        return root;
    }
}

internal static class IReproMarker
{
    public static readonly Color MarkerColor = Color.FromRgb(255, 0, 255);
}

internal sealed class AssFontSubsetReproWindow : Window
{
    public AssFontSubsetReproWindow(int x, int y)
    {
        Title = "AssFontSubset Direct2D repro";
        Width = 2880;
        Height = 1747;
        Position = new PixelPoint(x, y);
        ShowActivated = true;
        Topmost = true;
        CanResize = false;
        Background = Brushes.Black;
        Content = CreateContent();
    }

    private static Control CreateContent()
    {
        var root = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 12,
            Background = Brushes.Black
        };

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 12
        };
        header.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "AssFontSubset", FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White },
                new TextBlock { Text = "就绪", Opacity = 0.72, Foreground = Brushes.White }
            }
        });

        var headerButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        headerButtons.Children.Add(new Button { Content = "清空" });
        headerButtons.Children.Add(new Button { Content = "开始", MinWidth = 112, IsEnabled = false, FontWeight = FontWeight.SemiBold });
        Grid.SetColumn(headerButtons, 1);
        header.Children.Add(headerButtons);
        root.Children.Add(header);

        var body = new Grid
        {
            RowDefinitions = new RowDefinitions("2*,Auto,Auto,Auto,Auto,Auto,3*"),
            ColumnDefinitions = new ColumnDefinitions("150,*,Auto"),
            RowSpacing = 10,
            ColumnSpacing = 10
        };
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        AddLabel(body, 0, "字幕文件");
        var dropBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(48, 48, 48)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(38, 38, 38)),
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("*,Auto"),
                Children =
                {
                    new TextBlock
                    {
                        Text = "将 .ass 文件拖到这里，或手动选择文件。",
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = Brushes.White
                    },
                    CreateAssFooter()
                }
            }
        };
        Grid.SetRow(dropBorder, 0);
        Grid.SetColumn(dropBorder, 1);
        Grid.SetColumnSpan(dropBorder, 2);
        body.Children.Add(dropBorder);

        AddPathRow(body, 1, "字体目录", "默认：第一个 ASS 文件同目录下的 fonts 文件夹");
        AddPathRow(body, 2, "输出目录", "默认：第一个 ASS 文件同目录下的 output 文件夹");
        AddPathRow(body, 3, "FontTools 目录", "可选：pyftsubset 和 ttx 所在目录");

        AddLabel(body, 4, "后端");
        var backends = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, VerticalAlignment = VerticalAlignment.Center };
        backends.Children.Add(new RadioButton { Content = "py", IsChecked = true, Foreground = Brushes.White });
        backends.Children.Add(new RadioButton { Content = "居", Foreground = Brushes.White });
        Grid.SetRow(backends, 4);
        Grid.SetColumn(backends, 1);
        Grid.SetColumnSpan(backends, 2);
        body.Children.Add(backends);

        AddLabel(body, 5, "选项");
        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, VerticalAlignment = VerticalAlignment.Center };
        options.Children.Add(new CheckBox { Content = "居", IsChecked = true, Foreground = Brushes.White });
        options.Children.Add(new CheckBox { Content = "调试", Foreground = Brushes.White });
        Grid.SetRow(options, 5);
        Grid.SetColumn(options, 1);
        Grid.SetColumnSpan(options, 2);
        body.Children.Add(options);

        AddLabel(body, 6, "日志", top: true);
        var logBox = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(48, 48, 48)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = new TextBox
            {
                Text = "",
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
                FontSize = 12,
                BorderThickness = new Thickness(0)
            }
        };
        Grid.SetRow(logBox, 6);
        Grid.SetColumn(logBox, 1);
        Grid.SetColumnSpan(logBox, 2);
        body.Children.Add(logBox);

        var progress = new ProgressBar
        {
            Height = 8,
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Background = new SolidColorBrush(IReproMarker.MarkerColor)
        };
        Grid.SetRow(progress, 2);
        root.Children.Add(progress);

        return root;
    }

    private static Grid CreateAssFooter()
    {
        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(8, 0, 8, 8),
            ColumnSpacing = 8
        };
        Grid.SetRow(footer, 1);
        footer.Children.Add(new TextBlock { Text = "未选择 ASS 文件", Opacity = 0.72, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White });
        var button = new Button { Content = "选择文件" };
        Grid.SetColumn(button, 1);
        footer.Children.Add(button);
        return footer;
    }

    private static void AddLabel(Grid body, int row, string text, bool top = false)
    {
        var label = new TextBlock
        {
            Text = text,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Center,
            Margin = top ? new Thickness(0, 7, 0, 0) : default,
            Foreground = Brushes.White
        };
        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);
        body.Children.Add(label);
    }

    private static void AddPathRow(Grid body, int row, string label, string placeholder)
    {
        AddLabel(body, row, label);

        var textBox = new TextBox
        {
            PlaceholderText = placeholder,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(textBox, row);
        Grid.SetColumn(textBox, 1);
        body.Children.Add(textBox);

        var button = new Button { Content = "浏览" };
        Grid.SetRow(button, row);
        Grid.SetColumn(button, 2);
        body.Children.Add(button);
    }
}

internal sealed class SmokeSurface : Control
{
    private static readonly Typeface s_typeface = new("Segoe UI");
    private readonly bool _transparent;
    private int _tick;

    public SmokeSurface(bool transparent)
    {
        _transparent = transparent;
        ClipToBounds = true;
    }

    public int RenderCount { get; private set; }

    public void Advance()
    {
        _tick++;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        RenderCount++;

        var rect = new Rect(Bounds.Size);
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        var phase = _tick * 0.11;
        var background = _transparent
            ? new SolidColorBrush(Color.FromArgb(96, 30, 120, 210))
            : new SolidColorBrush(Color.FromRgb(245, 248, 252));

        context.DrawRectangle(background, null, rect);
        context.DrawRectangle(
            new SolidColorBrush(Color.FromArgb(_transparent ? (byte)160 : (byte)255, 36, 43, 54)),
            null,
            new RoundedRect(new Rect(24, 24, rect.Width - 48, rect.Height - 48), 16));

        for (var i = 0; i < 8; i++)
        {
            var x = 44 + (i * 36);
            var y = 78 + Math.Sin(phase + i * 0.65) * 30;
            var color = i % 2 == 0
                ? Color.FromArgb(210, 103, 232, 184)
                : Color.FromArgb(210, 255, 196, 87);

            context.DrawEllipse(new SolidColorBrush(color), null, new Rect(x, y, 22, 22));
        }

        var pen = new Pen(new SolidColorBrush(Color.FromArgb(230, 235, 242, 255)), 2);
        context.DrawLine(pen, new Point(34, 166), new Point(rect.Width - 34, 166));
        context.DrawLine(
            new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 128, 128)), 3),
            new Point(42 + Math.Cos(phase) * 16, 190),
            new Point(rect.Width - 42, 56 + Math.Sin(phase * 0.8) * 18));

        context.DrawRectangle(SmokeAssets.ImageBrush, null, new RoundedRect(new Rect(rect.Width - 88, rect.Height - 82, 48, 48), 10));

        var title = _transparent ? "Transparent D3D11" : "Normal D3D11";
        var text = new FormattedText(
            $"{title}  frame {_tick}",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            s_typeface,
            20,
            Brushes.White);

        context.DrawText(text, new Point(42, 36));
    }

}

internal static class SmokeAssets
{
    public static readonly ImageBrush ImageBrush = CreateImageBrush();

    private static ImageBrush CreateImageBrush()
    {
        var pixels = new byte[16 * 16 * 4];
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                var offset = ((y * 16) + x) * 4;
                var even = ((x / 4) + (y / 4)) % 2 == 0;
                pixels[offset] = even ? (byte)70 : (byte)235;
                pixels[offset + 1] = even ? (byte)210 : (byte)120;
                pixels[offset + 2] = even ? (byte)245 : (byte)90;
                pixels[offset + 3] = 255;
            }
        }

        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            return new ImageBrush(new Bitmap(
                PixelFormats.Bgra8888,
                AlphaFormat.Opaque,
                handle.AddrOfPinnedObject(),
                new PixelSize(16, 16),
                new Vector(96, 96),
                16 * 4))
            {
                Stretch = Stretch.Fill,
                TileMode = TileMode.FlipXY,
                SourceRect = new RelativeRect(0, 0, 1, 1, RelativeUnit.Relative)
            };
        }
        finally
        {
            handle.Free();
        }
    }
}

internal sealed record SmokeOptions(bool AutoExit, int MinFrames, TimeSpan Timeout, string OutputDirectory, bool UiRepro, bool AfsRepro)
{
    public static SmokeOptions Parse(string[] args)
    {
        var autoExit = args.Contains("--auto-exit", StringComparer.OrdinalIgnoreCase);
        var uiRepro = args.Contains("--ui-repro", StringComparer.OrdinalIgnoreCase);
        var afsRepro = args.Contains("--afs-repro", StringComparer.OrdinalIgnoreCase);
        var minFrames = GetInt(args, "--frames") ?? 12;
        var timeoutMs = GetInt(args, "--timeout-ms") ?? 8000;
        var outputDirectory = GetString(args, "--out") ??
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", "AotSmoke"));

        return new SmokeOptions(
            autoExit,
            minFrames,
            TimeSpan.FromMilliseconds(timeoutMs),
            Path.GetFullPath(outputDirectory),
            uiRepro || afsRepro,
            afsRepro);
    }

    private static int? GetInt(string[] args, string name)
    {
        var raw = GetString(args, name);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static string? GetString(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}

internal static class WindowScreenshot
{
    public static void Capture(Window window, string path)
    {
        var handle = window.TryGetPlatformHandle();
        if (handle is null ||
            handle.Handle == IntPtr.Zero ||
            !string.Equals(handle.HandleDescriptor, "HWND", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Window does not expose an HWND platform handle.");
        }

        CaptureHwnd(handle.Handle, path);
    }

    private static void CaptureHwnd(IntPtr hwnd, string path)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            throw new InvalidOperationException("GetWindowRect failed: " + Marshal.GetLastPInvokeError());
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException($"Window has invalid screenshot bounds: {width}x{height}.");
        }

        var sourceDc = IntPtr.Zero;
        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr oldObject = IntPtr.Zero;
        try
        {
            var screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                throw new InvalidOperationException("GetDC failed: " + Marshal.GetLastPInvokeError());
            }

            sourceDc = screenDc;
            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateCompatibleDC failed: " + Marshal.GetLastPInvokeError());
            }

            bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
            if (bitmap == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateCompatibleBitmap failed: " + Marshal.GetLastPInvokeError());
            }

            oldObject = NativeMethods.SelectObject(memoryDc, bitmap);
            if (oldObject == IntPtr.Zero)
            {
                throw new InvalidOperationException("SelectObject failed: " + Marshal.GetLastPInvokeError());
            }

            NativeMethods.DwmFlush();
            if (!NativeMethods.PrintWindow(hwnd, memoryDc, NativeMethods.PW_RENDERFULLCONTENT) &&
                !NativeMethods.BitBlt(memoryDc, 0, 0, width, height, screenDc, rect.Left, rect.Top, NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT))
            {
                throw new InvalidOperationException("PrintWindow and BitBlt failed. Last error: " + Marshal.GetLastPInvokeError());
            }

            var header = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BI_RGB
            };

            var pixels = new byte[width * height * 4];
            var scanLines = NativeMethods.GetDIBits(memoryDc, bitmap, 0, (uint)height, pixels, ref header, NativeMethods.DIB_RGB_COLORS);
            if (scanLines == 0)
            {
                throw new InvalidOperationException("GetDIBits failed: " + Marshal.GetLastPInvokeError());
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

            var pixelsHandle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                using var avaloniaBitmap = new Bitmap(
                    PixelFormats.Bgra8888,
                    AlphaFormat.Opaque,
                    pixelsHandle.AddrOfPinnedObject(),
                    new PixelSize(width, height),
                    new Vector(96, 96),
                    width * 4);
                avaloniaBitmap.Save(path);
            }
            finally
            {
                pixelsHandle.Free();
            }
        }
        finally
        {
            if (oldObject != IntPtr.Zero && memoryDc != IntPtr.Zero)
            {
                NativeMethods.SelectObject(memoryDc, oldObject);
            }

            if (bitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmap);
            }

            if (memoryDc != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(memoryDc);
            }

            if (sourceDc != IntPtr.Zero)
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, sourceDc);
            }
        }
    }
}

internal static class ScreenshotVerifier
{
    private static readonly Color s_imageBrushYellow = Color.FromRgb(245, 210, 70);
    private static readonly Color s_imageBrushBlue = Color.FromRgb(90, 120, 235);

    public static void VerifyPng(string path, string name)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0)
        {
            throw new InvalidOperationException($"The {name} screenshot was not written: {path}");
        }

        using var bitmap = new Bitmap(path);
        var size = bitmap.PixelSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            throw new InvalidOperationException($"The {name} screenshot has invalid dimensions: {size.Width}x{size.Height}.");
        }

        using var buffer = new VerifierFramebuffer(size);
        bitmap.CopyPixels(buffer);

        if (!buffer.HasColorVariation())
        {
            throw new InvalidOperationException($"The {name} screenshot appears blank: {path}");
        }
    }

    public static void VerifyImageBrushMarker(string path, string name) =>
        VerifyImageBrushMarker(path, name, 0, 0, int.MaxValue, int.MaxValue);

    public static void VerifyBottomMarker(string path, Color markerColor)
    {
        using var bitmap = new Bitmap(path);
        using var buffer = new VerifierFramebuffer(bitmap.PixelSize);
        bitmap.CopyPixels(buffer);

        var searchHeight = Math.Min(96, buffer.Size.Height);
        if (!buffer.ContainsColor(
                markerColor,
                0,
                buffer.Size.Height - searchHeight,
                buffer.Size.Width,
                searchHeight,
                tolerance: 24,
                minPixels: 24))
        {
            throw new InvalidOperationException($"The UI repro bottom marker was not rendered: {path}");
        }
    }

    public static void VerifyImageBrushMarker(string path, string name, int x, int y, int width, int height)
    {
        using var bitmap = new Bitmap(path);
        using var buffer = new VerifierFramebuffer(bitmap.PixelSize);
        bitmap.CopyPixels(buffer);

        if (!buffer.ContainsColor(s_imageBrushYellow, x, y, width, height, tolerance: 48, minPixels: 8) ||
            !buffer.ContainsColor(s_imageBrushBlue, x, y, width, height, tolerance: 48, minPixels: 8))
        {
            throw new InvalidOperationException($"The {name} marker was not rendered: {path}");
        }
    }
}

internal sealed class VerifierFramebuffer : ILockedFramebuffer
{
    private readonly byte[] _pixels;
    private readonly GCHandle _handle;
    private bool _disposed;

    public VerifierFramebuffer(PixelSize size)
    {
        Size = size;
        RowBytes = size.Width * 4;
        Dpi = new Vector(96, 96);
        Format = PixelFormats.Bgra8888;
        AlphaFormat = AlphaFormat.Opaque;
        _pixels = new byte[RowBytes * size.Height];
        _handle = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
    }

    public IntPtr Address => _handle.AddrOfPinnedObject();

    public PixelSize Size { get; }

    public int RowBytes { get; }

    public Vector Dpi { get; }

    public PixelFormat Format { get; }

    public AlphaFormat AlphaFormat { get; }

    public nint Key => 0;

    public bool HasColorVariation()
    {
        if (_pixels.Length < 4)
        {
            return false;
        }

        var min = int.MaxValue;
        var max = int.MinValue;
        for (var i = 0; i < _pixels.Length; i += 4)
        {
            var value = _pixels[i] + _pixels[i + 1] + _pixels[i + 2];
            if (value < min)
            {
                min = value;
            }

            if (value > max)
            {
                max = value;
            }

            if (max - min > 16)
            {
                return true;
            }
        }

        return false;
    }

    public bool ContainsColor(Color color, int x, int y, int width, int height, int tolerance, int minPixels)
    {
        var left = Math.Clamp(x, 0, Size.Width);
        var top = Math.Clamp(y, 0, Size.Height);
        var right = Math.Clamp(x + width, left, Size.Width);
        var bottom = Math.Clamp(y + height, top, Size.Height);
        var matches = 0;

        for (var row = top; row < bottom; row++)
        {
            for (var column = left; column < right; column++)
            {
                var offset = (row * RowBytes) + (column * 4);
                if (Math.Abs(_pixels[offset] - color.B) <= tolerance &&
                    Math.Abs(_pixels[offset + 1] - color.G) <= tolerance &&
                    Math.Abs(_pixels[offset + 2] - color.R) <= tolerance)
                {
                    matches++;
                    if (matches >= minPixels)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Free();
    }
}

internal static partial class NativeMethods
{
    public const int SRCCOPY = 0x00CC0020;
    public const int CAPTUREBLT = 0x40000000;
    public const uint PW_RENDERFULLCONTENT = 0x00000002;
    public const uint BI_RGB = 0;
    public const uint DIB_RGB_COLORS = 0;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr GetDC(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmFlush();

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial IntPtr CreateCompatibleDC(IntPtr hdc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, int rop);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial int GetDIBits(
        IntPtr hdc,
        IntPtr hbm,
        uint start,
        uint cLines,
        byte[] lpvBits,
        ref BITMAPINFOHEADER lpbmi,
        uint usage);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(IntPtr ho);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(IntPtr hdc);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }
}
