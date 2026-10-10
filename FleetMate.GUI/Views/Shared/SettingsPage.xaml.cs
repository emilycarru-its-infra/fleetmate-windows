using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FleetMate.Core.Config;
using FleetMate.Core.Models;
using FleetMate.Core.Services.Terminal;
using ModernWpf;

namespace FleetMate.GUI.Views.Shared;

public partial class SettingsPage : Page
{
    private static string RegistryPath => FleetMate.Core.Config.AppEdition.Current.UserRegistryPath;
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
        // The edition's own name: TicketsMate must not read "Configure FleetMate".
        var name = AppEdition.Current.Name;
        SettingsSubtitleText.Text = $"Configure {name}";
        GeneralSubtitleText.Text = AppEdition.Current.IsTicketsOnly
            ? "Setup and configuration storage."
            : $"The modules {name} shows, setup and configuration storage.";
        AppearanceSubtitleText.Text = $"Choose how {name} looks on this PC.";
        if (AppEdition.Current.IsTicketsOnly) ApplyTicketsOnly();
        Loaded += (_, _) =>
        {
            LoadSettings();
            AttachAuthUpdates();
            _ = RefreshAuthCardsAsync();
            UserPreferences.Changed += OnPreferencesChanged;
        };
        Unloaded += (_, _) =>
        {
            UserPreferences.Changed -= OnPreferencesChanged;
            DetachAuthUpdates();
        };
    }

    /// <summary>
    /// TicketsMate's settings: no module switches, no Manage, Enrollment or
    /// Terminal tabs, and TeamDynamix alone under Authentication.
    /// </summary>
    private void ApplyTicketsOnly()
    {
        foreach (var element in new UIElement[]
                 {
                     ModulesCard, ManageTab, EnrollmentTab, TerminalTab,
                     GraphCard, DevOpsCard, SnipeCard, ReportMateCard, HandbookCard,
                 })
            element.Visibility = Visibility.Collapsed;
        Grid.SetColumn(TdxCard, 0);
        Grid.SetColumnSpan(TdxCard, 2);
        TdxCard.Margin = new Thickness(0, 0, 0, 10);
    }

    // ── Load ────────────────────────────────────────────────────────────────

    private void LoadSettings()
    {
        _isLoadingSettings = true;
        var config = Application.Current is App app ? app.Config : FleetMateConfig.Load();

        // Config file path
        ConfigPathTextBox.Text = $@"HKCU\{AppEdition.Current.UserRegistryPath}";

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
        Func<string, bool> installed = key => AgentCommands.IsInstalled(key, AgentCommands.FindInstalled);
        AgentCommandPicker.ItemsSource = FleetMate.Core.Config.AgentCommandPicker.InstalledChoices(installed)
            .Select(c => new { c.Key, c.Label }).ToList();
        var (pickerKey, customCommand) = FleetMate.Core.Config.AgentCommandPicker.FromSetting(terminal.AgentCommand, installed);
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

        BuildAboutPanel();

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

    private static string ShortId(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var parts = s.Split('-');
        if (parts.Length >= 2)
            return $"{parts[0]}-{parts[1]}...";
        return s.Length > 14 ? $"{s[..14]}..." : s;
    }

}

