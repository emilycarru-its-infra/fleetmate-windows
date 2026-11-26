using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FleetMate.WinUI.Shell;

/// <summary>
/// The per-panel UI QA run: <c>--capture &lt;dir&gt;</c> walks every tab and
/// Settings at a narrow and a wide window, renders the XAML root at 100% and
/// 150% to PNG, checks each panel for clipped text and unnamed controls,
/// writes <c>qa-&lt;theme&gt;.md</c>, and exits. It captures the app's theme,
/// so pair it with <c>--theme light</c> and <c>--theme dark</c>, one run each
/// (scripts/winui-qa.ps1 does both editions in both themes): switching the
/// theme on an element instead leaves recycled list rows in the old theme.
///
/// It renders with <see cref="RenderTargetBitmap"/> from inside the app,
/// so it needs no visible desktop: it works on a locked or headless session
/// and in CI, where a screen capture would come back blank. What it cannot
/// show is drawn outside XAML: the system caption buttons and Mica, which
/// are drawn as Mica's base colour for the theme. High contrast is a Windows setting
/// an app cannot turn on for itself, so the run uses whichever contrast theme
/// Windows has, and says so in <c>qa.md</c>.
///
/// A capture is always a demo (<see cref="ShellServices.DemoMode"/>): the
/// services hold registered fixtures and nothing live, and the run refuses to
/// write a single PNG if a live service or a sign-in is present. A page added
/// in a later phase is covered as soon as it is registered in
/// <see cref="ShellPages"/> with its fixtures in
/// <see cref="ShellServices.RegisterDemoFixtures"/>.
/// </summary>
internal static class CaptureHarness
{
    /// <summary>Window sizes in DIPs: the narrowest the toolbar must survive, and a typical desktop.</summary>
    internal static readonly (string Name, int Width, int Height)[] Sizes =
    {
        ("narrow", 960, 720),
        ("wide", 1600, 1000),
    };

    internal static readonly double[] Scales = { 1.0, 1.5 };

    /// <summary>How long a panel gets to load and lay out before it is captured.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(900);

    /// <summary>The directory after <c>--capture</c>, or null when the flag is absent.</summary>
    public static string? DirectoryFromArgs(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (args[i].Equals("--capture", StringComparison.OrdinalIgnoreCase)) return Path.GetFullPath(args[i + 1]);
        return null;
    }

    /// <summary>The file name for one capture, e.g. <c>tickets-dark-wide@150.png</c>.</summary>
    internal static string FileName(string panel, ElementTheme theme, string size, double scale) =>
        $"{panel.ToLowerInvariant()}-{theme.ToString().ToLowerInvariant()}-{size}@{(int)Math.Round(scale * 100)}.png";

    public static async Task RunAsync(MainWindow window, string directory)
    {
        Directory.CreateDirectory(directory);

        // The repository is public: a capture shows fixtures or nothing.
        if (Core.Config.DemoMode.CaptureRefusal(App.Services.DemoMode, App.Services.HasLiveSession) is { } refusal)
        {
            Log.Error("[capture] {Reason}", refusal);
            await File.WriteAllTextAsync(Path.Combine(directory, "capture-refused.md"), $"# Capture refused{Environment.NewLine}{Environment.NewLine}{refusal}{Environment.NewLine}");
            Environment.ExitCode = 2;
            Application.Current.Exit();
            return;
        }

        var dark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
        var theme = dark ? ElementTheme.Dark : ElementTheme.Light;
        var report = new StringBuilder();
        var findings = 0;
        var contrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        report.AppendLine($"# {Core.Config.AppEdition.Current.Name} WinUI QA capture")
            .AppendLine()
            .AppendLine($"Captured {DateTime.Now:yyyy-MM-dd HH:mm}. High contrast in Windows: {(contrast ? "on" : "off")}.")
            .AppendLine();

        var panels = window.Tabs.Append(ShellPages.Settings).ToList();
        try
        {
            foreach (var (sizeName, width, height) in Sizes)
            {
                window.ResizeForCapture(width, height);
                // A sleeping or small display caps the window below the size
                // asked for; laying the root out at that size anyway keeps the
                // capture true to the window it stands for.
                window.CaptureRoot.Width = width;
                window.CaptureRoot.Height = height;
                window.CaptureRoot.HorizontalAlignment = HorizontalAlignment.Left;
                window.CaptureRoot.VerticalAlignment = VerticalAlignment.Top;
                {
                    foreach (var panel in panels)
                    {
                        window.ShowPanel(panel);
                        await Task.Delay(Settle);
                        report.AppendLine($"## {panel}, {theme}, {sizeName} ({width}x{height})").AppendLine();
                        foreach (var scale in Scales)
                        {
                            var file = FileName(panel, theme, sizeName, scale);
                            await RenderAsync(window.CaptureRoot, Path.Combine(directory, file), scale, dark);
                            report.AppendLine($"![{file}]({file})");
                        }
                        var issues = Inspect(window.CaptureRoot).ToList();
                        findings += issues.Count;
                        report.AppendLine();
                        if (issues.Count == 0) report.AppendLine("No clipped text or unnamed controls.");
                        foreach (var issue in issues) report.AppendLine($"- {issue}");
                        report.AppendLine();
                    }
                }
            }
            report.Insert(0, $"<!-- findings: {findings} -->{Environment.NewLine}");
            await File.WriteAllTextAsync(Path.Combine(directory, $"qa-{theme.ToString().ToLowerInvariant()}.md"), report.ToString());
            Log.Information("[capture] Wrote {Count} panels to {Directory} with {Findings} findings",
                panels.Count * Sizes.Length, directory, findings);
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[capture] The QA capture failed");
            Environment.ExitCode = 1;
        }
        Application.Current.Exit();
    }

    private static async Task RenderAsync(FrameworkElement root, string path, double scale, bool dark)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(root, (int)Math.Round(root.ActualWidth * scale), (int)Math.Round(root.ActualHeight * scale));
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        OverBackdrop(pixels, dark ? (byte)0x20 : (byte)0xF3);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96 * scale, 96 * scale, pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        await using var file = File.Create(path);
        await stream.AsStreamForRead().CopyToAsync(file);
    }

    /// <summary>
    /// Composite premultiplied BGRA over an opaque grey: where the window is
    /// transparent, Windows would show Mica, which a XAML render cannot.
    /// </summary>
    internal static void OverBackdrop(byte[] bgra, byte grey)
    {
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var a = bgra[i + 3];
            if (a == 255) continue;
            var rest = 255 - a;
            for (var c = 0; c < 3; c++) bgra[i + c] = (byte)(bgra[i + c] + grey * rest / 255);
            bgra[i + 3] = 255;
        }
    }

    /// <summary>
    /// What a reviewer would catch by eye: text cut off with an ellipsis, and
    /// buttons a screen reader has no name for. Only visible elements count.
    /// </summary>
    internal static IEnumerable<string> Inspect(DependencyObject root)
    {
        foreach (var element in Descendants(root).OfType<FrameworkElement>())
        {
            if (!IsShown(element)) continue;
            if (element is TextBlock { IsTextTrimmed: true } text)
                yield return $"Clipped text: \"{Shorten(text.Text)}\" ({Describe(text)})";
            if (element is ButtonBase button && button.Content is not string
                && string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))
                && string.IsNullOrWhiteSpace(AutomationProperties.GetLabeledBy(button)?.ToString()))
            {
                // Content that carries its own text names the button for UI Automation.
                if (!Descendants(button).OfType<TextBlock>().Any(t => !string.IsNullOrWhiteSpace(t.Text)))
                    yield return $"Button with no accessible name ({Describe(button)})";
            }
        }
    }

    private static bool IsShown(FrameworkElement element)
    {
        for (DependencyObject? e = element; e != null; e = VisualTreeHelper.GetParent(e))
        {
            if (e is UIElement { Visibility: Visibility.Collapsed }) return false;
            if (e is UIElement { Opacity: 0 }) return false;
        }
        return element.ActualWidth > 0 && element.ActualHeight > 0;
    }

    private static string Describe(FrameworkElement element) =>
        string.IsNullOrEmpty(element.Name) ? element.GetType().Name : $"{element.GetType().Name} {element.Name}";

    private static string Shorten(string text) => text.Length > 60 ? text[..57] + "..." : text;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var grandchild in Descendants(child)) yield return grandchild;
        }
    }
}
