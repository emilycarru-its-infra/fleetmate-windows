using System.Windows;
using System.Windows.Controls;

namespace FleetMate.GUI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Navigate to Tickets on startup (matching macOS default)
        ContentFrame.Navigate(typeof(TicketsPage));
        TabTickets.IsChecked = true;
    }

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton radio && radio.Tag is string tag)
        {
            switch (tag)
            {
                case "Dashboard":
                    ContentFrame.Navigate(typeof(DashboardPage));
                    break;
                case "Devices":
                    ContentFrame.Navigate(typeof(IntunePage));
                    break;
                case "Inventory":
                    ContentFrame.Navigate(typeof(AssetsPage));
                    break;
                case "Tickets":
                    ContentFrame.Navigate(typeof(TicketsPage));
                    break;
                case "Boards":
                    ContentFrame.Navigate(typeof(BoardsPage));
                    break;
                case "Identity":
                    ContentFrame.Navigate(typeof(IdentityPage));
                    break;
                case "Settings":
                    ContentFrame.Navigate(typeof(SettingsPage));
                    break;
            }
        }
    }
}
