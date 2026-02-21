using System.Windows;
using System.Windows.Controls;

namespace FleetMate.GUI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Navigate to Dashboard on startup
        ContentFrame.Navigate(new DashboardPage());
        TabDashboard.IsChecked = true;
    }

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (ContentFrame == null) return; // Not yet initialized
        if (sender is RadioButton radio && radio.Tag is string tag)
        {
            NavigateToPage(tag);
        }
    }

    /// <summary>
    /// Navigate to a tab by tag name. Called from Dashboard for drill-down navigation.
    /// </summary>
    public void NavigateToTab(string tag)
    {
        foreach (var child in TabBar.Children)
        {
            if (child is RadioButton radio && radio.Tag?.ToString() == tag)
            {
                radio.IsChecked = true; // fires OnTabChecked -> NavigateToPage
                return;
            }
        }
    }

    private void NavigateToPage(string tag)
    {
        switch (tag)
        {
            case "Dashboard":
                ContentFrame.Navigate(new DashboardPage());
                break;
            case "Devices":
                ContentFrame.Navigate(new IntunePage());
                break;
            case "Inventory":
                ContentFrame.Navigate(new AssetsPage());
                break;
            case "Tickets":
                ContentFrame.Navigate(new TicketsPage());
                break;
            case "Projects":
                ContentFrame.Navigate(new BoardsPage());
                break;
            case "Identity":
                ContentFrame.Navigate(new IdentityPage());
                break;
            case "Settings":
                ContentFrame.Navigate(new SettingsPage());
                break;
        }
    }
}
