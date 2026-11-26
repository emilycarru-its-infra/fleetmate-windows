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
                     GraphCard, DevOpsCard, SnipeCard, ReportMateCard, HandbookCard, TeamsCard,
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
        TeamsUrlTextBox.Text = config.TeamsApiUrl ?? "";
        TeamsScopeTextBox.Text = config.TeamsApiScope ?? "";
        TeamsDefaultTeamTextBox.Text = config.TeamsDefaultTeam ?? "";
        TeamsDefaultChannelsTextBox.Text = config.TeamsDefaultChannels ?? "";

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
        // The built-in default (Codex) shows as Shell where Codex is not installed, as sessions run it.
        var shown = terminal.AgentCommandIsBuiltInDefault && !installed(terminal.AgentCommand) ? "" : terminal.AgentCommand;
        var (pickerKey, customCommand) = FleetMate.Core.Config.AgentCommandPicker.FromSetting(shown, installed);
        AgentCustomCommandTextBox.Text = customCommand;
        AgentCommandPicker.SelectedValue = pickerKey;
        AgentAutoStartCheckBox.IsChecked = terminal.AgentAutoStart;
        TerminalRestoreSessionsCheckBox.IsChecked = UserPreferences.TerminalRestoreSessions;
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
        TicketBadgesCheckBox.IsChecked = UserPreferences.TicketsBadgesEnabled(config.TicketsBadgesEnabled);
        LoadPreferences(config);
        _isLoadingSettings = false;
    }

    private void OnTicketBadgesChanged(object sender, RoutedEventArgs e)
    {
        if (!_isLoadingSettings) UserPreferences.SetTicketsBadgesEnabled(TicketBadgesCheckBox.IsChecked == true);
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

        UserPreferences.SetUiTheme(theme);
    }

    // ── Save ────────────────────────────────────────────────────────────────

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = new DesktopSettings
            {
                GraphTenantId = TenantIdTextBox.Text,
                GraphClientId = ClientIdTextBox.Text,
                DevOpsOrganization = AdoOrgTextBox.Text,
                DevOpsProject = AdoProjectTextBox.Text,
                SnipeUrl = SnipeUrlTextBox.Text,
                TdxBaseUrl = TdxUrlTextBox.Text,
                TdxAppId = TdxAppIdTextBox.Text,
                ReportMateUrl = ReportMateUrlTextBox.Text,
                TeamsApiUrl = TeamsUrlTextBox.Text,
                TeamsApiScope = TeamsScopeTextBox.Text,
                TeamsDefaultTeam = TeamsDefaultTeamTextBox.Text,
                TeamsDefaultChannels = TeamsDefaultChannelsTextBox.Text,
                HandbookRepoUrl = HandbookRepoUrlTextBox.Text,
                HandbookSiteUrl = HandbookSiteUrlTextBox.Text,
                AgentsHubRepoUrl = AgentsHubRepoUrlTextBox.Text,
                ManageRosterPath = ManageRosterPathTextBox.Text,
                ManageCommandsPath = ManageCommandsPathTextBox.Text,
                SecureShellKeyPath = SecureShellKeyPathTextBox.Text,
                SecureShellUser = SecureShellUserTextBox.Text,
                ManageTerminalProfile = ManageTerminalProfileTextBox.Text,
                ManageRdpUser = ManageRdpUserTextBox.Text,
                ManageIncludeRetired = ManageIncludeRetiredCheckBox.IsChecked == true,
                ManageIncludeProvisioning = ManageIncludeProvisioningCheckBox.IsChecked == true,
                AgentCommand = FleetMate.Core.Config.AgentCommandPicker.ToSetting(
                    AgentCommandPicker.SelectedValue as string ?? "", AgentCustomCommandTextBox.Text),
                AgentAutoStart = AgentAutoStartCheckBox.IsChecked == true,
                Repos = FleetMate.Core.Config.TerminalSettings.ParseList(ReposTextBox.Text),
            };
            // The rules for what is written and what is left to policy are
            // DesktopSettings', shared with the WinUI Settings page.
            using (var sink = new RegistrySettingsSink(RegistryPath))
                settings.Save(sink, _terminal ?? new TerminalSettings { RepoDefaults = _repoDefaults.ToList() });

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

    /// <summary>Saved at once, like the other per-person terminal preferences.</summary>
    private void OnTerminalRestoreSessionsClicked(object sender, RoutedEventArgs e) =>
        UserPreferences.SetTerminalRestoreSessions(TerminalRestoreSessionsCheckBox.IsChecked == true);
}
