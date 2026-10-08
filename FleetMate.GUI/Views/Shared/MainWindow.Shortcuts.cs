using System.Windows;
using System.Windows.Controls;
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
    private string _lastTab = "Development";

    private void InitShortcuts()
    {
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
    }

    /// <summary>Called on every tab change, so Back knows where it came from.</summary>
    private void RecordTabVisit(string tag)
    {
        if (!_navigatingHistory && tag != "Settings" && _lastTab != "Settings")
            _tabHistory.Visit(_lastTab, tag);
        _lastTab = tag;
    }

    private bool HandleAppShortcut(Key key, ModifierKeys mods) =>
        AppShortcuts.Resolve(key, mods) is { } shortcut && RunShortcut(shortcut);

    private bool RunShortcut(AppShortcut shortcut)
    {
        switch (shortcut)
        {
            case AppShortcut.Back:
                return MoveInHistory(_tabHistory.Back(CurrentTab));
            case AppShortcut.Forward:
                return MoveInHistory(_tabHistory.Forward(CurrentTab));
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
            case AppShortcut.ActualSize:
                SetZoom(AppTextScale.Default);
                return true;
            default:
                // Refresh, list or board, and filters belong to the page showing.
                return PageCommands.Execute(ContentFrame.Content as Page, shortcut);
        }
    }

    private bool MoveInHistory(string? tag)
    {
        if (tag == null) return false;
        _navigatingHistory = true;
        try
        {
            NavigateToTab(tag);
        }
        finally
        {
            _navigatingHistory = false;
        }
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
    /// Scale the page area; the toolbar and terminal keep their size, as on
    /// Mac. The value is the Text size setting, which applies it.
    /// </summary>
    // The keys move in the slider's own 5% steps, so the two always agree.
    private static void SetZoom(double value) => UserPreferences.SetTextScale(AppTextScale.Clamp(value));
}
