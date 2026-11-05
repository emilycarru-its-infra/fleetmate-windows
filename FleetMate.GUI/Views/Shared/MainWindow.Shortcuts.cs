using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FleetMate.Core.Config;

namespace FleetMate.GUI.Views.Shared;

// Back/Forward, next and previous tab, list or board, filters, refresh and
// zoom: the keys FleetMate for Mac keeps in its menus.
public partial class MainWindow
{
    private readonly TabHistory _tabHistory = new();
    private bool _navigatingHistory;
    private NavigationLocation _location = new(LaunchTab);

    private void InitShortcuts()
    {
        _location = new NavigationLocation(LaunchTab, ChromeSections.Current(ToolbarOf(GetOrCreatePage(LaunchTab))));
        // Zoom and Settings › Appearance › Text size are one setting, as on
        // Mac. A zoom saved before they were joined carries over once.
        if (!UserPreferences.HasTextScale && ZoomScale.Load() is var legacy && Math.Abs(legacy - ZoomScale.Default) > 0.001)
            UserPreferences.SetTextScale(legacy);
        // The mouse's back and forward buttons move through tab history too.
        PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.XButton1) e.Handled = RunShortcut(AppShortcut.Back);
            else if (e.ChangedButton == MouseButton.XButton2) e.Handled = RunShortcut(AppShortcut.Forward);
        };
        // A sub-tab chosen in the title bar is a step in history, as a tab is.
        PageToolbarHost.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(OnPageSectionChecked));
        UpdateHistoryButtons();
    }

    /// <summary>Called on every tab change, so Back knows where it came from.</summary>
    private void RecordVisit(NavigationLocation to)
    {
        if (!_navigatingHistory)
            _tabHistory.Visit(_location, to);
        _location = to;
        UpdateHistoryButtons();
    }

    private void OnPageSectionChecked(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not RadioButton segment || PageToolbarHost.Child is not FrameworkElement toolbar) return;
        if (ChromeSections.Current(toolbar) != ChromeSections.NameOf(segment)) return;
        RecordVisit(new NavigationLocation(CurrentTab, ChromeSections.NameOf(segment)));
    }

    private void UpdateHistoryButtons()
    {
        BackButton.IsEnabled = _tabHistory.CanGoBack;
        ForwardButton.IsEnabled = _tabHistory.CanGoForward;
    }

    private void OnBackClicked(object sender, RoutedEventArgs e) => RunShortcut(AppShortcut.Back);

    private void OnForwardClicked(object sender, RoutedEventArgs e) => RunShortcut(AppShortcut.Forward);

    private bool HandleAppShortcut(Key key, ModifierKeys mods) =>
        AppShortcuts.Resolve(key, mods) is { } shortcut && RunShortcut(shortcut);

    private bool RunShortcut(AppShortcut shortcut)
    {
        switch (shortcut)
        {
            case AppShortcut.Back:
                return MoveInHistory(_tabHistory.Back(_location));
            case AppShortcut.Forward:
                return MoveInHistory(_tabHistory.Forward(_location));
            case AppShortcut.NextTab:
                CycleTab(1);
                return true;
            case AppShortcut.PreviousTab:
                CycleTab(-1);
                return true;
            case AppShortcut.ZoomIn:
                SetZoom(UserPreferences.TextScale + AppTextScale.Step);
                return true;
            case AppShortcut.ZoomOut:
                SetZoom(UserPreferences.TextScale - AppTextScale.Step);
                return true;
            case AppShortcut.ActivityLog:
                ActivityLogWindow.ShowSingle(this);
                return true;
            case AppShortcut.Settings:
                SettingsWindow.ShowSingle(this);
                return true;
            case AppShortcut.ActualSize:
                SetZoom(AppTextScale.Default);
                return true;
            default:
                // Refresh, list or board, and filters belong to the page showing.
                return PageCommands.Execute(ContentFrame.Content as Page, shortcut);
        }
    }

    private bool MoveInHistory(NavigationLocation? target)
    {
        if (target is not { } place) return false;
        _navigatingHistory = true;
        try
        {
            NavigateToTab(place.Tab);
            // A sub-tab that has since gone leaves the tab on whichever
            // sub-tab it shows.
            if (place.Section != null) ChromeSections.Show(PageToolbarHost.Child as FrameworkElement, place.Section);
            _location = new NavigationLocation(CurrentTab, ChromeSections.Current(PageToolbarHost.Child as FrameworkElement));
        }
        finally
        {
            _navigatingHistory = false;
        }
        UpdateHistoryButtons();
        return true;
    }

    /// <summary>Next or previous visible tab, wrapping round, in tab-bar order.</summary>
    private void CycleTab(int offset)
    {
        var tabs = TabOrder
            .Where(tag => TabBar.Children.OfType<RadioButton>().Any(r => (r.Tag as string) == tag && r.Visibility == Visibility.Visible))
            .ToList();
        if (tabs.Count == 0) return;
        var current = Math.Max(0, tabs.IndexOf(CurrentTab));
        NavigateToTab(tabs[(current + offset + tabs.Count) % tabs.Count]);
    }

    /// <summary>
    /// Scale the page area and the terminal's text; the toolbar keeps its
    /// size, as on Mac. The value is the Text size setting, which applies it.
    /// Inside a terminal the same keys size only the terminal's text.
    /// </summary>
    // The keys move in the slider's own 5% steps, so the two always agree.
    private static void SetZoom(double value) => UserPreferences.SetTextScale(AppTextScale.Clamp(value));

    private void OnSettingsClicked(object sender, RoutedEventArgs e) => SettingsWindow.ShowSingle(this);
}
