using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// Reserves the full search field before arranging module navigation. Names
/// compact first, then a module picker takes over; search never leaves the toolbar.
/// </summary>
public partial class MainWindow
{
    /// <summary>Space kept between the tab bar and the right-hand group.</summary>
    private const double ToolbarGap = 16;

    /// <summary>The search field's width with room to spare, and in a narrow window.</summary>
    internal const double SearchFullWidth = 320;
    internal const double SearchCompactWidth = SearchFullWidth;

    /// <summary>Where the results drop down, relative to the field's left edge, at full width.</summary>
    private const double SearchPopupOffset = -300;

    private bool _compactTabs;
    private readonly Dictionary<RadioButton, (double Full, double Label)> _tabWidths = new();

    private void InitToolbarFit()
    {
        foreach (var tab in TabBar.Children.OfType<RadioButton>())
        {
            if (TabLabel(tab) is { } label)
            {
                tab.ToolTip ??= label.Text;
                // The name stays the tab's accessible name when only its icon shows.
                if (string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(tab)))
                    System.Windows.Automation.AutomationProperties.SetName(tab, label.Text);
            }
            tab.Checked += (_, _) => ApplyTabLabels();
        }
        SizeChanged += (_, _) => FitToolbar();
        // The elevation label and Graphs button come and go; refit when they do.
        ToolbarRight.SizeChanged += (_, e) => { if (e.WidthChanged) FitToolbar(); };
        FitToolbar();
    }

    private static TextBlock? TabLabel(RadioButton tab) =>
        (tab.Content as Panel)?.Children.OfType<TextBlock>().FirstOrDefault();

    /// <summary>Measure names even while the navigation strip is folded into its picker.</summary>
    private void MeasureTabs()
    {
        if (_tabWidths.Count > 0) return;
        foreach (var tab in TabBar.Children.OfType<RadioButton>().Where(tab => tab.Visibility == Visibility.Visible))
        {
            var label = TabLabel(tab);
            var previous = label?.Visibility;
            if (label != null) label.Visibility = Visibility.Visible;
            tab.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var labelWidth = label?.DesiredSize.Width ?? 0;
            _tabWidths[tab] = (tab.DesiredSize.Width, labelWidth + (labelWidth > 0 ? 7 : 0));
            if (label != null && previous != null) label.Visibility = previous.Value;
        }
    }

    private void FitToolbar()
    {
        if (!IsLoaded) return;
        MeasureTabs();
        if (_tabWidths.Count == 0) return;
        // The Auto-sized right column reserves search before navigation is arranged.
        SearchField.Width = SearchFullWidth;
        SearchField.Visibility = Visibility.Visible;
        SearchPopup.HorizontalOffset = SearchPopupOffset;
        if (TicketsOnly)
        {
            TabBarBorder.Visibility = TabPickerButton.Visibility = Visibility.Collapsed;
            return;
        }
        ElevationText.Visibility = Visibility.Collapsed;
        var chrome = TabBarBorder.BorderThickness.Left + TabBarBorder.BorderThickness.Right
            + TabBarBorder.Padding.Left + TabBarBorder.Padding.Right;
        var full = _tabWidths.Values.Sum(value => value.Full) + chrome;
        var compact = _tabWidths.Sum(pair => pair.Key.IsChecked == true
            ? pair.Value.Full : pair.Value.Full - pair.Value.Label) + chrome;
        var available = Math.Max(0, TabNavigationHost.ActualWidth - ToolbarGap);
        var layout = NavigationFit(available, full, compact);
        if (_compactTabs != layout.CompactTabs)
        {
            _compactTabs = layout.CompactTabs;
            ApplyTabLabels();
        }
        TabBarBorder.RenderTransform = null;
        TabBarBorder.Visibility = layout.Picker ? Visibility.Collapsed : Visibility.Visible;
        TabPickerButton.Visibility = layout.Picker ? Visibility.Visible : Visibility.Collapsed;
        TabPickerButton.MaxWidth = Math.Max(1, Math.Min(160, available));
        TabPickerLabel.Text = (TabBar.Children.OfType<RadioButton>().FirstOrDefault(tab => tab.IsChecked == true)?.ToolTip?.ToString() ?? "Modules") + " ▾";
    }

    internal static (bool CompactTabs, bool Picker) NavigationFit(double available, double full, double compact) =>
        available >= full ? (false, false) : available >= compact ? (true, false) : (true, true);

    private void OnTabPickerClicked(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = TabPickerButton };
        foreach (var tab in TabBar.Children.OfType<RadioButton>().Where(tab => tab.Visibility == Visibility.Visible))
        {
            var item = new MenuItem { Header = tab.ToolTip?.ToString() ?? tab.Tag?.ToString(), IsCheckable = true, IsChecked = tab.IsChecked == true };
            item.Click += (_, _) => { tab.IsChecked = true; FitToolbar(); };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
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
