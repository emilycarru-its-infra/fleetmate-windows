using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// The app-wide keys FleetMate for Mac has in its menus, mapped to Windows
/// conventions: Alt+Left/Right for Back and Forward, Ctrl+Tab for the next tab.
/// </summary>
internal enum AppShortcut
{
    Back,
    Forward,
    NextTab,
    PreviousTab,
    ShowList,
    ShowBoard,
    ToggleFilters,
    ClearFilters,
    Refresh,
    ZoomIn,
    ZoomOut,
    ActualSize,
    ActivityLog,
}

internal static class AppShortcuts
{
    /// <summary>
    /// The shortcut for a key and modifiers, or null. <paramref name="key"/> is
    /// the real key: for Alt combinations WPF reports Key.System, so pass SystemKey.
    /// </summary>
    internal static AppShortcut? Resolve(Key key, ModifierKeys mods)
    {
        const ModifierKeys Ctrl = ModifierKeys.Control;
        const ModifierKeys Shift = ModifierKeys.Shift;
        const ModifierKeys Alt = ModifierKeys.Alt;

        return (key, mods) switch
        {
            (Key.Left, Alt) or (Key.BrowserBack, ModifierKeys.None) => AppShortcut.Back,
            (Key.Right, Alt) or (Key.BrowserForward, ModifierKeys.None) => AppShortcut.Forward,
            (Key.Tab, Ctrl) or (Key.PageDown, Ctrl) => AppShortcut.NextTab,
            (Key.Tab, Ctrl | Shift) or (Key.PageUp, Ctrl) => AppShortcut.PreviousTab,
            (Key.D1 or Key.NumPad1, Ctrl | Alt) => AppShortcut.ShowList,
            (Key.D2 or Key.NumPad2, Ctrl | Alt) => AppShortcut.ShowBoard,
            (Key.F, Ctrl | Shift) => AppShortcut.ToggleFilters,
            (Key.K, Ctrl | Shift) => AppShortcut.ClearFilters,
            (Key.F5, ModifierKeys.None) or (Key.R, Ctrl) => AppShortcut.Refresh,
            (Key.OemPlus or Key.Add, Ctrl) or (Key.OemPlus, Ctrl | Shift) => AppShortcut.ZoomIn,
            (Key.OemMinus or Key.Subtract, Ctrl) => AppShortcut.ZoomOut,
            (Key.D0 or Key.NumPad0, Ctrl) => AppShortcut.ActualSize,
            // Not Ctrl+Alt+L: other apps commonly take that as a global hotkey,
            // and a global hotkey reaches them before FleetMate's window sees it.
            (Key.L, Ctrl | Shift) => AppShortcut.ActivityLog,
            _ => null,
        };
    }
}

/// <summary>
/// A page that handles shortcuts itself. Pages that don't are reached by
/// their control names instead; see <see cref="PageCommands"/>.
/// </summary>
internal interface IPageCommands
{
    /// <summary>True when the page acted on the shortcut.</summary>
    bool Execute(AppShortcut shortcut);
}

internal static class PageCommands
{
    /// <summary>
    /// Run a page-level shortcut on <paramref name="page"/>. Pages share control
    /// names (RefreshButton, ListViewRadio and so on), so most need no code of
    /// their own. Returns false when the page has nothing for it.
    /// </summary>
    internal static bool Execute(Page? page, AppShortcut shortcut)
    {
        if (page == null) return false;
        if (page is IPageCommands own && own.Execute(shortcut)) return true;

        return shortcut switch
        {
            AppShortcut.Refresh => Click(page, "RefreshButton"),
            AppShortcut.ShowList => Check(page, "ListViewRadio") || Check(page, "ListModeRadio"),
            AppShortcut.ShowBoard => Check(page, "BoardViewRadio") || Check(page, "BoardModeRadio"),
            AppShortcut.ToggleFilters => Click(page, "FiltersButton"),
            _ => false,
        };
    }

    private static bool Click(FrameworkElement page, string name)
    {
        if (page.FindName(name) is not ButtonBase { IsEnabled: true, IsVisible: true } button) return false;
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        return true;
    }

    private static bool Check(FrameworkElement page, string name)
    {
        if (page.FindName(name) is not RadioButton { IsEnabled: true, IsVisible: true } radio) return false;
        radio.IsChecked = true;
        return true;
    }
}
