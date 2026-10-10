using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FleetMate.Core.Config;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// Settings in a window of its own, like the Mac app's Settings window: the
/// main window keeps its tab underneath, and Close or Esc puts it away.
/// </summary>
public sealed class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    /// <summary>Show the one Settings window, opening it if needed.</summary>
    public static void ShowSingle(Window? owner)
    {
        if (_open == null)
        {
            _open = new SettingsWindow(owner);
            _open.Closed += (_, _) => _open = null;
            _open.Show();
        }
        else
        {
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            _open.Activate();
        }
    }

    private SettingsWindow(Window? owner)
    {
        Owner = owner;
        Title = $"{AppEdition.Current.Name} Settings";
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Width = 900;
        Height = 720;
        MinWidth = 560;
        MinHeight = 420;
        if (owner != null)
        {
            Width = Math.Min(Width, Math.Max(MinWidth, owner.ActualWidth - 80));
            Height = Math.Min(Height, Math.Max(MinHeight, owner.ActualHeight - 80));
        }
        SetResourceReference(BackgroundProperty, "ApplicationPageBackgroundThemeBrush");
        SetResourceReference(ForegroundProperty, "SystemControlForegroundBaseHighBrush");

        var close = new Button
        {
            Content = "Close",
            IsCancel = true,
            MinWidth = 90,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 24, 16),
            ToolTip = "Close Settings (Esc)",
        };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Bottom);

        var frame = new Frame { NavigationUIVisibility = System.Windows.Navigation.NavigationUIVisibility.Hidden };
        frame.Navigate(new SettingsPage());

        var root = new DockPanel();
        root.Children.Add(close);
        root.Children.Add(frame);
        Content = root;

        // IsCancel covers Esc while the button's scope has focus; a text box or
        // combo box inside the page can swallow it, so catch it here as well.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            // Esc on an open dropdown closes the dropdown, not the window.
            if (Keyboard.FocusedElement is ComboBox { IsDropDownOpen: true }) return;
            if (Keyboard.FocusedElement is ComboBoxItem item
                && ItemsControl.ItemsControlFromItemContainer(item) is ComboBox { IsDropDownOpen: true }) return;
            e.Handled = true;
            Close();
        };
    }
}
