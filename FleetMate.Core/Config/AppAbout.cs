using System.Reflection;
using System.Runtime.InteropServices;

namespace FleetMate.Core.Config;

/// <summary>What Settings › About shows, the same in the WPF and WinUI apps.</summary>
public static class AppAbout
{
    public const string Repository = "github.com/emilycarru-its-infra/fleetmate-windows";

    public static readonly (string Name, string Description, string Url)[] RelatedProjects =
    {
        ("ReportMate", "Unified reporting + visibility for Mac + Windows fleets", "https://github.com/reportmate"),
        ("BootstrapMate", "Provisioning + bootstrap tooling with a DevOps-first workflow", "https://github.com/bootstrapmate"),
        ("Cimian", "Managed software deployment for MSI(X), EXE, NUPKG, and PWSH on Windows", "https://github.com/windowsadmins/cimian"),
        ("ASBMUtil", "Apple School & Business Manager CLI + GUI", "https://github.com/rodchristiansen/asbmutil"),
    };

    public const string AuthorName = "Rod Christiansen";

    public static readonly string[] AuthorLines =
    {
        "Devices Administrator Lead",
        "Vancouver, BC, Canada",
        "Managing a fleet of 1000+ computers. Focused on infrastructure, DevOps architecture, CI/CD pipelines, and automating at scale.",
    };

    public static readonly (string Label, string Display, string Url)[] AuthorLinks =
    {
        ("GitHub", "github.com/rodchristiansen", "https://github.com/rodchristiansen"),
        ("Blog", "blog.focused.systems", "https://blog.focused.systems"),
    };

    /// <summary>The line under the app name.</summary>
    public static string Tagline(AppEdition edition) => edition.IsTicketsOnly
        ? "Service desk tickets for Windows"
        : "Unified fleet management for Windows";

    public static string Version(Assembly app) => AppVersionDisplay.For(app);

    public static string Platform =>
        $"{RuntimeInformation.OSDescription.Trim()} ({RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()})";

    public static string Runtime => RuntimeInformation.FrameworkDescription;

    /// <summary>What Copy Version Info puts on the clipboard.</summary>
    public static string VersionInfo(AppEdition edition, Assembly app) =>
        $"{edition.Name} {Version(app)} · {Platform} · {Runtime}";
}
