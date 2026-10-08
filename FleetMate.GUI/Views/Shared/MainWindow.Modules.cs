using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FleetMate.Core.Config;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// Enabled Modules and text size, from Settings. A switched-off module's tab
/// leaves the tab bar, and Ctrl+1–8 count only the tabs left showing. Text
/// size scales the page content; the toolbar and the terminal keep their size.
/// </summary>
public partial class MainWindow
{
    private void InitPreferences()
    {
        ApplyModules();
        ApplyTextScale();
        UserPreferences.Changed += () => Dispatcher.Invoke(() =>
        {
            ApplyModules();
            ApplyTextScale();
        });
    }

    /// <summary>The tags of the tabs showing, in tab-bar order.</summary>
    internal IReadOnlyList<string> VisibleTabs => AppModules.Visible(TabOrder, UserPreferences.HiddenModules);

    private void ApplyModules()
    {
        var visible = VisibleTabs;
        var changed = false;
        foreach (var tab in TabBar.Children.OfType<RadioButton>())
        {
            var show = tab.Tag is string tag && visible.Contains(tag);
            var visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (tab.Visibility == visibility) continue;
            tab.Visibility = visibility;
            changed = true;
        }

        // The tab showing was switched off: move to the first one left.
        if (!visible.Contains(CurrentTab)) NavigateToTab(visible[0]);

        if (!changed || !IsLoaded) return;
        // Measure the tab bar again with every name showing, then refit.
        _tabWidths.Clear();
        _compactTabs = false;
        ApplyTabLabels();
    }

    private void ApplyTextScale()
    {
        var scale = UserPreferences.TextScale;
        ContentFrame.LayoutTransform = AppTextScale.IsDefault(scale) ? null : new ScaleTransform(scale, scale);
    }

    /// <summary>Ctrl+1–8 against the tabs showing, so a hidden tab never takes a number.</summary>
    private string? VisibleTabShortcut(System.Windows.Input.Key key)
    {
        if (TabShortcut(key) is not { } byPosition) return null;
        var index = Array.IndexOf(TabOrder, byPosition);
        var visible = VisibleTabs;
        return index >= 0 && index < visible.Count ? visible[index] : null;
    }
}
