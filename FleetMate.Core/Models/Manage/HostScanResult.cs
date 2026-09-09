namespace FleetMate.Core.Models.Manage;

/// <summary>Where a machine's address came from during a scan.</summary>
public enum AddressSource
{
    None,
    ReportMate,
    Dns,
    Stored
}

public enum HostState
{
    /// <summary>No address could be found.</summary>
    Unresolved,
    /// <summary>An address is known but neither SSH nor RDP answered.</summary>
    Unreachable,
    /// <summary>At least one of SSH or RDP answered on the address.</summary>
    Online
}

/// <summary>What a scan learned about one machine.</summary>
public class HostScanResult
{
    public string Serial { get; init; } = "";
    public string Ip { get; init; } = "";
    public AddressSource Source { get; init; }
    /// <summary>When the inventory system last collected the address; null when unknown or not from inventory.</summary>
    public DateTime? AddressCollectedAt { get; init; }
    public bool SshOpen { get; init; }
    public bool RdpOpen { get; init; }
    public DateTime ScannedAt { get; init; } = DateTime.Now;

    public bool HasAddress => Ip.Length > 0;

    public HostState State =>
        !HasAddress ? HostState.Unresolved
        : (SshOpen || RdpOpen) ? HostState.Online
        : HostState.Unreachable;

    /// <summary>Age of the inventory address, when it came from inventory.</summary>
    public TimeSpan? AddressAge => AddressCollectedAt.HasValue ? DateTime.UtcNow - AddressCollectedAt.Value.ToUniversalTime() : null;

    /// <summary>An inventory address older than this deserves a warning next to it.</summary>
    public static readonly TimeSpan StaleAddressAge = TimeSpan.FromHours(24);

    public bool AddressIsStale => AddressAge is { } age && age > StaleAddressAge;

    public static HostScanResult Unresolved(string serial) => new() { Serial = serial };
}

/// <summary>Which sources answered during the last scan, for the badge.</summary>
public enum ScanMode
{
    Unknown,
    ReportMate,
    DnsOnly,
    Limited
}

public static class ScanModeExtensions
{
    public static string Label(this ScanMode mode) => mode switch
    {
        ScanMode.ReportMate => "ReportMate active",
        ScanMode.DnsOnly => "DNS only",
        ScanMode.Limited => "Limited connectivity",
        _ => "No scan yet"
    };
}

public class ScanSummary
{
    public ScanMode Mode { get; init; }
    public int Total { get; init; }
    public int Resolved { get; init; }
    public int Online { get; init; }
    public int FromReportMate { get; init; }
    public int FromDns { get; init; }
    public bool ReportMateAvailable { get; init; }
    public TimeSpan Duration { get; init; }
}
