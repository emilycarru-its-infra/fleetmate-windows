using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FleetMate.WinUI.ViewModels;

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
        AuthList.ItemsSource = systems.Select(AuthCardViewModel.From).ToList();
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
}
