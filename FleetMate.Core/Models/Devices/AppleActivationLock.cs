using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace FleetMate.Core.Models.Devices;

/// <summary>
/// A device's Activation Lock state as its Apple organization reports it.
/// The organization knows the live state, covers Macs as well as iPhones and
/// iPads, and is the only source that says which kind of lock is set. A read
/// that fails or returns nothing is Unknown, never Disabled: Apple fails the
/// read for devices in an internal-only lock state, and "Disabled" from a
/// failure would be the wrong answer. Report only — nothing here clears a lock.
/// </summary>
public enum AppleActivationLock
{
    MdmLock,
    UserLock,
    /// <summary>Locked, but the organization did not say which kind.</summary>
    Enabled,
    Disabled,
    Unknown,
}

public static class AppleActivationLocks
{
    /// <summary>Map the organization's answer; a null isLocked means it could not report one.</summary>
    public static AppleActivationLock From(bool? isLocked, string? lockType)
    {
        if (isLocked is not { } locked) return AppleActivationLock.Unknown;
        if (!locked) return AppleActivationLock.Disabled;
        return lockType?.ToUpperInvariant() switch
        {
            "MDM" => AppleActivationLock.MdmLock,
            "USER" => AppleActivationLock.UserLock,
            _ => AppleActivationLock.Enabled,
        };
    }

    public static bool IsLocked(this AppleActivationLock l) =>
        l is AppleActivationLock.MdmLock or AppleActivationLock.UserLock or AppleActivationLock.Enabled;

    /// <summary>Short value for the table column.</summary>
    public static string ColumnText(this AppleActivationLock l) => l switch
    {
        AppleActivationLock.MdmLock => "Enabled — MDM",
        AppleActivationLock.UserLock => "Enabled — User",
        AppleActivationLock.Enabled => "Enabled",
        AppleActivationLock.Disabled => "Disabled",
        _ => "Unknown",
    };

    /// <summary>Full value for the inspector, saying what the lock means for clearing it.</summary>
    public static string DetailText(this AppleActivationLock l) => l switch
    {
        AppleActivationLock.MdmLock => "Enabled — MDM lock (bypass code escrowed; clearing doesn't need the owner)",
        AppleActivationLock.UserLock => "Enabled — User lock (needs the owner's Apple Account)",
        AppleActivationLock.Enabled => "Enabled",
        AppleActivationLock.Disabled => "Disabled",
        _ => "Unknown",
    };
}

/// <summary>
/// Activation Lock states read this session, by serial. Read one device at a
/// time on selection, so most rows have none and the column reads "—".
/// </summary>
public static class ActivationLockCache
{
    private static readonly ConcurrentDictionary<string, AppleActivationLock> States = new();

    public static AppleActivationLock? Get(string? serial) =>
        serial != null && States.TryGetValue(DeviceListJoin.Normalize(serial), out var state) ? state : null;

    public static void Set(string serial, AppleActivationLock state) => States[DeviceListJoin.Normalize(serial)] = state;

    public static void Clear() => States.Clear();
}

/// <summary>GET /v1/orgDevices/{serial}/activationLockStatus → data.attributes.</summary>
public sealed class AppleActivationLockAttributes
{
    [JsonPropertyName("isLocked")] public bool? IsLocked { get; set; }
    /// <summary>MDM, USER or NONE.</summary>
    [JsonPropertyName("lockType")] public string? LockType { get; set; }
}
