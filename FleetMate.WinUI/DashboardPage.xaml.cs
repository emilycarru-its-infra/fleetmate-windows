using Microsoft.UI.Xaml.Controls;

namespace FleetMate.WinUI;

public sealed partial class DashboardPage : Page
{
    public DashboardPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var checks = await App.Runtime.CheckAsync();
        var passed = checks.Count(x => x.Success);
        StatusTitle.Text = $"{passed} of {checks.Count} services connected";
        StatusDetail.Text = string.Join("   ·   ", checks.Select(x => $"{(x.Success ? "✓" : "✕")} {x.Name}: {x.Detail}"));
    }
}
