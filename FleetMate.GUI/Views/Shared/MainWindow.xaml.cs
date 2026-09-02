using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using FleetMate.Core.Models;
using FleetMate.Core.Services;
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

    public void ResetPageCache()
    {
        _pageCache.Clear();
    }

    // ── Authentication popover ────────────────────────────────────
    // Lives in the window chrome so it is reachable from any tab, the same
    // way the macOS toolbar exposes it.

    private void OnAuthClicked(object sender, RoutedEventArgs e)
    {
        PopulateAuthPopup();
        AuthPopup.IsOpen = !AuthPopup.IsOpen;
    }

    private void PopulateAuthPopup()
    {
        if (Application.Current is not App app) return;
        AuthSystemsPanel.Children.Clear();

        foreach (var category in Enum.GetValues<AuthCategory>())
        {
            var systems = app.AuthManager.SystemsForCategory(category);
            if (systems.Count == 0) continue;

            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 4) };
            header.Children.Add(new ModernWpf.Controls.FontIcon { Glyph = CategoryGlyph(category), FontSize = 14, Margin = new Thickness(0, 0, 6, 0) });
            header.Children.Add(new TextBlock { Text = category.DisplayName(), FontWeight = FontWeights.SemiBold, FontSize = 14 });
            AuthSystemsPanel.Children.Add(header);

            foreach (var system in systems)
                AuthSystemsPanel.Children.Add(BuildAuthSystemCard(app, system));
        }

        var refreshBtn = new Button { Content = "Refresh All", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        refreshBtn.Click += async (_, _) =>
        {
            await app.AuthManager.ProbeAllAsync(app.GraphService, app.TdxService, app.SnipeService, app.DevOpsService);
            PopulateAuthPopup();
        };
        AuthSystemsPanel.Children.Add(refreshBtn);
    }

    private Border BuildAuthSystemCard(App app, AuthSystemStatus system)
    {
        var card = new Border
        {
            Background = (Brush)FindResource("SubtleFillBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 4, 0, 4),
            BorderBrush = new SolidColorBrush(StateColor(system.State)),
            BorderThickness = new Thickness(1)
        };

        var content = new DockPanel();

        var icon = new ModernWpf.Controls.FontIcon
        {
            Glyph = system.SystemId.Icon(),
            FontSize = 20,
            Foreground = new SolidColorBrush(StateColor(system.State)),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        DockPanel.SetDock(icon, Dock.Left);
        content.Children.Add(icon);

        var actions = BuildAuthActions(app, system);
        if (actions != null)
        {
            DockPanel.SetDock(actions, Dock.Right);
            content.Children.Add(actions);
        }

        var main = new StackPanel();

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        headerRow.Children.Add(new TextBlock { Text = system.SystemId.DisplayName(), FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 8, 0) });

        var badge = new Border
        {
            Background = new SolidColorBrush(StateColor(system.State)) { Opacity = 0.15 },
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2, 6, 2)
        };
        var badgeContent = new StackPanel { Orientation = Orientation.Horizontal };
        badgeContent.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(StateColor(system.State)), Margin = new Thickness(0, 0, 4, 0) });
        badgeContent.Children.Add(new TextBlock { Text = system.State.StatusLabel, FontSize = 11, Foreground = new SolidColorBrush(StateColor(system.State)) });
        badge.Child = badgeContent;
        headerRow.Children.Add(badge);
        main.Children.Add(headerRow);

        if (system.User != null)
            main.Children.Add(AuthDetailRow("Signed in as", system.User));
        if (system.LastChecked.HasValue)
            main.Children.Add(AuthDetailRow("Last verified", FormatRelative(system.LastChecked.Value)));

        content.Children.Add(main);
        card.Child = content;
        return card;
    }

    private FrameworkElement? BuildAuthActions(App app, AuthSystemStatus system)
    {
        switch (system.SystemId)
        {
            case AuthSystemId.Tdx:
                var tdxBtn = new Button { FontSize = 11, Padding = new Thickness(8, 4, 8, 4), VerticalAlignment = VerticalAlignment.Top };
                if (system.State.IsHealthy)
                {
                    tdxBtn.Content = "Sign Out";
                    tdxBtn.Click += (_, _) => { app.SignOutTdxSso(); PopulateAuthPopup(); };
                }
                else
                {
                    tdxBtn.Content = "Sign In";
                    tdxBtn.Click += (_, _) => { app.ShowTdxSsoLogin(_ => Dispatcher.Invoke(PopulateAuthPopup)); };
                }
                return tdxBtn;

            case AuthSystemId.DevOps:
                var devOpsBtn = new Button { FontSize = 11, Padding = new Thickness(8, 4, 8, 4), VerticalAlignment = VerticalAlignment.Top };
                if (system.State.IsHealthy)
                {
                    devOpsBtn.Content = "Sign Out";
                    devOpsBtn.Click += (_, _) => { app.SignOutDevOpsSso(); PopulateAuthPopup(); };
                }
                else
                {
                    devOpsBtn.Content = "Sign In";
                    devOpsBtn.Click += (_, _) => { app.ShowDevOpsSsoLogin(_ => Dispatcher.Invoke(PopulateAuthPopup)); };
                }
                return devOpsBtn;

            default:
                return null;
        }
    }

    private static TextBlock AuthDetailRow(string label, string value)
    {
        var tb = new TextBlock { FontSize = 11, Margin = new Thickness(0, 1, 0, 1) };
        tb.Inlines.Add(new System.Windows.Documents.Run(label + "  ") { Foreground = new SolidColorBrush(Colors.Gray) });
        tb.Inlines.Add(new System.Windows.Documents.Run(value) { FontFamily = new FontFamily("Consolas") });
        return tb;
    }

    private static Color StateColor(AuthTokenState state) => state.Kind switch
    {
        AuthStateKind.Valid => Colors.Green,
        AuthStateKind.Configured => Colors.Goldenrod,
        AuthStateKind.Authenticating => Colors.DodgerBlue,
        AuthStateKind.Expired => Colors.Orange,
        AuthStateKind.Failed => Colors.Red,
        AuthStateKind.ServicePrincipal => Colors.Orange,
        _ => Colors.Gray
    };

    private static string CategoryGlyph(AuthCategory cat) => cat switch
    {
        AuthCategory.Devices => "",
        AuthCategory.Inventory => "",
        AuthCategory.Tickets => "",
        AuthCategory.Projects => "",
        AuthCategory.Identity => "",
        _ => ""
    };

    private static string FormatRelative(DateTime dt)
    {
        var span = DateTime.Now - dt;
        if (span.TotalMinutes < 2)  return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24)   return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 7)     return $"{(int)span.TotalDays}d ago";
        return dt.ToString("MMM d");
    }
}
