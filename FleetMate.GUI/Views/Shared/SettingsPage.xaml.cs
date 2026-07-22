using Microsoft.Win32;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FleetMate.Core.Config;
using FleetMate.Core.Services;

namespace FleetMate.GUI.Views.Shared;

public partial class SettingsPage : Page
{
    private const string RegistryPath = @"SOFTWARE\FleetMate";

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            LoadSettings();
            BuildAuthCards();
        };
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

    // ── Auth Status Cards ───────────────────────────────────────────────────

    private void BuildAuthCards()
    {
        AuthCardsPanel.Children.Clear();

        if (Application.Current is not App app) return;
        var config = app.Config;

        // Microsoft Graph (Intune / Entra)
        var graphConfigured = config.Graph != null && !string.IsNullOrEmpty(config.Graph.TenantId);
        var graphHasSecret = !string.IsNullOrEmpty(config.Graph?.ClientSecret);
        AddAuthCard("Microsoft Graph", "Service Principal (client credentials)",
            graphConfigured ? (graphHasSecret ? "configured" : "missing secret") : "not configured",
            graphConfigured && graphHasSecret ? AuthState.Configured : AuthState.NotConfigured,
            new[]
            {
                ("Tenant ID", ShortId(config.Graph?.TenantId)),
                ("Client ID", ShortId(config.Graph?.ClientId)),
                ("Client Secret", graphHasSecret ? "● configured" : "✗ missing"),
                ("Devices SP", ShortId(config.Graph?.DevicesClientId)),
                ("Systems SP", ShortId(config.Graph?.SystemsClientId))
            });

        // Azure DevOps
        var adoConfigured = config.AzureDevOps != null && !string.IsNullOrEmpty(config.AzureDevOps.Organization);
        var adoSso = app.IsDevOpsSsoAuthenticated;
        AddAuthCard("Azure DevOps", "Platform SSO (OAuth2 PKCE)",
            adoSso ? $"signed in as {app.DevOpsAuthenticatedUserName}" : (adoConfigured ? "not signed in" : "not configured"),
            adoSso ? AuthState.Valid : (adoConfigured ? AuthState.Configured : AuthState.NotConfigured),
            new[]
            {
                ("Organization", config.AzureDevOps?.Organization),
                ("Project", config.AzureDevOps?.Project),
                ("User", adoSso ? app.DevOpsAuthenticatedUserName : null)
            },
            adoConfigured ? (adoSso ? "Sign Out" : "Sign In") : null,
            () =>
            {
                if (adoSso) app.AuthManager.SignOut();
                else _ = app.AttemptSilentDevOpsSsoAsync();
                BuildAuthCards();
            });

        // TeamDynamix
        var tdxConfigured = config.Tdx != null && !string.IsNullOrEmpty(config.Tdx.BaseUrl);
        var tdxSso = app.IsTdxSsoAuthenticated;
        AddAuthCard("TeamDynamix", TdxAuthDescription(config, tdxSso),
            tdxSso ? $"signed in as {app.TdxAuthenticatedUserName}" : (tdxConfigured ? "not signed in" : "not configured"),
            tdxSso ? AuthState.Valid : (tdxConfigured ? AuthState.Configured : AuthState.NotConfigured),
            new[]
            {
                ("Base URL", config.Tdx?.BaseUrl),
                ("Auth Method", TdxAuthDescription(config, tdxSso)),
                ("User", tdxSso ? app.TdxAuthenticatedUserName : null)
            },
            tdxConfigured ? (tdxSso ? "Sign Out" : "Sign In") : null,
            () =>
            {
                if (tdxSso) app.SignOutTdxSso();
                else app.ShowTdxSsoLogin(_ => Dispatcher.Invoke(BuildAuthCards));
                BuildAuthCards();
            });

        // Snipe-IT
        var snipeConfigured = !string.IsNullOrEmpty(config.SnipeUrl) && !string.IsNullOrEmpty(config.SnipeApiKey);
        AddAuthCard("Snipe-IT", "API key (Bearer token)",
            snipeConfigured ? "configured" : "not configured",
            snipeConfigured ? AuthState.Configured : AuthState.NotConfigured,
            new[]
            {
                ("Instance URL", config.SnipeUrl),
                ("API Key", !string.IsNullOrEmpty(config.SnipeApiKey) ? MaskedToken(config.SnipeApiKey) : "✗ missing")
            });

        // GitHub
        var ghConfig = config.Tasks?.Providers?.GitHub;
        var ghConfigured = ghConfig != null && ghConfig.Enabled;
        AddAuthCard("GitHub", "gh CLI (device/browser flow)",
            ghConfigured ? "configured" : "not configured",
            ghConfigured ? AuthState.Configured : AuthState.NotConfigured,
            new[]
            {
                ("Organization", ghConfig?.Organization),
                ("Project #", ghConfig?.ProjectNumber?.ToString())
            });

        // Gitea
        var giteaConfig = config.Tasks?.Providers?.Gitea;
        var giteaConfigured = giteaConfig != null && giteaConfig.Enabled;
        AddAuthCard("Gitea", "API token",
            giteaConfigured ? "configured" : "not configured",
            giteaConfigured ? AuthState.Configured : AuthState.NotConfigured,
            new[]
            {
                ("Instance URL", giteaConfig?.Url),
                ("Owner", giteaConfig?.Owner),
                ("Token", !string.IsNullOrEmpty(giteaConfig?.Token) ? MaskedToken(giteaConfig.Token) : "✗ missing")
            });
    }

    private void AddAuthCard(string systemName, string authMethod, string statusText, AuthState state,
        (string label, string? value)[] details, string? actionLabel = null, Action? action = null)
    {
        var color = state switch
        {
            AuthState.Valid => "#27ae60",
            AuthState.Configured => "#f39c12",
            _ => "#666"
        };
        var borderColor = Color.FromArgb(50,
            ((Color)ColorConverter.ConvertFromString(color)).R,
            ((Color)ColorConverter.ConvertFromString(color)).G,
            ((Color)ColorConverter.ConvertFromString(color)).B);

        var card = new Border
        {
            Style = (Style)FindResource("CardStyle"),
            Margin = new Thickness(0, 0, 0, 12),
            BorderBrush = new SolidColorBrush(borderColor),
            BorderThickness = new Thickness(1)
        };

        var outerStack = new StackPanel();

        // Header: System name + status badge + action button
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameBlock = new TextBlock
        {
            Text = systemName,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(nameBlock, 0);
        header.Children.Add(nameBlock);

        // Status badge
        var badge = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        badge.Child = new TextBlock
        {
            Text = statusText,
            FontSize = 11,
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold
        };
        Grid.SetColumn(badge, 1);
        header.Children.Add(badge);

        // Action button
        if (actionLabel != null && action != null)
        {
            var btn = new Button
            {
                Content = actionLabel,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(12, 4, 12, 4)
            };
            btn.Click += (_, _) => action();
            Grid.SetColumn(btn, 2);
            header.Children.Add(btn);
        }

        outerStack.Children.Add(header);

        // Auth method
        outerStack.Children.Add(new TextBlock
        {
            Text = authMethod,
            FontSize = 11,
            Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
            Margin = new Thickness(0, 4, 0, 4)
        });

        // Detail rows
        foreach (var (label, value) in details)
        {
            if (string.IsNullOrEmpty(value)) continue;

            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var lbl = new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush")
            };
            Grid.SetColumn(lbl, 0);

            var val = new TextBlock
            {
                Text = value,
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            if (value.StartsWith("✗"))
                val.Foreground = new SolidColorBrush(Colors.Red);
            else if (value.StartsWith("●"))
                val.Foreground = new SolidColorBrush(Colors.Green);
            Grid.SetColumn(val, 1);

            row.Children.Add(lbl);
            row.Children.Add(val);
            outerStack.Children.Add(row);
        }

        card.Child = outerStack;
        AuthCardsPanel.Children.Add(card);
    }

    private void OnRefreshAuthClicked(object sender, RoutedEventArgs e)
    {
        BuildAuthCards();
    }

    // ── Auth Helpers ────────────────────────────────────────────────────────

    private enum AuthState { Valid, Configured, NotConfigured }

    private static string TdxAuthDescription(FleetMateConfig config, bool ssoActive)
    {
        if (ssoActive) return "Browser SSO (active)";
        if (config.Tdx?.Beid != null) return "Service account + SSO available";
        if (!string.IsNullOrEmpty(config.Tdx?.Username)) return "Username / password";
        return "Browser SSO (Entra ID / Shibboleth)";
    }

    private static string ShortId(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var parts = s.Split('-');
        if (parts.Length >= 2)
            return $"{parts[0]}-{parts[1]}...";
        return s.Length > 14 ? $"{s[..14]}..." : s;
    }

    private static string MaskedToken(string s)
    {
        if (s.Length <= 16) return new string('●', Math.Min(s.Length, 8));
        return $"{s[..6]}...{s[^6..]}";
    }
}

