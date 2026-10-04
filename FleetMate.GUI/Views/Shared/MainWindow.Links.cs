using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ModernWpf.Controls;

namespace FleetMate.GUI.Views.Shared;

/// <summary>A dismissible banner for a fleetmate: link the app can't open.</summary>
public partial class MainWindow
{
    private Border? _linkBanner;

    /// <summary>Show <paramref name="message"/> across the top of the window until dismissed.</summary>
    public void ShowLinkBanner(string message)
    {
        // The window's root grid; the banner floats over every row of it.
        if (Content is not Grid root) return;
        if (_linkBanner != null) root.Children.Remove(_linkBanner);

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 12, 0)
        };
        var close = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 11 },
            Padding = new Thickness(6),
            ToolTip = "Dismiss",
            VerticalAlignment = VerticalAlignment.Center
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(close, "LinkBannerDismissButton");

        var row = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        row.Children.Add(close);
        row.Children.Add(new FontIcon { Glyph = "", FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0x89, 0x0C)) });
        row.Children.Add(text);

        _linkBanner = new Border
        {
            Child = row,
            Background = (Brush)FindResource("CardBackgroundBrush"),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0x89, 0x0C)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 8, 8),
            Margin = new Thickness(16, 64, 16, 0),
            MaxWidth = 900,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_linkBanner, "LinkBanner");
        Grid.SetRowSpan(_linkBanner, Math.Max(1, root.RowDefinitions.Count));
        Grid.SetColumnSpan(_linkBanner, Math.Max(1, root.ColumnDefinitions.Count));
        Panel.SetZIndex(_linkBanner, 1000);

        var banner = _linkBanner;
        close.Click += (_, _) =>
        {
            root.Children.Remove(banner);
            if (_linkBanner == banner) _linkBanner = null;
        };
        root.Children.Add(_linkBanner);
    }
}
