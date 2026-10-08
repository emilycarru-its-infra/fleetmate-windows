using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using FleetMate.Core.Knowledge;
using FleetMate.GUI.Views.Shared;

namespace FleetMate.GUI.Views.Knowledge;

/// <summary>
/// A Handbook page read inside FleetMate: the same text staff read on the
/// site, from FleetMate's own copy of main (macOS parity).
/// </summary>
public sealed class HandbookReaderWindow : Window
{
    private readonly TextBlock _title = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _breadcrumb = new() { FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBlock _modified = new() { FontSize = 11, Margin = new Thickness(0, 0, 0, 8) };
    private readonly Button _openOnSite = new() { Content = "Open on Site", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
    private readonly MarkdownViewer _viewer = new();
    private Uri? _siteUrl;

    public HandbookReaderWindow()
    {
        Title = "Handbook";
        Width = 860;
        Height = 760;
        MinWidth = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ModernWpf.Controls.Primitives.WindowHelper.SetUseModernWindowStyle(this, true);

        _breadcrumb.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        _modified.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        _openOnSite.Click += (_, _) =>
        {
            if (_siteUrl != null) Process.Start(new ProcessStartInfo(_siteUrl.ToString()) { UseShellExecute = true });
        };
        var close = new Button { Content = "Close", Padding = new Thickness(12, 4, 12, 4), IsCancel = true };
        close.Click += (_, _) => Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        buttons.Children.Add(_openOnSite);
        buttons.Children.Add(close);
        var heading = new StackPanel();
        heading.Children.Add(_title);
        heading.Children.Add(_breadcrumb);
        var header = new DockPanel { Margin = new Thickness(18, 14, 18, 10) };
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(heading);

        var body = new DockPanel { Margin = new Thickness(18, 6, 18, 12) };
        DockPanel.SetDock(_modified, Dock.Top);
        body.Children.Add(_modified);
        body.Children.Add(_viewer);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(new Separator { Margin = new Thickness(0) });
        DockPanel.SetDock(root.Children[^1], Dock.Top);
        root.Children.Add(body);
        root.SetResourceReference(Panel.BackgroundProperty, "AppBackgroundBrush");
        root.SetResourceReference(TextElement.ForegroundProperty, "SystemControlForegroundBaseHighBrush");
        Content = root;
    }

    /// <summary>Show <paramref name="page"/>, bringing the window forward.</summary>
    public void Show(HandbookPage page, Uri? siteUrl)
    {
        _siteUrl = siteUrl;
        Title = $"{page.Title} · Handbook";
        _title.Text = page.Title;
        _breadcrumb.Text = string.IsNullOrEmpty(page.Breadcrumb) ? "Handbook" : $"Handbook › {page.Breadcrumb}";
        _modified.Text = page.LastModified is { } date
            ? $"Updated {date}{(page.LastModifiedBy is { } by ? $" by {by}" : "")}"
            : "";
        _modified.Visibility = _modified.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _openOnSite.Visibility = siteUrl != null ? Visibility.Visible : Visibility.Collapsed;
        _viewer.MarkdownText = page.Body;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
}
