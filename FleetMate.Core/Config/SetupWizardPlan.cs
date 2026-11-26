namespace FleetMate.Core.Config;

/// <summary>A group of endpoints the setup wizard asks for, named for what it does.</summary>
public sealed record SetupConnectionSection(string Title, string Description, IReadOnlyList<SetupField> Fields);

/// <summary>One endpoint: the registry value it writes and the label it shows.</summary>
public sealed record SetupField(string Key, string Label);

/// <summary>
/// The guided setup, as on FleetMate for Mac, shared by the WPF and WinUI
/// wizards: when it opens by itself, its steps for the chosen modules, the
/// endpoints each module needs, and how they are saved. There is never a
/// secret to enter.
/// </summary>
public static class SetupWizardPlan
{
    public const string Welcome = "Welcome";
    public const string Modules = "Modules";
    public const string Connections = "Connections";
    public const string Repositories = "Repositories";
    public const string Summary = "Summary";

    /// <summary>
    /// A first launch needs the wizard when nothing has an endpoint yet and it
    /// has never been finished or skipped. Policy-supplied endpoints count, so
    /// a managed PC never sees it.
    /// </summary>
    public static bool NeededAtLaunch(FleetMateConfig config, bool setupCompleted) =>
        !setupCompleted
        && string.IsNullOrWhiteSpace(config.AzureDevOps?.Organization)
        && string.IsNullOrWhiteSpace(config.SnipeUrl)
        && string.IsNullOrWhiteSpace(config.Tdx?.BaseUrl);

    /// <summary>
    /// The sign-in step: Development when it is chosen (gh and az), Sign In
    /// when only Devices, Identity or Projects need az, otherwise none.
    /// </summary>
    public static string? SignInStep(IReadOnlySet<string> hidden) =>
        !hidden.Contains("Development") ? "Development"
        : !hidden.Contains("Devices") || !hidden.Contains("Identity") || !hidden.Contains("Projects") ? "Sign In"
        : null;

    /// <summary>
    /// The steps for <paramref name="edition"/> and the chosen modules.
    /// TicketsMate has one module and signs in with the Windows account alone,
    /// so it skips Modules and the sign-in step.
    /// </summary>
    public static IReadOnlyList<string> Steps(AppEdition edition, IReadOnlySet<string> hidden, bool includeRepositories) =>
        edition.IsTicketsOnly
            ? new[] { Welcome, Connections, Summary }
            : new[] { Welcome, Modules, Connections, SignInStep(hidden),
                      includeRepositories && !hidden.Contains("Development") ? Repositories : null, Summary }
                .OfType<string>().ToArray();

    /// <summary>The endpoints the chosen modules need, in the order the wizard shows them.</summary>
    public static IReadOnlyList<SetupConnectionSection> ConnectionSections(IReadOnlySet<string> hidden)
    {
        bool Shows(string tag) => !hidden.Contains(tag);
        var sections = new List<SetupConnectionSection>();
        if (Shows("Devices") || Shows("Identity") || Shows(AppModules.Enrollment))
            sections.Add(new("Devices & Identity", "Managed devices, users and groups. Connector: Microsoft Graph.",
                new SetupField[] { new("GraphTenantId", "Tenant ID"), new("GraphClientId", "Client ID") }));
        if (Shows("Projects") || Shows("Development"))
            sections.Add(new("Projects", "Boards, work items and issues. Connector: Azure DevOps.",
                new SetupField[] { new("DevOpsOrganization", "Organization"), new("DevOpsProject", "Project") }));
        if (Shows("Inventory"))
            sections.Add(new("Inventory", "Asset inventory and lifecycle. Connector: Snipe-IT.",
                new SetupField[] { new("SnipeUrl", "Base URL") }));
        if (Shows("Tickets"))
            sections.Add(new("Tickets", "Service desk tickets. Connector: TeamDynamix.",
                new SetupField[] { new("TdxBaseUrl", "Base URL"), new("TdxAppId", "Ticketing App ID") }));
        if (Shows("Reporting"))
            sections.Add(new("Reporting", "Fleet reporting and device telemetry. Connector: ReportMate.",
                new SetupField[] { new("ReportMateUrl", "API URL") }));
        if (Shows("Development"))
            sections.Add(new("Handbook and Skills", "Shown beside Development and in the agent terminal.",
                new SetupField[] { new("HandbookRepoUrl", "Handbook repository"), new("HandbookSiteUrl", "Handbook site"), new("AgentsHubRepoUrl", "Skills repository") }));
        return sections;
    }

    /// <summary>How the Summary step names an endpoint.</summary>
    public static string FieldLabel(string key) => key switch
    {
        "GraphTenantId" => "Graph tenant",
        "GraphClientId" => "Graph client",
        "DevOpsOrganization" => "DevOps organization",
        "DevOpsProject" => "DevOps project",
        "SnipeUrl" => "Snipe-IT",
        "TdxBaseUrl" => "TeamDynamix",
        "TdxAppId" => "Ticketing app",
        "ReportMateUrl" => "ReportMate",
        "HandbookRepoUrl" => "Handbook repository",
        "HandbookSiteUrl" => "Handbook site",
        "AgentsHubRepoUrl" => "Skills repository",
        _ => key,
    };

    /// <summary>
    /// Save the endpoints this run showed. A blank one is left as it was: the
    /// wizard never clears what Settings or policy set.
    /// </summary>
    public static void SaveEndpoints(ISettingsSink sink, IEnumerable<KeyValuePair<string, string>> values)
    {
        foreach (var (key, value) in values)
            if (!string.IsNullOrWhiteSpace(value)) sink.Set(key, value.Trim());
    }
}
