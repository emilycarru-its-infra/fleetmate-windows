using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace FleetMate.WinUI;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "FleetMate";
        AppWindow.Resize(new SizeInt32(1440, 900));
        Navigation.SelectedItem = Navigation.MenuItems[0];
        ShowDashboard();
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ContentFrame.Content = new SettingsPage();
            return;
        }

        var tag = (args.SelectedItemContainer as NavigationViewItem)?.Tag?.ToString();
        if (tag == "Dashboard") ShowDashboard();
        else ContentFrame.Content = new ModulePage(tag ?? "FleetMate");
    }

    private void ShowDashboard() => ContentFrame.Content = new DashboardPage();

}
