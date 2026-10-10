namespace FleetMate.Core.Config;

/// <summary>
/// A part of FleetMate the operator can switch off. Every tab is one, plus
/// Enrollment, which is not a tab: it feeds Mac and Windows enrollment
/// records into Devices. <paramref name="Glyph"/> is a Segoe Fluent Icons
/// code point.
/// </summary>
public sealed record AppModule(string Tag, string Title, string Subtitle, string Glyph, bool IsTab = true);

/// <summary>
/// The modules FleetMate can show, in tab-bar order, and which ones the
/// operator has switched off (Settings › General › Enabled Modules). Hiding a
/// module only hides it; its endpoints and sign-in stay as they are, so
/// turning it back on needs no setup. At least one tab always stays visible.
///
/// Titles and subtitles say what a module does, never which product backs it:
/// the connector in use is named only where it is configured, under
/// Settings › Authentication.
/// </summary>
public static class AppModules
{
    public const string Enrollment = "Enrollment";

    public static readonly IReadOnlyList<AppModule> All = new[]
    {
        new AppModule("Development", "Development", "Repositories, pull requests, pipelines and the coding agent", "\uE943"),
        new AppModule("Projects", "Projects", "Boards, work items and issues", "\uE9D5"),
        new AppModule("Devices", "Devices", "Managed devices, compliance and remote actions", "\uE7F8"),
        new AppModule("Reporting", "Reporting", "Fleet reporting and device telemetry", "\uE9D9"),
        new AppModule("Manage", "Manage", "Lab operations over SSH and Remote Desktop", "\uE90F"),
        new AppModule("Inventory", "Inventory", "Asset inventory and lifecycle", "\uE7B8"),
        new AppModule("Identity", "Identity", "Users, groups and directory roles", "\uE716"),
        new AppModule("Tickets", "Tickets", "Service desk tickets", "\uE8EC"),
        new AppModule(Enrollment, "Enrollment", "Mac and Windows enrollment records, joined to Devices", "\uE896", IsTab: false),
    };

    /// <summary>The modules that are tabs, in tab-bar order.</summary>
    public static IReadOnlyList<AppModule> Tabs { get; } = All.Where(m => m.IsTab).ToList();

    /// <summary>Names older builds or hand-edited values may use for a module.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Apple"] = Enrollment,
        ["AppleOrg"] = Enrollment,
        ["Autopilot"] = Enrollment,
        ["Intune"] = "Devices",
        ["Entra"] = "Identity",
        ["Assets"] = "Inventory",
        ["Snipe"] = "Inventory",
        ["Tdx"] = "Tickets",
        ["DevOps"] = "Projects",
        ["Repos"] = "Development",
    };

    /// <summary>Read the stored list of hidden modules, ignoring anything that is not a known module.</summary>
    public static IReadOnlySet<string> ParseHidden(string? stored)
    {
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(stored)) return hidden;
        foreach (var part in stored.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = Aliases.GetValueOrDefault(part, part);
            if (All.FirstOrDefault(m => m.Tag.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } module)
                hidden.Add(module.Tag);
        }
        // Never hide every tab: a stored list that would is ignored.
        return HidesEveryTab(hidden) ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : hidden;
    }

    /// <summary>The stored form of a hidden set, in tab-bar order.</summary>
    public static string FormatHidden(IEnumerable<string> hidden)
    {
        var set = new HashSet<string>(hidden, StringComparer.OrdinalIgnoreCase);
        return string.Join(";", All.Where(m => set.Contains(m.Tag)).Select(m => m.Tag));
    }

    /// <summary>
    /// <paramref name="hidden"/> with <paramref name="tag"/> shown or hidden.
    /// Hiding the last visible tab is refused and returns the set unchanged.
    /// </summary>
    public static IReadOnlySet<string> WithModule(IReadOnlySet<string> hidden, string tag, bool shown)
    {
        var next = new HashSet<string>(hidden, StringComparer.OrdinalIgnoreCase);
        if (shown) next.Remove(tag);
        else next.Add(tag);
        return HidesEveryTab(next) ? hidden : next;
    }

    /// <summary>Whether <paramref name="tag"/> is switched on.</summary>
    public static bool IsOn(IReadOnlySet<string> hidden, string tag) => !hidden.Contains(tag);

    /// <summary>The tabs of <paramref name="order"/> left showing.</summary>
    public static IReadOnlyList<string> Visible(IReadOnlyList<string> order, IReadOnlySet<string> hidden)
    {
        var visible = order.Where(t => !hidden.Contains(t)).ToList();
        return visible.Count > 0 ? visible : order.ToList();
    }

    private static bool HidesEveryTab(IReadOnlySet<string> hidden) => Tabs.All(t => hidden.Contains(t.Tag));

    /// <summary>
    /// Whether the services a module reads from have the endpoint they need.
    /// A switched-on module that is not set up shows a Configure button
    /// rather than an empty tab. Enrollment reads Windows registrations
    /// through the Devices connection and Mac records from the enrollment
    /// organizations saved in Settings, so either one is enough.
    /// </summary>
    public static bool IsConfigured(string tag, FleetMateConfig config, bool hasEnrollmentOrganizations = false) => tag switch
    {
        "Projects" => !string.IsNullOrWhiteSpace(config.AzureDevOps?.Organization)
                      || config.Tasks?.Providers?.GitHub is { Enabled: true },
        "Devices" or "Identity" => !string.IsNullOrWhiteSpace(config.Graph?.TenantId),
        "Reporting" => !string.IsNullOrWhiteSpace(config.ReportMateUrl),
        "Inventory" => !string.IsNullOrWhiteSpace(config.SnipeUrl),
        "Tickets" => !string.IsNullOrWhiteSpace(config.Tdx?.BaseUrl),
        Enrollment => hasEnrollmentOrganizations || !string.IsNullOrWhiteSpace(config.Graph?.TenantId),
        _ => true,
    };

    /// <summary>What a switched-on module still needs, for its row in Settings.</summary>
    public static string NeedsMessage(string tag) => tag switch
    {
        "Manage" => "Needs the enrollment roster before it can show rooms.",
        Enrollment => "Needs the Devices connection or an enrollment organization.",
        _ => "Needs a connection before it can load anything.",
    };
}
