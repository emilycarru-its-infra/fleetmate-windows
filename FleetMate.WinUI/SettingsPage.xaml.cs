using FleetMate.Core.Config;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;

namespace FleetMate.WinUI;

public sealed partial class SettingsPage : Page
{
    private const string RegistryPath = @"SOFTWARE\FleetMate";
    private bool _loading = true;
    private TdxSignInWindow? _tdxSignInWindow;

    public SettingsPage()
    {
        InitializeComponent();
        var config = FleetMateConfig.LoadDesktop();
        TenantId.Text = config.Graph?.TenantId ?? "";
        ClientId.Text = config.Graph?.ClientId ?? "";
        DevOpsOrganization.Text = config.AzureDevOps?.Organization ?? "";
        DevOpsProject.Text = config.AzureDevOps?.Project ?? "";
        SnipeUrl.Text = config.SnipeUrl ?? "";
        TdxUrl.Text = config.Tdx?.BaseUrl ?? "";
        TdxAppId.Text = (config.Tdx?.TicketingAppId ?? config.Tdx?.AppId)?.ToString() ?? "";
        ReportMateUrl.Text = config.ReportMateUrl ?? "";
        ElevationResourceGroup.Text = config.Elevation?.ResourceGroup ?? "";
        ElevationImage.Text = config.Elevation?.AcrImage ?? "";
        ElevationTranscript.Text = config.Elevation?.TranscriptAccount ?? "";
        ElevationIdentityPrefix.Text = config.Elevation?.IdentityPrefix ?? "";
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        ThemePicker.SelectedIndex = key?.GetValue("UiTheme")?.ToString() switch { "Light" => 1, "Dark" => 2, _ => 0 };
        _loading = false;
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
        Set(key, "GraphTenantId", TenantId.Text);
        Set(key, "GraphClientId", ClientId.Text);
        Set(key, "DevOpsOrganization", DevOpsOrganization.Text);
        Set(key, "DevOpsProject", DevOpsProject.Text);
        Set(key, "SnipeUrl", SnipeUrl.Text);
        Set(key, "TdxBaseUrl", TdxUrl.Text);
        Set(key, "TdxTicketingAppId", TdxAppId.Text);
        Set(key, "ReportMateUrl", ReportMateUrl.Text);
        Set(key, "ElevationResourceGroup", ElevationResourceGroup.Text);
        Set(key, "ElevationAcrImage", ElevationImage.Text);
        Set(key, "ElevationTranscriptAccount", ElevationTranscript.Text);
        Set(key, "ElevationIdentityPrefix", ElevationIdentityPrefix.Text);
        foreach (var secret in new[] { "GraphClientSecret", "SnipeApiKey", "ReportMatePassphrase", "TdxUsername", "TdxPassword", "TdxBeid", "TdxWebServicesKey" })
            key.DeleteValue(secret, false);
        App.Runtime.Reload();
        SaveResult.Severity = InfoBarSeverity.Success;
        SaveResult.Title = "Settings saved";
        SaveResult.Message = "The active service configuration has reloaded; no app restart is required.";
        SaveResult.IsOpen = true;
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemePicker.SelectedItem is not ComboBoxItem item) return;
        var theme = item.Tag?.ToString() ?? "System";
        App.Window.Content.As<FrameworkElement>().RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
        key.SetValue("UiTheme", theme);
    }

    private void OnTdxSignInClicked(object sender, RoutedEventArgs e)
    {
        App.Runtime.Reload();
        var baseUrl = App.Runtime.Config.Tdx?.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            TdxSignInStatus.Text = "Enter and save the TeamDynamix base URL first.";
            return;
        }

        _tdxSignInWindow = new TdxSignInWindow(baseUrl);
        _tdxSignInWindow.AuthenticationCompleted += (_, result) =>
        {
            App.Runtime.SetTdxSession(result);
            TdxSignInStatus.Text = $"Signed in as {result.UserName ?? result.UserEmail ?? "operator"}; ticket API will be checked on the dashboard.";
            _tdxSignInWindow = null;
        };
        _tdxSignInWindow.Activate();
    }

    private static void Set(RegistryKey key, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) key.DeleteValue(name, false);
        else key.SetValue(name, value.Trim());
    }
}

internal static class ObjectExtensions
{
    public static T As<T>(this object value) where T : class => (T)value;
}
