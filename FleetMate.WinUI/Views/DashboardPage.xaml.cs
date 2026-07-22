using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using FleetMate.Core.Models;

namespace FleetMate.WinUI.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Render();
        await ProbeAsync();
    }

    /// <summary>Rebuild the auth-status cards from the shared AuthManager (UI thread).</summary>
    private void Render()
    {
        var systems = App.Current.AuthManager.ConfiguredSystems;
        AuthList.ItemsSource = systems.Select(ToCard).ToList();
        EmptyHint.Visibility = systems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ProbeAsync()
    {
        RefreshButton.IsEnabled = false;
        try
        {
            var app = App.Current;
            await app.AuthManager.ProbeAllAsync(app.GraphService, app.TdxService, app.SnipeService, app.DevOpsService);
        }
        catch { /* probe failures surface as per-system Failed states */ }
        finally
        {
            Render();
            RefreshButton.IsEnabled = true;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await ProbeAsync();

    private static AuthCard ToCard(AuthSystemStatus s) => new()
    {
        Name = s.SystemId.DisplayName(),
        Icon = s.SystemId.Icon(),
        Category = s.SystemId.Category().DisplayName(),
        StatusLabel = s.State.StatusLabel,
        StatusBrush = new SolidColorBrush(ColorFromName(s.State.StatusColor)),
        User = s.State.User ?? s.User ?? "",
    };

    private static Color ColorFromName(string name) => name switch
    {
        "Green" => Colors.Green,
        "Gold" => Colors.Gold,
        "DodgerBlue" => Colors.DodgerBlue,
        "Orange" => Colors.Orange,
        "Red" => Colors.Red,
        _ => Colors.Gray,
    };

    private sealed class AuthCard
    {
        public string Name { get; init; } = "";
        public string Icon { get; init; } = "";
        public string Category { get; init; } = "";
        public string StatusLabel { get; init; } = "";
        public Brush StatusBrush { get; init; } = new SolidColorBrush(Colors.Gray);
        public string User { get; init; } = "";
        public Visibility UserVisibility => string.IsNullOrEmpty(User) ? Visibility.Collapsed : Visibility.Visible;
    }
}
