using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FleetMate.WinUI.ViewModels;

namespace FleetMate.WinUI.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RenderModules();
        RenderAuth();
        await ProbeAsync();
    }

    private void RenderModules()
    {
        var app = App.Current;
        ModulesList.ItemsSource = new List<ModuleRow>
        {
            new("Devices", app.GraphService != null, "Microsoft Graph / Intune"),
            new("Inventory", app.SnipeService != null, "Snipe-IT"),
            new("Tickets", app.TdxService != null, "TeamDynamix"),
            new("Projects", app.DevOpsService != null, "Azure DevOps"),
            new("Identity", app.GraphService != null, "Entra ID (Microsoft Graph)"),
        };
    }

    private void RenderAuth()
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
        catch { /* per-system Failed states surface in the cards */ }
        finally
        {
            RenderAuth();
            RefreshButton.IsEnabled = true;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await ProbeAsync();

    private async void SetupButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OnboardingDialog(XamlRoot);
        if (await dialog.ShowAsync())
        {
            App.Current.ReloadConfigAndServices();
            RenderModules();
            RenderAuth();
            await ProbeAsync();
        }
    }

    private sealed record ModuleRow(string Name, bool IsConfigured, string Backing)
    {
        public string Caption => IsConfigured ? $"{Backing} — configured" : $"{Backing} — not configured";
    }
}
