namespace FleetMate.Core.Config;

/// <summary>One row of Settings › General › Enabled Modules.</summary>
/// <param name="Subtitle">What the module does, plus what it still needs when it is on but not set up.</param>
/// <param name="ConfigurePane">The Settings section that sets the module up, shown as "Configure…" when it needs it.</param>
/// <param name="ToggleTip">The switch's tooltip: what turning it over will do.</param>
public sealed record ModuleRow(string Tag, string Title, string Subtitle, string Glyph, bool IsTab,
    bool Shown, bool Configured, string? ConfigurePane, string ToggleTip);

/// <summary>
/// The Enabled Modules list, for the WPF and WinUI Settings pages alike: each
/// module, whether it is shown, and when it is on but missing what it needs,
/// the section that sets it up.
/// </summary>
public static class ModuleRows
{
    public static IReadOnlyList<ModuleRow> Build(FleetMateConfig config, IReadOnlySet<string> hidden,
        bool hasEnrollmentOrganizations) =>
        AppModules.All.Select(module =>
        {
            var shown = !hidden.Contains(module.Tag);
            var configured = AppModules.IsConfigured(module.Tag, config, hasEnrollmentOrganizations);
            var needsSetup = shown && !configured;
            return new ModuleRow(
                module.Tag,
                module.Title,
                needsSetup ? $"{module.Subtitle}. {AppModules.NeedsMessage(module.Tag)}" : module.Subtitle,
                module.Glyph,
                module.IsTab,
                shown,
                configured,
                needsSetup ? ConfigurePane(module.Tag) : null,
                module.IsTab
                    ? (shown ? $"Hide the {module.Title} tab" : $"Show the {module.Title} tab")
                    : (shown ? $"Stop reading {module.Title.ToLowerInvariant()} records" : $"Read {module.Title.ToLowerInvariant()} records"));
        }).ToList();

    /// <summary>The Settings section that configures <paramref name="tag"/>.</summary>
    public static string ConfigurePane(string tag) => tag switch
    {
        "Manage" => "Manage",
        AppModules.Enrollment => "Enrollment",
        _ => "Authentication",
    };
}
