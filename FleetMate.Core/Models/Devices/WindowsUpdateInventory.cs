namespace FleetMate.Core.Models.Devices;

public sealed record WindowsBuildCount(string Build, int Count, double Percentage);

public sealed record WindowsUpdateDevice(
    string DeviceName,
    string? SerialNumber,
    string Build,
    string OsVersion,
    DateTime LastSyncDateTime);

public sealed record WindowsUpdateInventory(
    DateTime Since,
    int TotalDevices,
    int MatchingDevices,
    double CoveragePercentage,
    IReadOnlyList<string> SelectedBuilds,
    IReadOnlyList<WindowsBuildCount> Builds,
    IReadOnlyList<WindowsUpdateDevice> Devices);

public static class WindowsUpdateInventoryBuilder
{
    public static WindowsUpdateInventory Build(
        IEnumerable<IntuneDevice> devices,
        DateTime since,
        IEnumerable<string>? selectedBuilds = null)
    {
        var cutoff = since.ToUniversalTime();
        var selected = (selectedBuilds ?? [])
            .Select(NormalizeBuild)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selectedSet = selected.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var current = devices
            .Where(device => string.Equals(device.OperatingSystem, "Windows", StringComparison.OrdinalIgnoreCase))
            .Where(device => device.LastSyncDateTime.HasValue && device.LastSyncDateTime.Value.ToUniversalTime() >= cutoff)
            .Select(device => new WindowsUpdateDevice(
                device.DeviceName,
                device.SerialNumber,
                NormalizeBuild(device.OsVersion),
                device.OsVersion ?? string.Empty,
                device.LastSyncDateTime!.Value.ToUniversalTime()))
            .Where(device => device.Build.Length > 0)
            .OrderBy(device => device.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var matching = selected.Length == 0
            ? current
            : current.Where(device => selectedSet.Contains(device.Build)).ToArray();
        var summaries = current
            .GroupBy(device => device.Build, StringComparer.OrdinalIgnoreCase)
            .Select(group => new WindowsBuildCount(
                group.Key,
                group.Count(),
                Percentage(group.Count(), current.Length)))
            .OrderByDescending(summary => summary.Count)
            .ThenBy(summary => summary.Build, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new WindowsUpdateInventory(
            cutoff,
            current.Length,
            matching.Length,
            Percentage(matching.Length, current.Length),
            selected,
            summaries,
            matching);
    }

    public static string NormalizeBuild(string? osVersion)
    {
        var value = (osVersion ?? string.Empty).Trim();
        var parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 4 ? string.Join('.', parts[^2..]) : value;
    }

    private static double Percentage(int count, int total) =>
        total == 0 ? 0 : Math.Round(count * 100.0 / total, 1);
}
