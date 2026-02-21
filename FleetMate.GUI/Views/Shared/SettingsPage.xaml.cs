using Microsoft.Win32;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Config;

namespace FleetMate.GUI.Views.Shared;

public partial class SettingsPage : Page
{
    private const string RegistryPath = @"SOFTWARE\FleetMate";

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadSettings();
    }

    // ── Load ────────────────────────────────────────────────────────────────

    private void LoadSettings()
    {
        var config = Application.Current is App app ? app.Config : FleetMateConfig.Load();

        // Config file path
        var cfgPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".fleetmate", "config.yaml");
        ConfigPathTextBox.Text = cfgPath;

        // Microsoft Graph
        TenantIdTextBox.Text  = config.Graph?.TenantId  ?? "";
        ClientIdTextBox.Text  = config.Graph?.ClientId  ?? "";
        if (!string.IsNullOrEmpty(config.Graph?.ClientSecret))
            ClientSecretBox.Password = config.Graph.ClientSecret;

        // Azure DevOps
        AdoOrgTextBox.Text     = config.AzureDevOps?.Organization ?? "";
        AdoProjectTextBox.Text = config.AzureDevOps?.Project      ?? "";
        // NO PAT — Azure DevOps uses SSO only (browser OAuth2 PKCE or Azure CLI)

        // Snipe-IT
        SnipeUrlTextBox.Text = config.SnipeUrl ?? "";
        if (!string.IsNullOrEmpty(config.SnipeApiKey))
            SnipeApiKeyBox.Password = config.SnipeApiKey;

        // TDX
        TdxUrlTextBox.Text      = config.Tdx?.BaseUrl  ?? "";
        TdxUsernameTextBox.Text = config.Tdx?.Username ?? "";
        if (!string.IsNullOrEmpty(config.Tdx?.Password))
            TdxPasswordBox.Password = config.Tdx.Password;

        // About
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";
        PlatformText.Text = $"Windows {Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}";
        ArchitectureText.Text = RuntimeInformation.ProcessArchitecture.ToString();
        RuntimeText.Text = RuntimeInformation.FrameworkDescription;
    }

    // ── Save ────────────────────────────────────────────────────────────────

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath)
                ?? throw new InvalidOperationException("Cannot open registry key");

            // Graph
            SetReg(key, "GraphTenantId",    TenantIdTextBox.Text);
            SetReg(key, "GraphClientId",    ClientIdTextBox.Text);
            SetReg(key, "GraphClientSecret", ClientSecretBox.Password);

            // AzDO — NO PAT, SSO only
            SetReg(key, "DevOpsOrganization", AdoOrgTextBox.Text);
            SetReg(key, "DevOpsProject",      AdoProjectTextBox.Text);

            // Snipe
            SetReg(key, "SnipeUrl",    SnipeUrlTextBox.Text);
            SetReg(key, "SnipeApiKey", SnipeApiKeyBox.Password);

            // TDX
            SetReg(key, "TdxBaseUrl",  TdxUrlTextBox.Text);
            SetReg(key, "TdxUsername", TdxUsernameTextBox.Text);
            SetReg(key, "TdxPassword", TdxPasswordBox.Password);

            MessageBox.Show(
                "Settings saved. Restart FleetMate to apply changes.",
                "Settings Saved",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to save settings:\n{ex.Message}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static void SetReg(RegistryKey key, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            key.SetValue(name, value);
    }
}

