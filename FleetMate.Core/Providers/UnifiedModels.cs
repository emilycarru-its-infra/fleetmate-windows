namespace FleetMate.Core.Providers;

// ── Unified models ──────────────────────────────────────────────────────────

/// <summary>Unified device from any MDM provider.</summary>
public class UnifiedManagedDevice
{
    public required string Id { get; init; }
    public required string ProviderId { get; init; }
    public string? DeviceName { get; init; }
    public string? SerialNumber { get; init; }
    public string? OperatingSystem { get; init; }
    public string? OsVersion { get; init; }
    public string? Model { get; init; }
    public string? Manufacturer { get; init; }
    public string? UserPrincipalName { get; init; }
    public string? ComplianceState { get; init; }
    public string? ManagementState { get; init; }
    public DateTime? LastSyncDateTime { get; init; }
    public DateTime? EnrolledDateTime { get; init; }
    public string? EntraDeviceId { get; init; }
    public long? TotalStorageSpaceInBytes { get; init; }
    public long? FreeStorageSpaceInBytes { get; init; }
    public string? WiFiMacAddress { get; init; }
    public string? EthernetMacAddress { get; init; }
}

/// <summary>Unified asset from any inventory provider.</summary>
public class UnifiedAsset
{
    public required string Id { get; init; }
    public required string ProviderId { get; init; }
    public string? AssetTag { get; init; }
    public string? Name { get; init; }
    public string? Serial { get; init; }
    public string? ModelName { get; init; }
    public string? CategoryName { get; init; }
    public string? StatusLabel { get; init; }
    public string? AssignedTo { get; init; }
    public string? LocationName { get; init; }
    public DateTime? PurchaseDate { get; init; }
    public decimal? PurchaseCost { get; init; }
    public string? OrderNumber { get; init; }
    public string? Notes { get; init; }
    public Dictionary<string, string>? CustomFields { get; init; }
}

/// <summary>Unified ticket from any ticketing provider.</summary>
public class UnifiedTicket
{
    public required string Id { get; init; }
    public required string ProviderId { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? Status { get; init; }
    public string? Priority { get; init; }
    public string? Requestor { get; init; }
    public string? Responsible { get; init; }
    public string? GroupName { get; init; }
    public DateTime? CreatedDate { get; init; }
    public DateTime? ModifiedDate { get; init; }
    public DateTime? DueDate { get; init; }
    public string? TicketType { get; init; }
    public string? Source { get; init; }
}

/// <summary>Unified group from any identity provider.</summary>
public class UnifiedGroup
{
    public required string Id { get; init; }
    public required string ProviderId { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string? GroupType { get; init; }
    public int? MemberCount { get; init; }
    public string? Mail { get; init; }
}

/// <summary>Unified group member.</summary>
public class UnifiedGroupMember
{
    public required string Id { get; init; }
    public string? DisplayName { get; init; }
    public string? UserPrincipalName { get; init; }
    public string? MemberType { get; init; }
}

// ── Filters ─────────────────────────────────────────────────────────────────

public class DeviceFilter
{
    public string? OperatingSystem { get; set; }
    public string? ComplianceState { get; set; }
    public string? SearchQuery { get; set; }
    public int? Limit { get; set; }
}

public class AssetFilter
{
    public string? Status { get; set; }
    public string? Category { get; set; }
    public string? Location { get; set; }
    public string? SearchQuery { get; set; }
    public int? Limit { get; set; }
}

public class TicketFilter
{
    public string? Status { get; set; }
    public string? Priority { get; set; }
    public string? Responsible { get; set; }
    public string? GroupName { get; set; }
    public string? SearchQuery { get; set; }
    public int? Limit { get; set; }
}
