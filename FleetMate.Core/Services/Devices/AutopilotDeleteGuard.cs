using FleetMate.Core.Models.Devices;

namespace FleetMate.Core.Services.Devices;

/// <summary>
/// The answer to "does Intune still hold a record for this device?". A failed
/// lookup is its own state and never reads as "no record": the guard below
/// refuses on it rather than deleting on an empty answer it can't trust.
/// </summary>
public sealed record ManagedDeviceLookup(bool Succeeded, IReadOnlyList<IntuneDevice> Devices, string? Error = null)
{
    public static ManagedDeviceLookup Found(IReadOnlyList<IntuneDevice> devices) => new(true, devices);
    public static ManagedDeviceLookup Failed(string error) => new(false, Array.Empty<IntuneDevice>(), error);
}

/// <summary>The Graph reads the Autopilot delete guard makes, so it can be tested without a tenant.</summary>
public interface IAutopilotDeleteGraph
{
    /// <summary>The Intune records matching an OData filter, or a failure.</summary>
    Task<ManagedDeviceLookup> LookupManagedDevicesAsync(string filter);
}

/// <summary>
/// Refuses to release an Autopilot registration while the device still has an
/// Intune record. Deleting the registration under an enrolled machine leaves
/// it managed but no longer known to Autopilot, so its next reset or
/// re-provision lands in a plain out-of-box setup. The Intune record has to
/// go first (wipe or retire, then delete); the offboard command does that in
/// order and doesn't need this check.
/// </summary>
public sealed class AutopilotDeleteGuard(IAutopilotDeleteGraph graph)
{
    public const string AsyncNote =
        "Autopilot removes the registration in the background; it can take several minutes to disappear from listings.";

    /// <summary>Null when it is safe to delete; otherwise the reason to refuse.</summary>
    public async Task<string?> CheckAsync(AutopilotDevice registration)
    {
        var serial = registration.SerialNumber?.Trim();
        var serialFilter = string.IsNullOrEmpty(serial) ? null : CliTargets.SerialFilter(serial);
        var boundId = BoundManagedDeviceId(registration.ManagedDeviceId);

        if (serialFilter == null && boundId == null)
            return "Refusing: the registration has no usable serial number or Intune device id, so its Intune record can't be checked.";

        if (serialFilter != null)
        {
            var refusal = Evaluate(await graph.LookupManagedDevicesAsync(serialFilter), $"serial {serial}");
            if (refusal != null) return refusal;
        }
        if (boundId != null)
        {
            var refusal = Evaluate(await graph.LookupManagedDevicesAsync($"id eq {ODataFilter.Guid(boundId)}"), $"Intune id {boundId}");
            if (refusal != null) return refusal;
        }
        return null;
    }

    private static string? Evaluate(ManagedDeviceLookup lookup, string target)
    {
        if (!lookup.Succeeded)
            return $"Refusing: the Intune lookup for {target} failed ({lookup.Error ?? "unknown error"}), so it can't be confirmed that the device has left Intune.";
        if (lookup.Devices.Count == 0) return null;
        var names = string.Join(", ", lookup.Devices.Select(d => d.DeviceName ?? d.Id));
        return $"Refusing: {target} still has an Intune record ({names}). Wipe or retire it and delete the Intune record first, or use `fleetmate intune offboard`.";
    }

    /// <summary>The registration's bound managedDevice id, ignoring empty and all-zero values.</summary>
    public static string? BoundManagedDeviceId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id.Trim(), out var guid) || guid == Guid.Empty) return null;
        return id.Trim();
    }
}
