using System.Reflection;

namespace FleetMate.Core.Config;

/// <summary>
/// Which app this build is. TicketsMate is FleetMate limited to the Tickets
/// tab: the same source, built with <c>-p:Edition=TicketsMate</c>, which names
/// the exe and stamps <c>[AssemblyMetadata("FleetMateEdition", "TicketsMate")]</c>
/// on it. Each edition keeps its own settings, policy key and single-instance
/// lock, so the two install and run side by side.
/// </summary>
public sealed class AppEdition
{
    /// <summary>The AssemblyMetadata key the GUI exe declares its edition under.</summary>
    public const string MetadataKey = "FleetMateEdition";

    public static readonly AppEdition FleetMate = new("FleetMate");
    public static readonly AppEdition TicketsMate = new("TicketsMate");

    /// <summary>
    /// The edition of the running exe. The CLI, the tests and any exe without
    /// the metadata are FleetMate.
    /// </summary>
    public static AppEdition Current { get; } = FromMetadata(ReadEntryMetadata());

    private AppEdition(string name) => Name = name;

    /// <summary>The app's name, shown in titles and used for its settings.</summary>
    public string Name { get; }

    /// <summary>True when the app shows only the Tickets tab.</summary>
    public bool IsTicketsOnly => ReferenceEquals(this, TicketsMate);

    /// <summary>The operator's own settings and endpoints, under HKCU.</summary>
    public string UserRegistryPath => $@"SOFTWARE\{Name}";

    /// <summary>Where Intune or Group Policy writes managed settings, under HKLM or HKCU.</summary>
    public string PolicyRegistryPath => $@"SOFTWARE\Policies\{Name}";

    /// <summary>The per-user folder for config.yaml and .env, and FleetMate's debug.log.</summary>
    public string UserDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "." + Name.ToLowerInvariant());

    /// <summary>The per-user local application data folder.</summary>
    public string LocalAppDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);

    /// <summary>
    /// Where the desktop app writes its logs, debug.log included. FleetMate
    /// keeps them in <see cref="UserDirectory"/>. TicketsMate keeps its own in
    /// %LOCALAPPDATA%\TicketsMate\Logs, apart from every FleetMate and
    /// Managed* log folder.
    /// </summary>
    public string LogDirectory => IsTicketsOnly
        ? Path.Combine(LocalAppDataDirectory, "Logs")
        : UserDirectory;

    /// <summary>The tabs of <paramref name="order"/> this edition carries at all.</summary>
    public IReadOnlyList<string> Tabs(IReadOnlyList<string> order) =>
        IsTicketsOnly ? order.Where(t => t == "Tickets").ToList() : order;

    /// <summary>An unknown or missing value is FleetMate, so an exe built before the key keeps working.</summary>
    public static AppEdition FromMetadata(string? value) =>
        string.Equals(value, TicketsMate.Name, StringComparison.OrdinalIgnoreCase) ? TicketsMate : FleetMate;

    private static string? ReadEntryMetadata()
    {
        try
        {
            return Assembly.GetEntryAssembly()?
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == MetadataKey)?.Value;
        }
        catch
        {
            return null;
        }
    }

    public override string ToString() => Name;
}
