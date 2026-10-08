using Microsoft.Win32;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FleetMate.Core.Config;
using FleetMate.Core.Models;
using ModernWpf;

namespace FleetMate.GUI.Views.Shared;

public partial class SettingsPage : Page
{
    private const string RegistryPath = @"SOFTWARE\FleetMate";
    private bool _isLoadingSettings;
    private IReadOnlyList<string> _repoDefaults = Array.Empty<string>();
    private FleetMate.Core.Config.TerminalSettings? _terminal;

    private void OnAgentCommandPickerChanged(object sender, SelectionChangedEventArgs e) =>
        AgentCustomCommandTextBox.Visibility = AgentCommandPicker.SelectedValue as string == FleetMate.Core.Config.AgentCommandPicker.Custom
            ? Visibility.Visible : Visibility.Collapsed;

    private static void ShowPolicySource(TextBlock text, bool fromPolicy)
    {
        text.Text = "Set by your organization. Changing it here overrides that for you.";
        text.Visibility = fromPolicy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnResetReposClicked(object sender, RoutedEventArgs e) =>
        ReposTextBox.Text = string.Join(Environment.NewLine, _repoDefaults);

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            LoadSettings();
            _ = RefreshAuthCardsAsync();
            UserPreferences.Changed += OnPreferencesChanged;
        };
        Unloaded += (_, _) => UserPreferences.Changed -= OnPreferencesChanged;
    }

    // ── Load ────────────────────────────────────────────────────────────────

    private void LoadSettings()
    {
        _isLoadingSettings = true;
        var config = Application.Current is App app ? app.Config : FleetMateConfig.Load();

        // Config file path
        ConfigPathTextBox.Text = @"HKCU\SOFTWARE\FleetMate";

        // Microsoft Graph — tenant and client ID only; there is no secret to enter.
        TenantIdTextBox.Text  = config.Graph?.TenantId  ?? "";
        ClientIdTextBox.Text  = config.Graph?.ClientId  ?? "";

        // Azure DevOps
        AdoOrgTextBox.Text     = config.AzureDevOps?.Organization ?? "";
        AdoProjectTextBox.Text = config.AzureDevOps?.Project      ?? "";
        // NO PAT — Azure DevOps uses SSO only (browser OAuth2 PKCE or Azure CLI)

        // Snipe-IT — auth is the operator's Entra session; no key to enter.
        SnipeUrlTextBox.Text = config.SnipeUrl ?? "";

        // TDX — SSO only; there is no username or password to enter.
        TdxUrlTextBox.Text = config.Tdx?.BaseUrl ?? "";
        TdxAppIdTextBox.Text = config.Tdx?.AppId > 0 ? config.Tdx.AppId.ToString() : "";

        ReportMateUrlTextBox.Text = config.ReportMateUrl ?? "";

        HandbookRepoUrlTextBox.Text = config.HandbookRepoUrl ?? "";
        HandbookSiteUrlTextBox.Text = config.HandbookSiteUrl ?? "";
        AgentsHubRepoUrlTextBox.Text = config.AgentsHubRepoUrl ?? "";

        // Manage tab
        var manage = config.Manage ?? new ManageConfig();
        ManageRosterPathTextBox.Text = manage.RosterPath;
        ManageCommandsPathTextBox.Text = manage.CommandsPath;
        SecureShellKeyPathTextBox.Text = manage.SshKeyPath;
        SecureShellUserTextBox.Text = manage.SshUser;
        ManageTerminalProfileTextBox.Text = manage.TerminalProfile;
        ManageRdpUserTextBox.Text = manage.RdpUser;
        ManageIncludeRetiredCheckBox.IsChecked = manage.IncludeRetired;
        ManageIncludeProvisioningCheckBox.IsChecked = manage.IncludeProvisioning;

        // Terminal. Repos starts from the managed defaults until the operator saves their own.
        var terminal = config.Terminal;
        _terminal = terminal;
        AgentCommandPicker.ItemsSource = FleetMate.Core.Config.AgentCommandPicker.Choices
            .Select(c => new { c.Key, c.Label }).ToList();
        var (pickerKey, customCommand) = FleetMate.Core.Config.AgentCommandPicker.FromSetting(terminal.AgentCommand);
        AgentCustomCommandTextBox.Text = customCommand;
        AgentCommandPicker.SelectedValue = pickerKey;
        AgentAutoStartCheckBox.IsChecked = terminal.AgentAutoStart;
        ShowPolicySource(AgentCommandSourceText, terminal.AgentCommandFromPolicy);
        ShowPolicySource(AgentAutoStartSourceText, terminal.AgentAutoStartFromPolicy);
        ReposTextBox.Text = string.Join(Environment.NewLine, terminal.EffectiveRepos);
        _repoDefaults = terminal.RepoDefaults;
        RepoDefaultsText.Text = terminal.RepoDefaults.Count > 0
            ? $"Your organization's defaults: {terminal.RepoDefaults.Count} repo(s)."
            : "No default repos are set by policy.";
        RefreshRdpCredentialStatus();
        SshKeyStatusText.Text = manage.HasSshKey
            ? $"Key found at {manage.ResolvedSshKeyPath}. Sessions connect as {manage.ResolvedSshUser}."
            : $"No key at {manage.ResolvedSshKeyPath}. Machine details and command runs need the fleet admin key; sessions and scanning still work without it.";

        // About
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";
        PlatformText.Text = $"Windows {Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}";
        ArchitectureText.Text = RuntimeInformation.ProcessArchitecture.ToString();
        RuntimeText.Text = RuntimeInformation.FrameworkDescription;

        using var appearanceKey = Registry.CurrentUser.OpenSubKey(RegistryPath);
        var theme = appearanceKey?.GetValue("UiTheme")?.ToString() ?? "System";
        ThemeComboBox.SelectedIndex = theme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0
        };
        LoadPreferences(config);
        _isLoadingSettings = false;
    }

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings || ThemeComboBox.SelectedItem is not ComboBoxItem item)
            return;

        var theme = item.Tag?.ToString() ?? "System";
        ThemeManager.Current.ApplicationTheme = theme switch
        {
            "Light" => ApplicationTheme.Light,
            "Dark" => ApplicationTheme.Dark,
            _ => null
        };

        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
        key?.SetValue("UiTheme", theme);
    }

    // ── Save ────────────────────────────────────────────────────────────────

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath)
                ?? throw new InvalidOperationException("Cannot open registry key");

            // Graph — no secret is written; Graph authenticates via the broker.
            SetReg(key, "GraphTenantId",    TenantIdTextBox.Text);
            SetReg(key, "GraphClientId",    ClientIdTextBox.Text);
            key.DeleteValue("GraphClientSecret", throwOnMissingValue: false);

            // AzDO — NO PAT, SSO only
            SetReg(key, "DevOpsOrganization", AdoOrgTextBox.Text);
            SetReg(key, "DevOpsProject",      AdoProjectTextBox.Text);

            // Snipe — no API key is written; auth is the operator's Entra session.
            SetReg(key, "SnipeUrl", SnipeUrlTextBox.Text);

            SetReg(key, "ReportMateUrl", ReportMateUrlTextBox.Text);
            key.DeleteValue("ReportMatePassphrase", throwOnMissingValue: false);

            // Handbook and skills. Empty values are removed so a managed value applies.
            SetOrDeleteReg(key, "HandbookRepoUrl", HandbookRepoUrlTextBox.Text);
            SetOrDeleteReg(key, "HandbookSiteUrl", HandbookSiteUrlTextBox.Text);
            SetOrDeleteReg(key, "AgentsHubRepoUrl", AgentsHubRepoUrlTextBox.Text);

            // Manage tab. Empty values are removed so the defaults apply again.
            SetOrDeleteReg(key, "ManageRosterPath", ManageRosterPathTextBox.Text);
            SetOrDeleteReg(key, "ManageCommandsPath", ManageCommandsPathTextBox.Text);
            SetOrDeleteReg(key, "SecureShellKeyPath", SecureShellKeyPathTextBox.Text);
            SetOrDeleteReg(key, "SecureShellUser", SecureShellUserTextBox.Text);
            SetOrDeleteReg(key, "ManageTerminalProfile", ManageTerminalProfileTextBox.Text);
            SetOrDeleteReg(key, "ManageRdpUser", ManageRdpUserTextBox.Text);
            key.SetValue("ManageIncludeRetired", ManageIncludeRetiredCheckBox.IsChecked == true ? "1" : "0");
            key.SetValue("ManageIncludeProvisioning", ManageIncludeProvisioningCheckBox.IsChecked == true ? "1" : "0");
            key.DeleteValue("SnipeApiKey", throwOnMissingValue: false);

            // Terminal. An empty repo list is removed so the managed defaults seed it again.
            // Your own value is written only when it differs from what you
            // would otherwise get (policy, then the default), so a managed
            // default keeps applying until you choose something else.
            var agentCommand = FleetMate.Core.Config.AgentCommandPicker.ToSetting(
                AgentCommandPicker.SelectedValue as string ?? "", AgentCustomCommandTextBox.Text);
            if (_terminal != null && string.Equals(agentCommand, _terminal.AgentCommandFallback, StringComparison.OrdinalIgnoreCase))
                key.DeleteValue("AgentCommand", throwOnMissingValue: false);
            else
                key.SetValue("AgentCommand", agentCommand);
            var autoStart = AgentAutoStartCheckBox.IsChecked == true;
            if (_terminal != null && autoStart == _terminal.AgentAutoStartFallback)
                key.DeleteValue("AgentAutoStart", throwOnMissingValue: false);
            else
                key.SetValue("AgentAutoStart", autoStart ? "1" : "0");
            var repos = FleetMate.Core.Config.TerminalSettings.ParseList(ReposTextBox.Text);
            if (repos.Count == 0 || repos.SequenceEqual(_repoDefaults, StringComparer.OrdinalIgnoreCase))
                key.DeleteValue("Repos", throwOnMissingValue: false);
            else
                key.SetValue("Repos", repos.ToArray(), RegistryValueKind.MultiString);

            // TDX
            // TDX — SSO only. Clear any service-account credential left behind by
            // an older build rather than leaving a live secret in the registry.
            SetReg(key, "TdxBaseUrl", TdxUrlTextBox.Text);
            SetReg(key, "TdxAppId", TdxAppIdTextBox.Text);
            foreach (var retired in new[] { "TdxUsername", "TdxPassword", "TdxBeid", "TdxWebServicesKey" })
                key.DeleteValue(retired, throwOnMissingValue: false);

            if (Application.Current is App app)
            {
                app.ReloadConfiguration();
                BuildAuthCards();
            }

            MessageBox.Show(
                "Settings saved and applied.",
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

    private static void SetOrDeleteReg(RegistryKey key, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, value.Trim());
    }

    // ── Remote Desktop credential (DPAPI, never in the registry) ────────────

    private void RefreshRdpCredentialStatus()
    {
        var store = new FleetMate.Core.Services.Manage.RdpCredentialStore();
        RdpCredentialStatusText.Text = store.HasCredential
            ? "A password is stored, encrypted for your Windows account. Remote Desktop opens without a prompt."
            : "No password stored. Remote Desktop will prompt for the account password each time.";
        ClearRdpPasswordButton.IsEnabled = store.HasCredential;
    }

    private void OnSaveRdpPassword(object sender, RoutedEventArgs e)
    {
        var password = RdpPasswordBox.Password;
        if (string.IsNullOrEmpty(password)) return;
        new FleetMate.Core.Services.Manage.RdpCredentialStore().Save(password);
        RdpPasswordBox.Clear();
        RefreshRdpCredentialStatus();
    }

    private void OnClearRdpPassword(object sender, RoutedEventArgs e)
    {
        new FleetMate.Core.Services.Manage.RdpCredentialStore().Delete();
        RdpPasswordBox.Clear();
        RefreshRdpCredentialStatus();
    }

    // ── Manage tab file pickers ─────────────────────────────────────────────

    private void OnBrowseRoster(object sender, RoutedEventArgs e) =>
        PickFile(ManageRosterPathTextBox, "Roster CSV|*.csv|All files|*.*", "Choose the enrollment roster");

    private void OnBrowseCommands(object sender, RoutedEventArgs e) =>
        PickFile(ManageCommandsPathTextBox, "YAML|*.yaml;*.yml|All files|*.*", "Choose the command library");

    private void OnBrowseSshKey(object sender, RoutedEventArgs e) =>
        PickFile(SecureShellKeyPathTextBox, "All files|*.*", "Choose the SSH private key");

    private static void PickFile(TextBox target, string filter, string title)
    {
        var dialog = new OpenFileDialog { Filter = filter, Title = title, CheckFileExists = true };
        var current = ManageConfig.ExpandHome(target.Text);
        if (!string.IsNullOrWhiteSpace(current))
        {
            var dir = System.IO.Path.GetDirectoryName(current);
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir)) dialog.InitialDirectory = dir;
        }
        if (dialog.ShowDialog() == true) target.Text = dialog.FileName;
    }

    // ── Auth Status Cards ───────────────────────────────────────────────────

    private void BuildAuthCards()
    {
        AuthCardsPanel.Children.Clear();

        if (Application.Current is not App app) return;
        var config = app.Config;

        var graphConfigured = config.Graph != null && !string.IsNullOrEmpty(config.Graph.TenantId);
        if (graphConfigured)
        {
            var (text, state) = BrokerState(app, AuthSystemId.Graph);
            AddAuthCard(AuthSystemId.Graph, "Microsoft Graph", "Entra SSO · Windows Web Account Manager", text, state,
                new (string label, string? value)[]
                {
                    ("Tenant ID", ShortId(config.Graph?.TenantId)),
                    ("Client ID", ShortId(config.Graph?.ClientId)),
                    ("Token source", "Windows broker (WAM / device PRT)"),
                    ("Elevation", "managed identity (aze)")
                });
        }

        var adoConfigured = config.AzureDevOps != null && !string.IsNullOrEmpty(config.AzureDevOps.Organization);
        if (adoConfigured)
        {
            var (text, state) = BrokerState(app, AuthSystemId.DevOps);
            AddAuthCard(AuthSystemId.DevOps, "Azure DevOps", "Entra SSO · Windows Web Account Manager", text, state,
                new[]
                {
                    ("Organization", config.AzureDevOps?.Organization),
                    ("Project", config.AzureDevOps?.Project),
                    ("Token source", "Windows broker (operator identity)")
                });
        }

        var tdxConfigured = config.Tdx != null && !string.IsNullOrEmpty(config.Tdx.BaseUrl);
        if (tdxConfigured)
        {
            var (text, state) = BrokerState(app, AuthSystemId.Tdx);
            AddAuthCard(AuthSystemId.Tdx, "TeamDynamix", "Integrated SSO · Entra / Shibboleth", text, state,
                new[]
                {
                    ("Base URL", config.Tdx?.BaseUrl),
                    ("Token source", "silent Windows SSO (operator identity)")
                });
        }

        var snipeConfigured = !string.IsNullOrEmpty(config.SnipeUrl);
        if (snipeConfigured)
        {
            var (text, state) = BrokerState(app, AuthSystemId.Snipe);
            AddAuthCard(AuthSystemId.Snipe, "Snipe-IT", "Entra SSO · brokered bearer", text, state,
                new[]
                {
                    ("Instance URL", config.SnipeUrl),
                    ("Audience", ShortId(config.SnipeOidcAudience)),
                    ("Token source", "Windows broker — no API key")
                });
        }

        var rmConfigured = !string.IsNullOrEmpty(config.ReportMateUrl);
        if (rmConfigured)
        {
            AddAuthCard(null, "ReportMate", "Entra SSO · brokered bearer", "ready for SSO", AuthState.Configured,
                new[]
                {
                    ("API URL", config.ReportMateUrl),
                    ("Audience", ShortId(config.ReportMateOidcAudience)),
                    ("Token source", "Windows broker — no passphrase")
                });
        }

        var ghConfig = config.Tasks?.Providers?.GitHub;
        var ghState = app.AuthManager.Systems.GetValueOrDefault(AuthSystemId.GitHub)?.State;
        if (ghConfig is { Enabled: true } || ghState?.Kind == AuthStateKind.Valid)
        {
            var (text, state) = BrokerState(app, AuthSystemId.GitHub);
            AddAuthCard(AuthSystemId.GitHub, "GitHub", "GitHub CLI · OS credential store", text, state,
                new[]
                {
                    ("Organization", ghConfig?.Organization),
                    ("Project #", ghConfig?.ProjectNumber?.ToString()),
                    ("Token source", "gh authenticated session — no config token")
                });
        }

        if (AuthCardsPanel.Children.Count == 0)
        {
            AuthCardsPanel.Children.Add(new TextBlock
            {
                Text = "Add an endpoint above, then save. FleetMate will acquire the operator's session from Windows automatically.",
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(12),
                Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush")
            });
        }
    }

    private static (string text, AuthState state) BrokerState(App app, AuthSystemId id)
    {
        var status = app.AuthManager.Systems.GetValueOrDefault(id);
        if (status == null) return ("needs setup", AuthState.NotConfigured);
        return status.State.Kind switch
        {
            AuthStateKind.Valid => ($"signed in as {status.State.User ?? status.User ?? "you"}", AuthState.Valid),
            AuthStateKind.Authenticating => ("checking Windows session…", AuthState.Configured),
            AuthStateKind.Failed => ("SSO unavailable", AuthState.Failed),
            AuthStateKind.ServicePrincipal => ("service principal blocked", AuthState.Failed),
            _ => ("ready for SSO", AuthState.Configured)
        };
    }

    private void AddAuthCard(AuthSystemId? systemId, string systemName, string authMethod, string statusText, AuthState state,
        (string label, string? value)[] details)
    {
        // Never red: a failed sign-in is orange, as on the macOS client.
        var color = state switch
        {
            AuthState.Valid => "#27ae60",
            AuthState.Configured => "#f39c12",
            AuthState.Failed => "#e67e22",
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

        // Sign-in actions: Sign Out / Retry SSO, az login, gh auth login, Re-check.
        if (systemId is { } id)
        {
            var actions = BuildAuthActions(id, state);
            Grid.SetColumn(actions, 2);
            header.Children.Add(actions);
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
                val.Foreground = new SolidColorBrush(Colors.DarkOrange);
            else if (value.StartsWith("●"))
                val.Foreground = new SolidColorBrush(Colors.Green);
            Grid.SetColumn(val, 1);

            row.Children.Add(lbl);
            row.Children.Add(val);
            outerStack.Children.Add(row);
        }

        // What the last az or gh sign-in from this card reported.
        if (systemId is { } resultId && _signInResults.TryGetValue(resultId, out var outcome))
        {
            outerStack.Children.Add(new TextBlock
            {
                Text = outcome.Message,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
                Foreground = outcome.Succeeded
                    ? (Brush)FindResource("SystemControlForegroundBaseMediumBrush")
                    : new SolidColorBrush(Colors.DarkOrange)
            });
        }

        card.Child = outerStack;
        AuthCardsPanel.Children.Add(card);
    }

    private async void OnRefreshAuthClicked(object sender, RoutedEventArgs e)
    {
        await RefreshAuthCardsAsync();
    }

    private async Task RefreshAuthCardsAsync()
    {
        if (Application.Current is not App app) return;
        try
        {
            await app.AuthManager.ProbeAllAsync(
                app.GraphService, app.TdxService, app.SnipeService, app.DevOpsService);
        }
        catch
        {
            // Individual probes own their error states; keep rendering the rest.
        }
        BuildAuthCards();
    }

    // ── Auth Helpers ────────────────────────────────────────────────────────

    private enum AuthState { Valid, Configured, Failed, NotConfigured }

    private static string TdxAuthDescription(FleetMateConfig config, bool ssoActive)
        => ssoActive ? "SSO — signed in" : "SSO (Entra ID / Shibboleth)";

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

