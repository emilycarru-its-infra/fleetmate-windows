using Microsoft.Win32;

namespace FleetMate.Core.Config;

/// <summary>
/// The values Settings edits — connections, Handbook, Manage and Terminal —
/// read from the effective configuration and saved to the user's registry key,
/// for the WPF and WinUI Settings pages alike. Nothing secret is ever written:
/// every connection signs in as the Windows user, and Save removes credentials
/// older builds left behind.
/// </summary>
public sealed record DesktopSettings
{
    // Connections
    public string GraphTenantId { get; init; } = "";
    public string GraphClientId { get; init; } = "";
    public string DevOpsOrganization { get; init; } = "";
    public string DevOpsProject { get; init; } = "";
    public string SnipeUrl { get; init; } = "";
    public string TdxBaseUrl { get; init; } = "";
    public string TdxAppId { get; init; } = "";
    public string ReportMateUrl { get; init; } = "";
    public string TeamsApiUrl { get; init; } = "";
    public string TeamsApiScope { get; init; } = "";
    public string TeamsDefaultTeam { get; init; } = "";
    public string TeamsDefaultChannels { get; init; } = "";

    // Handbook and skills
    public string HandbookRepoUrl { get; init; } = "";
    public string HandbookSiteUrl { get; init; } = "";
    public string AgentsHubRepoUrl { get; init; } = "";

    // Manage
    public string ManageRosterPath { get; init; } = "";
    public string ManageCommandsPath { get; init; } = "";
    public string SecureShellKeyPath { get; init; } = "";
    public string SecureShellUser { get; init; } = "";
    public string ManageTerminalProfile { get; init; } = "";
    public string ManageRdpUser { get; init; } = "";
    public bool ManageIncludeRetired { get; init; }
    public bool ManageIncludeProvisioning { get; init; }

    // Terminal
    public string AgentCommand { get; init; } = "";
    public bool AgentAutoStart { get; init; }
    public IReadOnlyList<string> Repos { get; init; } = Array.Empty<string>();

    /// <summary>What Settings shows for <paramref name="config"/>.</summary>
    public static DesktopSettings From(FleetMateConfig config)
    {
        var manage = config.Manage ?? new ManageConfig();
        var terminal = config.Terminal;
        return new DesktopSettings
        {
            GraphTenantId = config.Graph?.TenantId ?? "",
            GraphClientId = config.Graph?.ClientId ?? "",
            DevOpsOrganization = config.AzureDevOps?.Organization ?? "",
            DevOpsProject = config.AzureDevOps?.Project ?? "",
            SnipeUrl = config.SnipeUrl ?? "",
            TdxBaseUrl = config.Tdx?.BaseUrl ?? "",
            TdxAppId = config.Tdx?.AppId > 0 ? config.Tdx.AppId.ToString() : "",
            ReportMateUrl = config.ReportMateUrl ?? "",
            TeamsApiUrl = config.TeamsApiUrl ?? "",
            TeamsApiScope = config.TeamsApiScope ?? "",
            TeamsDefaultTeam = config.TeamsDefaultTeam ?? "",
            TeamsDefaultChannels = config.TeamsDefaultChannels ?? "",
            HandbookRepoUrl = config.HandbookRepoUrl ?? "",
            HandbookSiteUrl = config.HandbookSiteUrl ?? "",
            AgentsHubRepoUrl = config.AgentsHubRepoUrl ?? "",
            ManageRosterPath = manage.RosterPath,
            ManageCommandsPath = manage.CommandsPath,
            SecureShellKeyPath = manage.SshKeyPath,
            SecureShellUser = manage.SshUser,
            ManageTerminalProfile = manage.TerminalProfile,
            ManageRdpUser = manage.RdpUser,
            ManageIncludeRetired = manage.IncludeRetired,
            ManageIncludeProvisioning = manage.IncludeProvisioning,
            AgentCommand = terminal.AgentCommand,
            AgentAutoStart = terminal.AgentAutoStart,
            Repos = terminal.EffectiveRepos.ToList(),
        };
    }

    /// <summary>
    /// Write these values. Connection fields left empty keep what is there;
    /// Handbook, Teams and Manage fields left empty are removed so a managed
    /// value or the default applies again. The agent command, auto-start and
    /// repositories are written only when they differ from what
    /// <paramref name="terminal"/> would give without them, so a managed
    /// default keeps applying until someone chooses otherwise.
    /// </summary>
    public void Save(ISettingsSink sink, TerminalSettings terminal)
    {
        // Connections: no secret is written; each signs in as the Windows user.
        SetIfAny(sink, "GraphTenantId", GraphTenantId);
        SetIfAny(sink, "GraphClientId", GraphClientId);
        SetIfAny(sink, "DevOpsOrganization", DevOpsOrganization);
        SetIfAny(sink, "DevOpsProject", DevOpsProject);
        SetIfAny(sink, "SnipeUrl", SnipeUrl);
        SetIfAny(sink, "ReportMateUrl", ReportMateUrl);
        SetIfAny(sink, "TdxBaseUrl", TdxBaseUrl);
        SetIfAny(sink, "TdxAppId", TdxAppId);
        SetOrDelete(sink, "TeamsApiUrl", TeamsApiUrl);
        SetOrDelete(sink, "TeamsApiScope", TeamsApiScope);
        SetOrDelete(sink, "TeamsDefaultTeam", TeamsDefaultTeam);
        SetOrDelete(sink, "TeamsDefaultChannels", TeamsDefaultChannels);

        SetOrDelete(sink, "HandbookRepoUrl", HandbookRepoUrl);
        SetOrDelete(sink, "HandbookSiteUrl", HandbookSiteUrl);
        SetOrDelete(sink, "AgentsHubRepoUrl", AgentsHubRepoUrl);

        SetOrDelete(sink, "ManageRosterPath", ManageRosterPath);
        SetOrDelete(sink, "ManageCommandsPath", ManageCommandsPath);
        SetOrDelete(sink, "SecureShellKeyPath", SecureShellKeyPath);
        SetOrDelete(sink, "SecureShellUser", SecureShellUser);
        SetOrDelete(sink, "ManageTerminalProfile", ManageTerminalProfile);
        SetOrDelete(sink, "ManageRdpUser", ManageRdpUser);
        sink.Set("ManageIncludeRetired", ManageIncludeRetired ? "1" : "0");
        sink.Set("ManageIncludeProvisioning", ManageIncludeProvisioning ? "1" : "0");

        if (string.Equals(AgentCommand, terminal.AgentCommandFallback, StringComparison.OrdinalIgnoreCase))
            sink.Delete("AgentCommand");
        else
            sink.Set("AgentCommand", AgentCommand);
        if (AgentAutoStart == terminal.AgentAutoStartFallback) sink.Delete("AgentAutoStart");
        else sink.Set("AgentAutoStart", AgentAutoStart ? "1" : "0");
        if (Repos.Count == 0 || Repos.SequenceEqual(terminal.RepoDefaults, StringComparer.OrdinalIgnoreCase))
            sink.Delete("Repos");
        else
            sink.SetList("Repos", Repos);

        // Credentials older builds stored; never kept.
        foreach (var retired in RetiredSecrets) sink.Delete(retired);
    }

    /// <summary>Values that once held secrets and are removed on every save.</summary>
    public static readonly string[] RetiredSecrets =
    {
        "GraphClientSecret", "ReportMatePassphrase", "SnipeApiKey",
        "TdxUsername", "TdxPassword", "TdxBeid", "TdxWebServicesKey",
    };

    private static void SetIfAny(ISettingsSink sink, string name, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) sink.Set(name, value);
    }

    private static void SetOrDelete(ISettingsSink sink, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) sink.Delete(name);
        else sink.Set(name, value.Trim());
    }
}

/// <summary>Where Settings writes: the user's registry key, or a test double.</summary>
public interface ISettingsSink
{
    void Set(string name, string value);
    void SetList(string name, IReadOnlyList<string> values);
    void Delete(string name);
}

/// <summary>The edition's <c>HKCU\SOFTWARE\&lt;edition&gt;</c> key.</summary>
public sealed class RegistrySettingsSink : ISettingsSink, IDisposable
{
    private readonly RegistryKey _key;

    public RegistrySettingsSink(string? path = null) =>
        _key = Registry.CurrentUser.CreateSubKey(path ?? AppEdition.Current.UserRegistryPath)
            ?? throw new InvalidOperationException("Cannot open the settings registry key");

    public void Set(string name, string value) => _key.SetValue(name, value);

    public void SetList(string name, IReadOnlyList<string> values) =>
        _key.SetValue(name, values.ToArray(), RegistryValueKind.MultiString);

    public void Delete(string name) => _key.DeleteValue(name, throwOnMissingValue: false);

    public void Dispose() => _key.Dispose();
}
