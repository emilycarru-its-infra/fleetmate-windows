using System.Windows;
using System.Windows.Controls;
using FleetMate.GUI.Views.Devices;
using FleetMate.GUI.Views.Inventory;
using FleetMate.GUI.Views.Tickets;
using FleetMate.GUI.Views.Projects;
using FleetMate.GUI.Views.Identity;

namespace FleetMate.GUI.Views.Shared;

public partial class MainWindow : Window
{
    // Page cache: keep views alive across tab switches
    private readonly Dictionary<string, Page> _pageCache = new();

    public MainWindow()
    {
        InitializeComponent();

        // Navigate to Dashboard on startup
        ContentFrame.Navigate(GetOrCreatePage("Dashboard"));
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

    private Page GetOrCreatePage(string tag) => tag switch
    {
        // Settings is intentionally NOT cached (always fresh)
        "Settings" => new SettingsPage(),
        _ => _pageCache.TryGetValue(tag, out var cached) ? cached : (_pageCache[tag] = CreatePage(tag))
    };

    private static Page CreatePage(string tag) => tag switch
    {
        "Dashboard" => new DashboardPage(),
        "Devices" => new IntunePage(),
        "Inventory" => new AssetsPage(),
        "Tickets" => new TicketsPage(),
        "Projects" => new BoardsPage(),
        "Identity" => new IdentityPage(),
        _ => new DashboardPage()
    };

    private void NavigateToPage(string tag)
    {
        ContentFrame.Navigate(GetOrCreatePage(tag));
    }
}
