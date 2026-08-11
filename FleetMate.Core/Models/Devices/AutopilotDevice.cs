using System.Text.Json.Serialization;

namespace FleetMate.Core.Models.Devices;

/// <summary>
/// A Windows AutoPilot device identity — the hardware-hash record that survives
/// every wipe and is what lets a machine find its deployment profile at OOBE.
///
/// It is deliberately never deleted by the cleanup paths here. The two records
/// that <em>are</em> deleted (the Intune managedDevice and the Entra device
/// object) are re-created on the next enrollment; this one carries the hardware
/// hash, and losing it means the machine no longer knows it is ours.
/// </summary>
public class AutopilotDevice
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("serialNumber")]
    public string? SerialNumber { get; set; }

    [JsonPropertyName("manufacturer")]
    public string? Manufacturer { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("groupTag")]
    public string? GroupTag { get; set; }

    [JsonPropertyName("systemFamily")]
    public string? SystemFamily { get; set; }

    /// <summary>
    /// notContacted / contacted / enrolled / blocked / failed / unknown.
    /// A machine that has never completed OOBE sits at notContacted.
    /// </summary>
    [JsonPropertyName("enrollmentState")]
    public string? EnrollmentState { get; set; }

    [JsonPropertyName("lastContactedDateTime")]
    public DateTime? LastContactedDateTime { get; set; }

    /// <summary>
    /// The Entra device object this identity is bound to. After a hand-cleanup
    /// that removed only the Intune record, this still points at a live Entra
    /// object — which is exactly the stale binding that fails the next OOBE.
    /// </summary>
    [JsonPropertyName("azureActiveDirectoryDeviceId")]
    public string? AzureActiveDirectoryDeviceId { get; set; }

    /// <summary>
    /// The Intune managedDevice this identity is bound to. Frequently a dangling
    /// reference: Intune does not clear it when the managedDevice is deleted, so
    /// a 404 here is normal for a machine mid-reprovision and is reported rather
    /// than treated as an error.
    /// </summary>
    [JsonPropertyName("managedDeviceId")]
    public string? ManagedDeviceId { get; set; }

    [JsonPropertyName("userPrincipalName")]
    public string? UserPrincipalName { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }
}

/// <summary>Response for an AutoPilot device identity list</summary>
public class AutopilotDeviceListResponse
{
    [JsonPropertyName("value")]
    public List<AutopilotDevice> Value { get; set; } = new();

    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; set; }
}

/// <summary>
/// An Entra ID device object (directory object, not the Intune record).
/// </summary>
public class EntraDevice
{
    /// <summary>Directory object id — what DELETE /devices/{id} takes.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>The device id (distinct from the object id above).</summary>
    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("accountEnabled")]
    public bool? AccountEnabled { get; set; }

    /// <summary>AzureAd / ServerAd / Workplace.</summary>
    [JsonPropertyName("trustType")]
    public string? TrustType { get; set; }

    [JsonPropertyName("isCompliant")]
    public bool? IsCompliant { get; set; }

    [JsonPropertyName("isManaged")]
    public bool? IsManaged { get; set; }

    [JsonPropertyName("operatingSystem")]
    public string? OperatingSystem { get; set; }

    [JsonPropertyName("operatingSystemVersion")]
    public string? OperatingSystemVersion { get; set; }

    [JsonPropertyName("registrationDateTime")]
    public DateTime? RegistrationDateTime { get; set; }

    [JsonPropertyName("approximateLastSignInDateTime")]
    public DateTime? ApproximateLastSignInDateTime { get; set; }

    /// <summary>
    /// Carries the AutoPilot binding as a "[ZTDID]:&lt;guid&gt;" entry. This is how an
    /// orphaned object is matched back to the machine that will re-use it at OOBE.
    /// </summary>
    [JsonPropertyName("physicalIds")]
    public List<string> PhysicalIds { get; set; } = new();

    /// <summary>The AutoPilot identity id stamped into physicalIds, if present.</summary>
    public string? ZtdId => PhysicalIds
        .FirstOrDefault(p => p.StartsWith("[ZTDID]:", StringComparison.OrdinalIgnoreCase))?
        .Substring("[ZTDID]:".Length);
}

/// <summary>Response for an Entra device list</summary>
public class EntraDeviceListResponse
{
    [JsonPropertyName("value")]
    public List<EntraDevice> Value { get; set; } = new();

    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; set; }
}
