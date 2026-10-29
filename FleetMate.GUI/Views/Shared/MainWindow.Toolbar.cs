using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// Fits the toolbar to the window, as the macOS toolbar does on a laptop
/// screen. The tabs stay centered and are the last thing to give way. When
/// the right-hand group would run into the tabs, it yields first: search
/// narrows and the elevation status drops to its dot (its name stays in the
/// tooltip). If that is not enough, inactive tabs drop to their icon (name in
/// the tooltip; the selected tab keeps its name). If even that does not fit,
/// the tab bar slides left off center just far enough to clear the right-hand
/// group, so nothing overlaps. The leading group (Back, Forward and the tab's
/// sub-tabs) takes the room left of the tab bar; when that is too little, the
/// sub-tabs fold into one pill naming the open one, as the macOS toolbar folds
/// its segments in a narrow window.
/// </summary>
public partial class MainWindow
{
    /// <summary>Space kept between the tab bar and the right-hand group.</summary>
    private const double ToolbarGap = 16;

    /// <summary>The search field's width with room to spare, and in a narrow window.</summary>
    internal const double SearchFullWidth = 320;
    internal const double SearchCompactWidth = 200;

    /// <summary>Where the results drop down, relative to the field's left edge, at full width.</summary>
    private const double SearchPopupOffset = -300;

    private bool _compactTabs;
    private bool _compactRight;
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
        // So does a tab's Inbox button, and the tab bar's names.
        ToolbarLeft.SizeChanged += (_, e) => { if (e.WidthChanged) FitToolbar(); };
        TabBarBorder.SizeChanged += (_, e) => { if (e.WidthChanged) FitToolbar(); };
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

    /// <summary>
    /// What the right-hand group gives back when it yields: the narrower search
    /// field and, while the elevation status shows, its label and the gap
    /// after its dot.
    /// </summary>
    private double RightGroupSavings()
    {
        var savings = SearchFullWidth - SearchCompactWidth;
        if (ElevationButton.Visibility != Visibility.Visible) return savings;

        // Measure the label even while it is hidden, so the saving is known
        // before the first time it shows.
        var hidden = ElevationText.Visibility != Visibility.Visible;
        if (hidden) ElevationText.Visibility = Visibility.Visible;
        ElevationText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var label = ElevationText.DesiredSize.Width + 6; // the dot's right margin
        if (hidden) ElevationText.Visibility = Visibility.Collapsed;
        return savings + label;
    }

    private void FitToolbar()
    {
        if (!IsLoaded) return;
        MeasureTabs();
        if (_tabWidths.Count == 0) return;

        var chrome = TabBarBorder.ActualWidth - TabBar.ActualWidth;
        var fullTabs = _tabWidths.Values.Sum(w => w.Full) + chrome;
        var compactTabs = _tabWidths.Sum(kv => kv.Key.IsChecked == true ? kv.Value.Full : kv.Value.Full - kv.Value.Label) + chrome;
        var savings = RightGroupSavings();
        var rightNow = ToolbarRight.ActualWidth + ToolbarRight.Margin.Right;
        var fullRight = _compactRight ? rightNow + savings : rightNow;

        var leftMin = LeadingMinimum();
        var fit = Fit(ActualWidth - 24, fullTabs, compactTabs, fullRight, fullRight - savings, ToolbarGap, leftMin);
        TabBarBorder.RenderTransform = fit.Shift > 0 ? new TranslateTransform(-fit.Shift, 0) : null;
        FitPageSegments(fit.Shift);

        if (fit.CompactRight != _compactRight)
        {
            _compactRight = fit.CompactRight;
            ApplyRightGroup();
        }
        if (fit.CompactTabs == _compactTabs) return;
        _compactTabs = fit.CompactTabs;
        ApplyTabLabels();
    }

    /// <summary>
    /// How the toolbar fits a width of <paramref name="width"/>. The tab bar is
    /// centered, so the right group has half the width less half the tabs. The
    /// right group yields first (<paramref name="right"/> down to
    /// <paramref name="compactRight"/>); only then do inactive tab names
    /// collapse, and last the tab bar moves left by <c>Shift</c>.
    /// </summary>
    /// <remarks>
    /// <paramref name="left"/> is the least the leading group can shrink to,
    /// Back and Forward with the sub-tabs folded: the right group yields for it
    /// too, and the tab bar never slides over it.
    /// </remarks>
    internal static (bool CompactRight, bool CompactTabs, double Shift) Fit(
        double width, double fullTabs, double compactTabs, double right, double compactRight, double gap,
        double left = 0)
    {
        double Overflow(double tabs, double group) => tabs / 2 + gap + group - width / 2;
        bool Fits(double tabs, double group) => Overflow(tabs, group) <= 0 && Overflow(tabs, left) <= 0;
        if (Fits(fullTabs, right)) return (false, false, 0);
        if (Fits(fullTabs, compactRight)) return (true, false, 0);
        var room = left > 0 ? Math.Max(0, -Overflow(compactTabs, left)) : double.MaxValue;
        return (true, true, Math.Min(room, Math.Max(0, Overflow(compactTabs, compactRight))));
    }

    /// <summary>A folded sub-tab pill's width, near enough, for keeping room for it.</summary>
    private const double FoldedSegmentsWidth = 96;

    /// <summary>Agent and Back and Forward: the leading buttons that never fold.</summary>
    private double LeadingButtonsWidth =>
        TerminalToggleButton.ActualWidth + TerminalToggleButton.Margin.Left + TerminalToggleButton.Margin.Right
        + HistoryButtons.ActualWidth;

    /// <summary>Agent, Back and Forward, plus the showing tab's toolbar with its sub-tabs folded.</summary>
    private double LeadingMinimum()
    {
        var history = LeadingButtonsWidth;
        if (PageToolbarHost.Child is not FrameworkElement toolbar || PageToolbarHost.Visibility != Visibility.Visible)
            return history;
        var (_, others) = SegmentsOf(toolbar);
        return history + PageToolbarHost.Margin.Left + others + FoldedSegmentsWidth;
    }

    /// <summary>
    /// The showing tab's segment pill, if it has one, and the width of the rest
    /// of its toolbar (its Inbox, Refresh and the like), which never folds.
    /// </summary>
    private static (SegmentMenuButton? Pill, double Others) SegmentsOf(FrameworkElement toolbar)
    {
        var pill = FindPill(toolbar);
        if (pill?.Parent is not FrameworkElement segmentHost || toolbar is not Panel panel || ReferenceEquals(segmentHost, toolbar))
            return (pill, 0);
        var others = panel.Children.OfType<FrameworkElement>()
            .Where(c => !ReferenceEquals(c, segmentHost) && c.Visibility == Visibility.Visible)
            .Sum(c => c.ActualWidth + c.Margin.Left + c.Margin.Right);
        return (pill, others);
    }

    private static SegmentMenuButton? FindPill(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is SegmentMenuButton pill) return pill;
            if (FindPill(child) is { } nested) return nested;
        }
        return null;
    }

    /// <summary>
    /// Give the showing tab's sub-tabs the room between Back and Forward and the
    /// tab bar's left edge, so they fold into their pill only when they would
    /// run into the tabs.
    /// </summary>
    private void FitPageSegments(double shift)
    {
        if (PageToolbarHost.Child is not FrameworkElement toolbar) return;
        var (pill, others) = SegmentsOf(toolbar);
        if (pill == null) return;
        var tabsLeft = (ToolbarGrid.ActualWidth - TabBarBorder.ActualWidth) / 2 - shift;
        var room = tabsLeft - ToolbarGap - LeadingButtonsWidth - PageToolbarHost.Margin.Left - others;
        pill.AvailableWidth = Math.Max(1, room);
    }

    /// <summary>Narrow the search field and drop the elevation label, or put them back.</summary>
    private void ApplyRightGroup()
    {
        var width = _compactRight ? SearchCompactWidth : SearchFullWidth;
        SearchField.Width = width;
        // Keep the results' right edge where it was relative to the field.
        SearchPopup.HorizontalOffset = SearchPopupOffset - (SearchFullWidth - width);
        ElevationText.Visibility = _compactRight ? Visibility.Collapsed : Visibility.Visible;
        ElevationDot.Margin = _compactRight ? new Thickness(0) : new Thickness(0, 0, 6, 0);
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
