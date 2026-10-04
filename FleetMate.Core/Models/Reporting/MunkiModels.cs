using System.Globalization;
using System.Text.Json.Serialization;

namespace FleetMate.Core.Models.Reporting;

/// <summary>MunkiReport's rows arrive as text; these read them the way the macOS client does.</summary>
internal static class MunkiRow
{
    public static string Get(IReadOnlyDictionary<string, string> row, string key) =>
        row.TryGetValue(key, out var v) ? v : "";

    public static DateTime? Time(IReadOnlyDictionary<string, string> row, string key) =>
        DateTime.TryParseExact(Get(row, key), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var t) ? t : null;
}

/// <summary>A device from MunkiReport's reportdata table.</summary>
public sealed class MunkiDevice
{
    [JsonPropertyName("serialNumber")] public string SerialNumber { get; init; } = "";
    [JsonPropertyName("hostname")] public string Hostname { get; init; } = "";
    [JsonPropertyName("machineName")] public string MachineName { get; init; } = "";
    [JsonPropertyName("osVersion")] public string OsVersion { get; init; } = "";
    [JsonPropertyName("buildVersion")] public string BuildVersion { get; init; } = "";
    [JsonPropertyName("machineModel")] public string MachineModel { get; init; } = "";
    [JsonPropertyName("cpuType")] public string CpuType { get; init; } = "";
    [JsonPropertyName("physicalMemory")] public long PhysicalMemory { get; init; }
    [JsonPropertyName("remoteIp")] public string RemoteIp { get; init; } = "";
    [JsonPropertyName("timestamp")] public DateTime? Timestamp { get; init; }

    public static MunkiDevice FromRow(IReadOnlyDictionary<string, string> row) => new()
    {
        SerialNumber = MunkiRow.Get(row, "serial_number"),
        Hostname = MunkiRow.Get(row, "hostname"),
        MachineName = MunkiRow.Get(row, "machine_name"),
        OsVersion = MunkiRow.Get(row, "os_version"),
        BuildVersion = MunkiRow.Get(row, "buildversion"),
        MachineModel = MunkiRow.Get(row, "machine_model"),
        CpuType = MunkiRow.Get(row, "cpu_type"),
        PhysicalMemory = long.TryParse(MunkiRow.Get(row, "physical_memory"), out var m) ? m : 0,
        RemoteIp = MunkiRow.Get(row, "remote_ip"),
        Timestamp = MunkiRow.Time(row, "timestamp"),
    };

    [JsonIgnore]
    public string DisplayName =>
        MachineName.Length > 0 ? MachineName : Hostname.Length > 0 ? Hostname : SerialNumber;

    [JsonIgnore]
    public string LastSeenFormatted => Timestamp is { } t ? Relative(t) : "Unknown";

    internal static string Relative(DateTime t)
    {
        var span = DateTime.Now - t;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return Plural((int)span.TotalMinutes, "minute");
        if (span.TotalDays < 1) return Plural((int)span.TotalHours, "hour");
        return Plural((int)span.TotalDays, "day");
    }

    private static string Plural(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")} ago";
}

/// <summary>The latest Munki run for a device.</summary>
public sealed class MunkiInfo
{
    [JsonPropertyName("serialNumber")] public string SerialNumber { get; init; } = "";
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("manifest")] public string Manifest { get; init; } = "";
    [JsonPropertyName("manifestUrl")] public string ManifestUrl { get; init; } = "";
    [JsonPropertyName("runType")] public string RunType { get; init; } = "";
    [JsonPropertyName("startTime")] public DateTime? StartTime { get; init; }
    [JsonPropertyName("endTime")] public DateTime? EndTime { get; init; }

    public static MunkiInfo FromRow(IReadOnlyDictionary<string, string> row) => new()
    {
        SerialNumber = MunkiRow.Get(row, "serial_number"),
        Version = MunkiRow.Get(row, "version"),
        Manifest = MunkiRow.Get(row, "manifest"),
        ManifestUrl = MunkiRow.Get(row, "manifesturl"),
        RunType = MunkiRow.Get(row, "runtype"),
        StartTime = MunkiRow.Time(row, "starttime"),
        EndTime = MunkiRow.Time(row, "endtime"),
    };

    [JsonIgnore]
    public string Duration
    {
        get
        {
            if (StartTime is not { } start || EndTime is not { } end) return "Unknown";
            var seconds = (end - start).TotalSeconds;
            return seconds < 60 ? $"{(int)seconds} seconds"
                : seconds < 3600 ? $"{(int)(seconds / 60)} minutes"
                : $"{(int)(seconds / 3600)} hours";
        }
    }
}

/// <summary>A managed install record for one device.</summary>
public sealed class ManagedInstall
{
    [JsonPropertyName("serialNumber")] public string SerialNumber { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; init; } = "";
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("installedVersion")] public string InstalledVersion { get; init; } = "";
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("installed")] public bool Installed { get; init; }

    public static ManagedInstall FromRow(IReadOnlyDictionary<string, string> row) => new()
    {
        SerialNumber = MunkiRow.Get(row, "serial_number"),
        Name = MunkiRow.Get(row, "name"),
        DisplayName = MunkiRow.Get(row, "display_name"),
        Version = MunkiRow.Get(row, "version"),
        InstalledVersion = MunkiRow.Get(row, "installed_version"),
        Status = MunkiRow.Get(row, "status"),
        Installed = MunkiRow.Get(row, "installed") == "1",
    };
}

/// <summary>An install that isn't in the installed state, across all devices.</summary>
public sealed class MunkiInstallError
{
    [JsonPropertyName("serialNumber")] public string SerialNumber { get; init; } = "";
    [JsonPropertyName("hostname")] public string Hostname { get; init; } = "";
    [JsonPropertyName("itemName")] public string ItemName { get; init; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; init; } = "";
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("status")] public string Status { get; init; } = "";

    public static MunkiInstallError FromRow(IReadOnlyDictionary<string, string> row) => new()
    {
        SerialNumber = MunkiRow.Get(row, "serial_number"),
        Hostname = MunkiRow.Get(row, "hostname"),
        ItemName = MunkiRow.Get(row, "name"),
        DisplayName = MunkiRow.Get(row, "display_name"),
        Version = MunkiRow.Get(row, "version"),
        Status = MunkiRow.Get(row, "status"),
    };
}
