using System.Text.Json;
using System.Text.Json.Serialization;

namespace FleetMate.Core.Models.Devices;

/// <summary>
/// An Apple School Manager or Apple Business Manager API profile. The secret
/// half (key ID and private key) lives in Windows Credential Manager; this is
/// what the rest of the app may see.
/// </summary>
public sealed record AppleOrgProfile(string Name, string ClientId)
{
    /// <summary>School Manager credentials carry a SCHOOLAPI client ID; everything else is Business Manager.</summary>
    public bool IsSchool => ClientId.StartsWith("SCHOOLAPI", StringComparison.OrdinalIgnoreCase);
    public string ServiceName => IsSchool ? "Apple School Manager" : "Apple Business Manager";
    public string Scope => IsSchool ? "school.api" : "business.api";
    public string ApiHost => IsSchool ? "api-school.apple.com" : "api-business.apple.com";

    /// <summary>
    /// What to call each organization: its service, with the profile name
    /// added only when two profiles are the same kind, so a bare profile name
    /// such as "default" never shows on its own.
    /// </summary>
    public static Dictionary<string, string> Labels(IReadOnlyCollection<AppleOrgProfile> profiles) =>
        profiles.ToDictionary(p => p.Name, p =>
            profiles.Count(o => o.IsSchool == p.IsSchool) > 1 ? $"{p.ServiceName} ({p.Name})" : p.ServiceName);
}

/// <summary>A device management service (an MDM server entry) in an Apple organization.</summary>
public sealed record AppleOrgServer(string Id, string OrgId, string Name, string? Type, int? DeviceCount = null);

/// <summary>One device as the Apple organization reports it.</summary>
public sealed record AppleOrgDevice
{
    public required string SerialNumber { get; init; }
    /// <summary>The profile name of the organization that holds the device.</summary>
    public string OrgId { get; init; } = "";
    public string Model { get; init; } = "Unknown";
    public string? ProductFamily { get; init; }
    /// <summary>ASSIGNED or UNASSIGNED.</summary>
    public string? Status { get; init; }
    public string? AssignedServerId { get; init; }
    public string? OrderNumber { get; init; }
    /// <summary>APPLE, RESELLER or MANUALLY_ADDED.</summary>
    public string? PurchaseSource { get; init; }
    public DateTimeOffset? AddedToOrg { get; init; }
    public DateTimeOffset? OrderDate { get; init; }
    public bool? IsMigrationCapable { get; init; }
    /// <summary>REQUESTED, STARTED, SUCCESS or FAILED.</summary>
    public string? MigrationStatus { get; init; }
    public DateTimeOffset? MigrationDeadline { get; init; }
    public DateTimeOffset? ReleasedFromOrg { get; init; }
    public IReadOnlyList<string> WifiMacAddresses { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> EthernetMacAddresses { get; init; } = Array.Empty<string>();

    /// <summary>A migration Apple has not finished yet.</summary>
    public bool HasActiveMigration => MigrationStatus?.ToUpperInvariant() is "REQUESTED" or "STARTED";
}

/// <summary>One AppleCare or warranty agreement on a device.</summary>
public sealed record AppleCareAgreement(string Description, string? Status, DateTimeOffset? Start, DateTimeOffset? End,
    string? AgreementNumber, string? PaymentType, bool IsCanceled);

public enum AppleOrgActionKind { Assign, Unassign, ScheduleMigration, UpdateMigrationDeadline, CancelMigration, Release }

/// <summary>What can be asked of an Apple organization for a set of devices.</summary>
public sealed record AppleOrgAction(AppleOrgActionKind Kind, string? ServerId = null, DateTimeOffset? Deadline = null)
{
    /// <summary>Apple caps a migration deadline at 90 days out.</summary>
    public const int MaxMigrationDays = 90;

    public string Title => Kind switch
    {
        AppleOrgActionKind.Assign => "Assign",
        AppleOrgActionKind.Unassign => "Unassign",
        AppleOrgActionKind.ScheduleMigration => "Schedule Migration",
        AppleOrgActionKind.UpdateMigrationDeadline => "Change Migration Deadline",
        AppleOrgActionKind.CancelMigration => "Cancel Migration",
        _ => "Release from Organization",
    };

    /// <summary>Apple Business only, and irreversible.</summary>
    public bool IsBusinessOnly => Kind == AppleOrgActionKind.Release;

    /// <summary>The activity type Apple's orgDeviceActivities endpoint takes.</summary>
    public string ActivityType => Kind switch
    {
        AppleOrgActionKind.Assign => "ASSIGN_DEVICES",
        AppleOrgActionKind.Unassign => "UNASSIGN_DEVICES",
        AppleOrgActionKind.ScheduleMigration => "ASSIGN_DEVICES_WITH_MDM_MIGRATION_DEADLINE",
        AppleOrgActionKind.UpdateMigrationDeadline => "UPDATE_MDM_MIGRATION_DEADLINE",
        AppleOrgActionKind.CancelMigration => "CANCEL_MDM_MIGRATION",
        _ => "RELEASE_DEVICES",
    };

    public bool RequiresServer => Kind is AppleOrgActionKind.Assign or AppleOrgActionKind.Unassign or AppleOrgActionKind.ScheduleMigration;
    public bool RequiresDeadline => Kind is AppleOrgActionKind.ScheduleMigration or AppleOrgActionKind.UpdateMigrationDeadline;

    public static DateTimeOffset LatestDeadline(DateTimeOffset now) => now.AddDays(MaxMigrationDays);
}

/// <summary>How an Apple organization activity ended.</summary>
public sealed record AppleOrgActivityResult(string ActivityId, string Status, IReadOnlyList<string> Serials)
{
    public bool Succeeded => Status.ToUpperInvariant() is "COMPLETED" or "COMPLETE";
}

/// <summary>Everything read from one organization.</summary>
public sealed record AppleOrgSnapshot(AppleOrgProfile Profile, IReadOnlyList<AppleOrgDevice> Devices, IReadOnlyList<AppleOrgServer> Servers);

/// <summary>Labels the Devices list shows for an Apple record.</summary>
public static class AppleOrgLabels
{
    public static string OrgStatusLabel(this AppleOrgDevice d)
    {
        if (d.ReleasedFromOrg != null) return "Released";
        return d.Status?.ToUpperInvariant() switch
        {
            "ASSIGNED" => "Assigned",
            "UNASSIGNED" => "Unassigned",
            null or "" => "Unknown",
            var s => Capitalize(s.ToLowerInvariant()),
        };
    }

    public static string PurchaseSourceLabel(this AppleOrgDevice d) => d.PurchaseSource?.ToUpperInvariant() switch
    {
        "APPLE" => "Apple",
        "RESELLER" => "Reseller",
        "MANUALLY_ADDED" => "Manually Added",
        null or "" => DeviceListRow.Missing,
        var s => Capitalize(s.ToLowerInvariant()),
    };

    public static string MigrationLabel(this AppleOrgDevice d) => d.MigrationStatus?.ToUpperInvariant() switch
    {
        null or "" => "None",
        "REQUESTED" => "Requested",
        "STARTED" => "In Progress",
        "SUCCESS" => "Migrated",
        "FAILED" => "Failed",
        var s => Capitalize(s.ToLowerInvariant()),
    };

    /// <summary>The platform an Apple product family implies, so a Platform filter keeps an organization-only row.</summary>
    public static string? PlatformFromFamily(string? family) => family?.ToLowerInvariant() switch
    {
        null or "" => null,
        "mac" => "macOS",
        "ipad" => "iPadOS",
        "iphone" => "iOS",
        "appletv" => "tvOS",
        "vision" => "visionOS",
        _ => family,
    };

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

// ── Apple API wire shapes ────────────────────────────────────────────────

/// <summary>A value Apple sends either as one string or as an array of them.</summary>
public sealed class StringOrArrayConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return new() { reader.GetString() ?? "" };
        if (reader.TokenType == JsonTokenType.Null) return new();
        var list = new List<string>();
        using var doc = JsonDocument.ParseValue(ref reader);
        foreach (var e in doc.RootElement.EnumerateArray())
            if (e.ValueKind == JsonValueKind.String) list.Add(e.GetString()!);
        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}

public sealed class AppleDeviceAttributes
{
    [JsonPropertyName("serialNumber")] public string SerialNumber { get; set; } = "";
    [JsonPropertyName("deviceModel")] public string? DeviceModel { get; set; }
    [JsonPropertyName("model")] public string? LegacyModel { get; set; }
    [JsonPropertyName("productFamily")] public string? ProductFamily { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("orderNumber")] public string? OrderNumber { get; set; }
    [JsonPropertyName("orderDateTime")] public string? OrderDateTime { get; set; }
    [JsonPropertyName("purchaseSourceType")] public string? PurchaseSourceType { get; set; }
    [JsonPropertyName("addedToOrgDateTime")] public string? AddedToOrgDateTime { get; set; }
    [JsonPropertyName("deviceManagementServiceId")] public string? DeviceManagementServiceId { get; set; }
    [JsonPropertyName("isMdmMigrationCapable")] public bool? IsMdmMigrationCapable { get; set; }
    [JsonPropertyName("mdmMigrationStatus")] public string? MdmMigrationStatus { get; set; }
    [JsonPropertyName("mdmMigrationDeadlineDateTime")] public string? MdmMigrationDeadlineDateTime { get; set; }
    [JsonPropertyName("releasedFromOrgDateTime")] public string? ReleasedFromOrgDateTime { get; set; }
    [JsonPropertyName("wifiMacAddress"), JsonConverter(typeof(StringOrArrayConverter))] public List<string>? WifiMacAddress { get; set; }
    [JsonPropertyName("builtInEthernetMacAddress"), JsonConverter(typeof(StringOrArrayConverter))] public List<string>? BuiltInEthernetMacAddress { get; set; }
}

public sealed class AppleResource<T>
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("attributes")] public T? Attributes { get; set; }
}

public sealed class AppleListResponse<T>
{
    [JsonPropertyName("data")] public List<AppleResource<T>> Data { get; set; } = new();
    [JsonPropertyName("meta")] public AppleMeta? Meta { get; set; }
    [JsonPropertyName("links")] public AppleLinks? Links { get; set; }
}

public sealed class AppleSingleResponse<T>
{
    [JsonPropertyName("data")] public AppleResource<T>? Data { get; set; }
}

public sealed class AppleMeta
{
    [JsonPropertyName("paging")] public ApplePaging? Paging { get; set; }
}

public sealed class ApplePaging
{
    [JsonPropertyName("nextCursor")] public string? NextCursor { get; set; }
}

public sealed class AppleLinks
{
    [JsonPropertyName("next")] public string? Next { get; set; }
}

public sealed class AppleServerAttributes
{
    [JsonPropertyName("serverName")] public string? ServerName { get; set; }
    [JsonPropertyName("serverType")] public string? ServerType { get; set; }
}

public sealed class AppleCareAttributes
{
    [JsonPropertyName("agreementNumber")] public string? AgreementNumber { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("startDateTime")] public string? StartDateTime { get; set; }
    [JsonPropertyName("endDateTime")] public string? EndDateTime { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("paymentType")] public string? PaymentType { get; set; }
    [JsonPropertyName("isCanceled")] public bool? IsCanceled { get; set; }
}

public sealed class AppleActivityAttributes
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("activityType")] public string? ActivityType { get; set; }
}
