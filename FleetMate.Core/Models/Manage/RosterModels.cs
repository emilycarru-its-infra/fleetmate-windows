namespace FleetMate.Core.Models.Manage;

/// <summary>
/// One row of the fleet roster (the enrollment computers.csv). Serial is the
/// stable identity; hostname is what the network knows the machine as, and is
/// empty for machines that have not been provisioned yet.
/// </summary>
public class RosterComputer
{
    public const string AdhocSerialPrefix = "adhoc-";

    public string Serial { get; init; } = "";
    public string Catalog { get; init; } = "";
    public string Area { get; init; } = "";
    public string Location { get; init; } = "";
    public string Asset { get; init; } = "";
    public string Usage { get; init; } = "";
    public string Status { get; init; } = "";
    /// <summary>Person the machine is assigned to, or a descriptive label for shared machines.</summary>
    public string Allocation { get; init; } = "";
    public string Username { get; init; } = "";
    public string Platform { get; init; } = "";
    /// <summary>Lab or fleet grouping (for example "Studio Lab A"). The real lab grouping; location is the fallback.</summary>
    public string Fleet { get; init; } = "";
    public string Hostname { get; init; } = "";

    public string Id => Serial;

    public bool HasHostname => !string.IsNullOrWhiteSpace(Hostname);

    public bool IsAdhoc => Serial.StartsWith(AdhocSerialPrefix, StringComparison.Ordinal);

    /// <summary>
    /// In-service means any "Active" variant: Active, Active (Legacy), Active (Buyouts),
    /// Active (Lease End). Everything else has left the fleet and must never enter a batch.
    /// </summary>
    public bool IsInService => Status.TrimStart().StartsWith("Active", StringComparison.OrdinalIgnoreCase);

    /// <summary>The roster's friendly name (allocation), falling back to <see cref="DisplayName"/>.</summary>
    public string FriendlyName => !string.IsNullOrWhiteSpace(Allocation) ? Allocation : DisplayName;

    /// <summary>What to show for the machine: hostname, else the assignee, else the serial.</summary>
    public string DisplayName =>
        HasHostname ? Hostname
        : !string.IsNullOrWhiteSpace(Allocation) ? Allocation
        : Serial;

    /// <summary>A temporary machine that is not in the roster, addressed by hostname or IP.</summary>
    public static RosterComputer Adhoc(string hostname, string ip)
    {
        var label = string.IsNullOrWhiteSpace(hostname) ? ip : hostname.Trim();
        return new RosterComputer
        {
            Serial = AdhocSerialPrefix + label,
            Status = "Active",
            Allocation = label,
            Hostname = label,
            Platform = "Windows"
        };
    }

    /// <summary>One line suitable for a ticket or hand-off note.</summary>
    public string InventoryLine(string? ip = null, string? osVersion = null)
    {
        var parts = new List<string> { DisplayName };
        if (!string.IsNullOrEmpty(ip)) parts.Add(ip);
        if (!IsAdhoc) parts.Add(Serial);
        if (!string.IsNullOrEmpty(Asset)) parts.Add(Asset);
        if (!string.IsNullOrEmpty(Location)) parts.Add(Location);
        if (!string.IsNullOrEmpty(osVersion)) parts.Add(osVersion);
        return string.Join("  ", parts);
    }
}

/// <summary>
/// A sidebar group: a lab, a kiosk room, a department's staff machines, or a
/// letter bucket of faculty machines. Number is the short key shown first
/// (room number, department, letter); DisplayName the longer label when known.
/// </summary>
public class RosterRoom
{
    public string Number { get; init; } = "";
    public string? DisplayName { get; init; }
    public List<RosterComputer> Computers { get; init; } = new();

    public string Id => Number + "|" + (DisplayName ?? "");
    public int Count => Computers.Count;
    public string Name => DisplayName is { Length: > 0 } d && d != Number ? $"{Number} · {d}" : Number;
}

public enum RosterSection
{
    Labs,
    Kiosks,
    Staff,
    Faculty
}

/// <summary>The roster split into sidebar sections plus the full source list for search.</summary>
public class FleetRoster
{
    public List<RosterRoom> Labs { get; init; } = new();
    public List<RosterRoom> Kiosks { get; init; } = new();
    public List<RosterRoom> Staff { get; init; } = new();
    public List<RosterRoom> Faculty { get; init; } = new();
    /// <summary>Every parsed row that passed the in-service filter, sorted by display name.</summary>
    public List<RosterComputer> Source { get; init; } = new();
    /// <summary>Rows dropped by the in-service filter, kept so the count can be shown.</summary>
    public int RetiredCount { get; init; }

    public static FleetRoster Empty => new();

    public IEnumerable<RosterRoom> Rooms(RosterSection section) => section switch
    {
        RosterSection.Labs => Labs,
        RosterSection.Kiosks => Kiosks,
        RosterSection.Staff => Staff,
        RosterSection.Faculty => Faculty,
        _ => Enumerable.Empty<RosterRoom>()
    };

    public IEnumerable<RosterComputer> AllSectioned =>
        Labs.Concat(Kiosks).Concat(Staff).Concat(Faculty).SelectMany(r => r.Computers);
}
