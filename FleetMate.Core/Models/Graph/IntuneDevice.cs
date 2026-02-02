using System.Text.Json.Serialization;

namespace FleetMate.Models.Graph;

/// <summary>
/// Intune managed device from Microsoft Graph
/// </summary>
public class IntuneDevice
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("serialNumber")]
    public string? SerialNumber { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("manufacturer")]
    public string? Manufacturer { get; set; }

    [JsonPropertyName("operatingSystem")]
    public string? OperatingSystem { get; set; }

    [JsonPropertyName("osVersion")]
    public string? OsVersion { get; set; }

    [JsonPropertyName("complianceState")]
    public string? ComplianceState { get; set; }

    [JsonPropertyName("managementState")]
    public string? ManagementState { get; set; }

    [JsonPropertyName("enrolledDateTime")]
    public DateTime? EnrolledDateTime { get; set; }

    [JsonPropertyName("lastSyncDateTime")]
    public DateTime? LastSyncDateTime { get; set; }

    [JsonPropertyName("userPrincipalName")]
    public string? UserPrincipalName { get; set; }

    [JsonPropertyName("userDisplayName")]
    public string? UserDisplayName { get; set; }

    [JsonPropertyName("emailAddress")]
    public string? EmailAddress { get; set; }

    [JsonPropertyName("azureADDeviceId")]
    public string? AzureAdDeviceId { get; set; }

    [JsonPropertyName("deviceEnrollmentType")]
    public string? DeviceEnrollmentType { get; set; }

    [JsonPropertyName("isEncrypted")]
    public bool? IsEncrypted { get; set; }

    [JsonPropertyName("isSupervised")]
    public bool? IsSupervised { get; set; }

    [JsonPropertyName("jailBroken")]
    public string? JailBroken { get; set; }

    [JsonPropertyName("managementAgent")]
    public string? ManagementAgent { get; set; }

    [JsonPropertyName("deviceCategoryDisplayName")]
    public string? DeviceCategoryDisplayName { get; set; }

    [JsonPropertyName("freeStorageSpaceInBytes")]
    public long? FreeStorageSpaceInBytes { get; set; }

    [JsonPropertyName("totalStorageSpaceInBytes")]
    public long? TotalStorageSpaceInBytes { get; set; }

    [JsonPropertyName("wiFiMacAddress")]
    public string? WiFiMacAddress { get; set; }

    [JsonPropertyName("ethernetMacAddress")]
    public string? EthernetMacAddress { get; set; }

    /// <summary>
    /// Helper to check if device is compliant
    /// </summary>
    [JsonIgnore]
    public bool IsCompliant => ComplianceState?.Equals("compliant", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Helper to get storage usage percentage
    /// </summary>
    [JsonIgnore]
    public double? StorageUsedPercent
    {
        get
        {
            if (TotalStorageSpaceInBytes.HasValue && TotalStorageSpaceInBytes > 0 && FreeStorageSpaceInBytes.HasValue)
            {
                var used = TotalStorageSpaceInBytes.Value - FreeStorageSpaceInBytes.Value;
                return Math.Round((double)used / TotalStorageSpaceInBytes.Value * 100, 1);
            }
            return null;
        }
    }
}

/// <summary>
/// Graph API response for device list
/// </summary>
public class IntuneDeviceListResponse
{
    [JsonPropertyName("value")]
    public List<IntuneDevice> Value { get; set; } = new();

    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; set; }

    [JsonPropertyName("@odata.count")]
    public int? Count { get; set; }
}

/// <summary>
/// Device compliance policy state
/// </summary>
public class DeviceCompliancePolicyState
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("settingCount")]
    public int? SettingCount { get; set; }

    [JsonPropertyName("userPrincipalName")]
    public string? UserPrincipalName { get; set; }
}

/// <summary>
/// Response for compliance policy states
/// </summary>
public class CompliancePolicyStatesResponse
{
    [JsonPropertyName("value")]
    public List<DeviceCompliancePolicyState> Value { get; set; } = new();
}

/// <summary>
/// Mobile app from Intune
/// </summary>
public class MobileApp
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("publisher")]
    public string? Publisher { get; set; }

    [JsonPropertyName("createdDateTime")]
    public DateTime? CreatedDateTime { get; set; }

    [JsonPropertyName("lastModifiedDateTime")]
    public DateTime? LastModifiedDateTime { get; set; }

    [JsonPropertyName("isFeatured")]
    public bool? IsFeatured { get; set; }

    [JsonPropertyName("privacyInformationUrl")]
    public string? PrivacyInformationUrl { get; set; }

    [JsonPropertyName("informationUrl")]
    public string? InformationUrl { get; set; }

    [JsonPropertyName("owner")]
    public string? Owner { get; set; }

    [JsonPropertyName("developer")]
    public string? Developer { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("publishingState")]
    public string? PublishingState { get; set; }

    [JsonPropertyName("@odata.type")]
    public string? OdataType { get; set; }

    /// <summary>
    /// Check if this is a Windows Win32 app (.intunewin)
    /// </summary>
    [JsonIgnore]
    public bool IsWin32App => OdataType?.Contains("win32LobApp") == true;

    /// <summary>
    /// Check if this is a macOS app (.pkg/.dmg)
    /// </summary>
    [JsonIgnore]
    public bool IsMacOSApp => OdataType?.Contains("macOS") == true;
}

/// <summary>
/// Response for mobile apps list
/// </summary>
public class MobileAppsResponse
{
    [JsonPropertyName("value")]
    public List<MobileApp> Value { get; set; } = new();

    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; set; }
}

/// <summary>
/// Detected app on a device
/// </summary>
public class DetectedApp
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("sizeInByte")]
    public long? SizeInByte { get; set; }

    [JsonPropertyName("deviceCount")]
    public int? DeviceCount { get; set; }

    [JsonPropertyName("publisher")]
    public string? Publisher { get; set; }

    [JsonPropertyName("platform")]
    public string? Platform { get; set; }
}

/// <summary>
/// Response for detected apps list
/// </summary>
public class DetectedAppsResponse
{
    [JsonPropertyName("value")]
    public List<DetectedApp> Value { get; set; } = new();

    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; set; }
}
