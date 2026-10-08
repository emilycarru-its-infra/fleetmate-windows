namespace FleetMate.Core.Config;

/// <summary>One tab in the tab bar that the operator can switch off.</summary>
public sealed record AppModule(string Tag, string Title, string Subtitle);

/// <summary>
/// The tabs FleetMate can show, in tab-bar order, and which ones the operator
/// has switched off (Settings › General › Enabled Modules). Hiding a module
/// only hides its tab; its endpoints and sign-in stay as they are, so turning
/// it back on needs no setup. At least one tab always stays visible.
/// </summary>
public static class AppModules
{
    public static readonly IReadOnlyList<AppModule> All = new[]
    {
        new AppModule("Development", "Development", "Pull requests, commits, pipelines and the GitHub inbox"),
        new AppModule("Projects", "Projects", "Azure DevOps boards and GitHub issues"),
        new AppModule("Devices", "Devices", "Intune, Autopilot and Apple organization devices"),
        new AppModule("Reporting", "Reporting", "The ReportMate fleet dashboard"),
        new AppModule("Manage", "Manage", "Lab operations over SSH and Remote Desktop"),
        new AppModule("Inventory", "Inventory", "Snipe-IT asset management"),
        new AppModule("Identity", "Identity", "Entra ID users and groups"),
        new AppModule("Tickets", "Tickets", "TeamDynamix service desk"),
    };

    /// <summary>Read the stored list of hidden tabs, ignoring anything that is not a known tab.</summary>
    public static IReadOnlySet<string> ParseHidden(string? stored)
    {
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(stored)) return hidden;
        foreach (var part in stored.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (All.FirstOrDefault(m => m.Tag.Equals(part, StringComparison.OrdinalIgnoreCase)) is { } module)
                hidden.Add(module.Tag);
        // Never hide every tab: a stored list that would is ignored.
        return hidden.Count >= All.Count ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : hidden;
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
        return next.Count >= All.Count ? hidden : next;
    }

    /// <summary>The tabs of <paramref name="order"/> left showing.</summary>
    public static IReadOnlyList<string> Visible(IReadOnlyList<string> order, IReadOnlySet<string> hidden)
    {
        var visible = order.Where(t => !hidden.Contains(t)).ToList();
        return visible.Count > 0 ? visible : order.ToList();
    }

    /// <summary>
    /// Whether the services a tab reads from have the endpoint they need.
    /// A switched-on module that is not set up shows a Configure button
    /// rather than an empty tab.
    /// </summary>
    public static bool IsConfigured(string tag, FleetMateConfig config) => tag switch
    {
        "Projects" => !string.IsNullOrWhiteSpace(config.AzureDevOps?.Organization)
                      || config.Tasks?.Providers?.GitHub is { Enabled: true },
        "Devices" or "Identity" => !string.IsNullOrWhiteSpace(config.Graph?.TenantId),
        "Reporting" => !string.IsNullOrWhiteSpace(config.ReportMateUrl),
        "Inventory" => !string.IsNullOrWhiteSpace(config.SnipeUrl),
        "Tickets" => !string.IsNullOrWhiteSpace(config.Tdx?.BaseUrl),
        _ => true,
    };
}
