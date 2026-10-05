using FleetMate.Core.Models.Devices;

namespace FleetMate.Core.Services.Devices;

/// <summary>One Intune record a typed identifier could mean.</summary>
public sealed record DeviceCandidate(string Id, string? Name, string? Serial, DateTime? LastSync)
{
    public static DeviceCandidate From(IntuneDevice d) => new(d.Id, d.DeviceName, d.SerialNumber, d.LastSyncDateTime);

    public override string ToString() =>
        $"{Id}  {Name ?? "-"}  serial {Serial ?? "-"}  last sync {LastSync?.ToString("yyyy-MM-dd HH:mm") ?? "-"}";
}

/// <summary>What a typed identifier resolved to.</summary>
public abstract record TargetResolution
{
    public sealed record Resolved(DeviceCandidate Device) : TargetResolution;
    public sealed record NotFound(string Identifier) : TargetResolution;
    public sealed record Ambiguous(string Identifier, IReadOnlyList<DeviceCandidate> Candidates) : TargetResolution;
    public sealed record Invalid(string Identifier, string Reason) : TargetResolution;
}

/// <summary>
/// Resolves the identifier a destructive action was given to exactly one
/// Intune record, or refuses. A managedDevice id must exist; a serial must
/// match exactly one record by equality. There is no partial match and no
/// falling back from a serial to a device name: wiping the wrong machine
/// because its name looked similar is the failure this exists to prevent.
/// </summary>
public static class DestructiveTargetResolver
{
    public static Task<TargetResolution> ResolveSingleAsync(GraphService graph, string identifier) =>
        ResolveSingleAsync(identifier, graph.GetManagedDeviceByIdAsync, s => graph.GetDevicesBySerialAsync(s));

    public static async Task<TargetResolution> ResolveSingleAsync(
        string identifier,
        Func<string, Task<IntuneDevice?>> byId,
        Func<string, Task<List<IntuneDevice>>> bySerial)
    {
        var value = (identifier ?? "").Trim();

        if (ODataFilter.IsGuid(value))
        {
            var device = await byId(value);
            return device == null
                ? new TargetResolution.NotFound(value)
                : new TargetResolution.Resolved(DeviceCandidate.From(device));
        }

        if (!ODataFilter.IsSerial(value))
            return new TargetResolution.Invalid(value,
                "Give a serial number (letters, digits and hyphens) or a managedDevice id. Device names are not accepted for this action.");

        var matches = (await bySerial(value))
            .Where(d => string.Equals(d.SerialNumber?.Trim(), value, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            0 => new TargetResolution.NotFound(value),
            1 => new TargetResolution.Resolved(DeviceCandidate.From(matches[0])),
            _ => new TargetResolution.Ambiguous(value, matches
                .OrderByDescending(d => d.LastSyncDateTime ?? DateTime.MinValue)
                .Select(DeviceCandidate.From)
                .ToList()),
        };
    }
}
