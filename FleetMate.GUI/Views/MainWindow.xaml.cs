using System.Windows;
using ModernWpf.Controls;

namespace FleetMate.GUI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Navigate to Dashboard on startup
        ContentFrame.Navigate(typeof(DashboardPage));
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ContentFrame.Navigate(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItem is NavigationViewItem item)
        {
            var tag = item.Tag?.ToString();
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
                case "Errors":
                    ContentFrame.Navigate(typeof(ErrorsPage));
                    break;
                case "Users":
                    ContentFrame.Navigate(typeof(UsersPage));
                    break;
                case "Groups":
                    ContentFrame.Navigate(typeof(GroupsPage));
                    break;
                case "Tickets":
                    ContentFrame.Navigate(typeof(TicketsPage));
                    break;
                case "Tasks":
                    ContentFrame.Navigate(typeof(WorkItemsPage));
                    break;
                case "Boards":
                    ContentFrame.Navigate(typeof(BoardsPage));
                    break;
            }
        }
    }
}
