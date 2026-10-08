using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// Fits the toolbar to the window, as the macOS toolbar does on a laptop
/// screen. The tabs stay centered and search stays full width against the
/// right edge. When the right-hand group would run into the tabs, inactive
/// tabs drop to their icon (name in the tooltip; the selected tab keeps its
/// name). If even that does not fit, the tab bar slides left off center just
/// far enough to clear the right-hand group, so nothing overlaps.
/// </summary>
public partial class MainWindow
{
    /// <summary>Space kept between the tab bar and the right-hand group.</summary>
    private const double ToolbarGap = 16;

    private bool _compactTabs;
    private readonly Dictionary<RadioButton, (double Full, double Label)> _tabWidths = new();

    private void InitToolbarFit()
    {
        foreach (var tab in TabBar.Children.OfType<RadioButton>())
        {
            if (TabLabel(tab) is { } label) tab.ToolTip ??= label.Text;
            tab.Checked += (_, _) => ApplyTabLabels();
        }
        SizeChanged += (_, _) => FitToolbar();
        // The elevation label and Graphs button come and go; refit when they do.
        ToolbarRight.SizeChanged += (_, e) => { if (e.WidthChanged) FitToolbar(); };
        FitToolbar();
    }

    private static TextBlock? TabLabel(RadioButton tab) =>
        (tab.Content as Panel)?.Children.OfType<TextBlock>().FirstOrDefault();

    /// <summary>Measure each tab once with its name showing, so compact widths need no relayout.</summary>
    private void MeasureTabs()
    {
        if (_tabWidths.Count > 0 || TabBar.ActualWidth == 0) return;
        // A switched-off tab is collapsed and takes no room.
        foreach (var tab in TabBar.Children.OfType<RadioButton>().Where(t => t.Visibility == Visibility.Visible))
        {
            var label = TabLabel(tab);
            var labelWidth = label == null ? 0 : label.ActualWidth + 7; // the icon's right margin
            _tabWidths[tab] = (tab.ActualWidth, labelWidth);
        }
    }

    private void FitToolbar()
    {
        if (!IsLoaded) return;
        MeasureTabs();
        if (_tabWidths.Count == 0) return;

        var chrome = TabBarBorder.ActualWidth - TabBar.ActualWidth;
        var fullTabs = _tabWidths.Values.Sum(w => w.Full) + chrome;
        var compactTabs = _tabWidths.Sum(kv => kv.Key.IsChecked == true ? kv.Value.Full : kv.Value.Full - kv.Value.Label) + chrome;
        var right = ToolbarRight.ActualWidth + ToolbarRight.Margin.Right;

        var (compact, shift) = Fit(ActualWidth - 24, fullTabs, compactTabs, right, ToolbarGap);
        TabBarBorder.RenderTransform = shift > 0 ? new TranslateTransform(-shift, 0) : null;
        if (compact == _compactTabs) return;
        _compactTabs = compact;
        ApplyTabLabels();
    }

    /// <summary>
    /// Whether inactive tab names collapse for a toolbar of
    /// <paramref name="width"/>, and how far left the tab bar moves when even
    /// that does not clear the right-hand group. The tab bar is centered, so
    /// the right group has half the width less half the tabs.
    /// </summary>
    internal static (bool CompactTabs, double Shift) Fit(
        double width, double fullTabs, double compactTabs, double right, double gap)
    {
        double Overflow(double tabs) => tabs / 2 + gap + right - width / 2;
        if (Overflow(fullTabs) <= 0) return (false, 0);
        return (true, Math.Max(0, Overflow(compactTabs)));
    }

    private void ApplyTabLabels()
    {
        foreach (var tab in TabBar.Children.OfType<RadioButton>())
        {
            if (TabLabel(tab) is not { } label) continue;
            var show = !_compactTabs || tab.IsChecked == true;
            label.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if ((tab.Content as Panel)?.Children.OfType<FrameworkElement>().FirstOrDefault() is { } icon && icon != label)
                icon.Margin = show ? new Thickness(0, 0, 7, 0) : new Thickness(0);
        }
        // Switching tabs changes which name shows, so the fit can change too.
        Dispatcher.BeginInvoke(FitToolbar, System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
