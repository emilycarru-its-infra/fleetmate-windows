namespace FleetMate.Core.Config;

/// <summary>One editable text value in Settings, with the card it sits in and how to read and change it.</summary>
public sealed record DesktopSettingField(
    string Card,
    string Key,
    string Label,
    string Placeholder,
    Func<DesktopSettings, string> Get,
    Func<DesktopSettings, string, DesktopSettings> Set,
    bool Multiline = false);

/// <summary>One editable switch in Settings.</summary>
public sealed record DesktopSettingToggle(
    string Card,
    string Key,
    string Label,
    Func<DesktopSettings, bool> Get,
    Func<DesktopSettings, bool, DesktopSettings> Set);

/// <summary>
/// The fields each Settings section edits, in order, so a page can draw them
/// without its own copy of the list. Each section's cards are named after the
/// system they configure; no secret is ever asked for.
/// </summary>
public static class DesktopSettingsFields
{
    public static readonly IReadOnlyList<DesktopSettingField> Connections = new DesktopSettingField[]
    {
        new("Microsoft Graph", "GraphTenantId", "Tenant ID", "00000000-0000-0000-0000-000000000000", s => s.GraphTenantId, (s, v) => s with { GraphTenantId = v }),
        new("Microsoft Graph", "GraphClientId", "Client ID", "Application (client) ID", s => s.GraphClientId, (s, v) => s with { GraphClientId = v }),
        new("Azure DevOps", "DevOpsOrganization", "Organization", "organization name", s => s.DevOpsOrganization, (s, v) => s with { DevOpsOrganization = v }),
        new("Azure DevOps", "DevOpsProject", "Project", "default project", s => s.DevOpsProject, (s, v) => s with { DevOpsProject = v }),
        new("TeamDynamix", "TdxBaseUrl", "Base URL", "https://service.example.edu", s => s.TdxBaseUrl, (s, v) => s with { TdxBaseUrl = v }),
        new("TeamDynamix", "TdxAppId", "Ticketing app ID", "number", s => s.TdxAppId, (s, v) => s with { TdxAppId = v }),
        new("Inventory", "SnipeUrl", "Snipe-IT URL", "https://assets.example.edu", s => s.SnipeUrl, (s, v) => s with { SnipeUrl = v }),
        new("Reporting", "ReportMateUrl", "ReportMate URL", "https://reports.example.edu", s => s.ReportMateUrl, (s, v) => s with { ReportMateUrl = v }),
        new("Teams", "TeamsApiUrl", "API URL", "https://teams-api.example.edu", s => s.TeamsApiUrl, (s, v) => s with { TeamsApiUrl = v }),
        new("Teams", "TeamsApiScope", "API scope", "api://…/.default", s => s.TeamsApiScope, (s, v) => s with { TeamsApiScope = v }),
        new("Teams", "TeamsDefaultTeam", "Default team", "team name", s => s.TeamsDefaultTeam, (s, v) => s with { TeamsDefaultTeam = v }),
        new("Teams", "TeamsDefaultChannels", "Default channels", "channel, channel", s => s.TeamsDefaultChannels, (s, v) => s with { TeamsDefaultChannels = v }),
        new("Handbook and skills", "HandbookRepoUrl", "Handbook repository", "https://git.example.edu/handbook", s => s.HandbookRepoUrl, (s, v) => s with { HandbookRepoUrl = v }),
        new("Handbook and skills", "HandbookSiteUrl", "Handbook site", "https://handbook.example.edu", s => s.HandbookSiteUrl, (s, v) => s with { HandbookSiteUrl = v }),
        new("Handbook and skills", "AgentsHubRepoUrl", "Skills repository", "https://git.example.edu/agents", s => s.AgentsHubRepoUrl, (s, v) => s with { AgentsHubRepoUrl = v }),
    };

    public static readonly IReadOnlyList<DesktopSettingField> Manage = new DesktopSettingField[]
    {
        new("Rooms", "ManageRosterPath", "Enrollment roster", "default location", s => s.ManageRosterPath, (s, v) => s with { ManageRosterPath = v }),
        new("Rooms", "ManageCommandsPath", "Command library", "default location", s => s.ManageCommandsPath, (s, v) => s with { ManageCommandsPath = v }),
        new("Remote access", "SecureShellKeyPath", "SSH key", "default key path", s => s.SecureShellKeyPath, (s, v) => s with { SecureShellKeyPath = v }),
        new("Remote access", "SecureShellUser", "SSH user", "default user", s => s.SecureShellUser, (s, v) => s with { SecureShellUser = v }),
        new("Remote access", "ManageTerminalProfile", "Terminal profile", "default profile", s => s.ManageTerminalProfile, (s, v) => s with { ManageTerminalProfile = v }),
        new("Remote access", "ManageRdpUser", "Remote Desktop user", "default user", s => s.ManageRdpUser, (s, v) => s with { ManageRdpUser = v }),
    };

    public static readonly IReadOnlyList<DesktopSettingToggle> ManageToggles = new DesktopSettingToggle[]
    {
        new("Rooms", "ManageIncludeRetired", "Include retired machines", s => s.ManageIncludeRetired, (s, v) => s with { ManageIncludeRetired = v }),
        new("Rooms", "ManageIncludeProvisioning", "Include machines still provisioning", s => s.ManageIncludeProvisioning, (s, v) => s with { ManageIncludeProvisioning = v }),
    };

    public static readonly IReadOnlyList<DesktopSettingField> Terminal = new DesktopSettingField[]
    {
        new("Agent", "AgentCommand", "Agent command", "the command a new session runs", s => s.AgentCommand, (s, v) => s with { AgentCommand = v }),
        new("Repositories", "Repos", "Tracked repositories", "one per line", s => string.Join(Environment.NewLine, s.Repos),
            (s, v) => s with { Repos = TerminalSettings.ParseList(v) }, Multiline: true),
    };

    public static readonly IReadOnlyList<DesktopSettingToggle> TerminalToggles = new DesktopSettingToggle[]
    {
        new("Agent", "AgentAutoStart", "Start a session when the app opens", s => s.AgentAutoStart, (s, v) => s with { AgentAutoStart = v }),
    };
}
