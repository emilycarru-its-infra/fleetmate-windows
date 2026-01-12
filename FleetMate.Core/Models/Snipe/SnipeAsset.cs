using System.Text.Json.Serialization;

namespace FleetMate.Models.Snipe;

/// <summary>
/// Hardware asset from Snipe-IT API
/// </summary>
public class SnipeAsset
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    
    [JsonPropertyName("asset_tag")]
    public string AssetTag { get; set; } = string.Empty;
    
    [JsonPropertyName("serial")]
    public string? Serial { get; set; }
    
    [JsonPropertyName("model")]
    public SnipeRef? Model { get; set; }
    
    [JsonPropertyName("model_number")]
    public string? ModelNumber { get; set; }
    
    [JsonPropertyName("eol")]
    public SnipeDate? Eol { get; set; }
    
    [JsonPropertyName("status_label")]
    public SnipeStatusLabel? StatusLabel { get; set; }
    
    [JsonPropertyName("category")]
    public SnipeRef? Category { get; set; }
    
    [JsonPropertyName("manufacturer")]
    public SnipeRef? Manufacturer { get; set; }
    
    [JsonPropertyName("supplier")]
    public SnipeRef? Supplier { get; set; }
    
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
    
    [JsonPropertyName("order_number")]
    public string? OrderNumber { get; set; }
    
    [JsonPropertyName("company")]
    public SnipeRef? Company { get; set; }
    
    [JsonPropertyName("location")]
    public SnipeRef? Location { get; set; }
    
    [JsonPropertyName("rtd_location")]
    public SnipeRef? RtdLocation { get; set; }
    
    [JsonPropertyName("image")]
    public string? Image { get; set; }
    
    [JsonPropertyName("qr")]
    public string? Qr { get; set; }
    
    [JsonPropertyName("alt_barcode")]
    public string? AltBarcode { get; set; }
    
    [JsonPropertyName("assigned_to")]
    public SnipeAssignee? AssignedTo { get; set; }
    
    [JsonPropertyName("warranty_months")]
    public int? WarrantyMonths { get; set; }
    
    [JsonPropertyName("warranty_expires")]
    public SnipeDate? WarrantyExpires { get; set; }
    
    [JsonPropertyName("created_at")]
    public SnipeDateTime? CreatedAt { get; set; }
    
    [JsonPropertyName("updated_at")]
    public SnipeDateTime? UpdatedAt { get; set; }
    
    [JsonPropertyName("last_audit_date")]
    public string? LastAuditDate { get; set; }
    
    [JsonPropertyName("next_audit_date")]
    public string? NextAuditDate { get; set; }
    
    [JsonPropertyName("deleted_at")]
    public SnipeDateTime? DeletedAt { get; set; }
    
    [JsonPropertyName("purchase_date")]
    public SnipeDate? PurchaseDate { get; set; }
    
    [JsonPropertyName("age")]
    public string? Age { get; set; }
    
    [JsonPropertyName("last_checkout")]
    public SnipeDateTime? LastCheckout { get; set; }
    
    [JsonPropertyName("expected_checkin")]
    public SnipeDate? ExpectedCheckin { get; set; }
    
    [JsonPropertyName("purchase_cost")]
    public string? PurchaseCost { get; set; }
    
    [JsonPropertyName("checkin_counter")]
    public int CheckinCounter { get; set; }
    
    [JsonPropertyName("checkout_counter")]
    public int CheckoutCounter { get; set; }
    
    [JsonPropertyName("requests_counter")]
    public int RequestsCounter { get; set; }
    
    [JsonPropertyName("user_can_checkout")]
    public bool UserCanCheckout { get; set; }
    
    [JsonPropertyName("custom_fields")]
    public Dictionary<string, SnipeCustomField>? CustomFields { get; set; }
    
    [JsonPropertyName("available_actions")]
    public SnipeActions? AvailableActions { get; set; }
    
    /// <summary>
    /// Display name for the asset
    /// </summary>
    [JsonIgnore]
    public string DisplayName => !string.IsNullOrEmpty(Name) ? Name : AssetTag;
}

/// <summary>
/// Request model for creating/updating assets
/// </summary>
public class SnipeAssetRequest
{
    [JsonPropertyName("asset_tag")]
    public string AssetTag { get; set; } = string.Empty;
    
    [JsonPropertyName("status_id")]
    public int StatusId { get; set; }
    
    [JsonPropertyName("model_id")]
    public int ModelId { get; set; }
    
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    
    [JsonPropertyName("serial")]
    public string? Serial { get; set; }
    
    [JsonPropertyName("purchase_date")]
    public string? PurchaseDate { get; set; }
    
    [JsonPropertyName("purchase_cost")]
    public decimal? PurchaseCost { get; set; }
    
    [JsonPropertyName("order_number")]
    public string? OrderNumber { get; set; }
    
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
    
    [JsonPropertyName("archived")]
    public bool Archived { get; set; }
    
    [JsonPropertyName("warranty_months")]
    public int? WarrantyMonths { get; set; }
    
    [JsonPropertyName("supplier_id")]
    public int? SupplierId { get; set; }
    
    [JsonPropertyName("requestable")]
    public bool Requestable { get; set; }
    
    [JsonPropertyName("rtd_location_id")]
    public int? RtdLocationId { get; set; }
    
    [JsonPropertyName("location_id")]
    public int? LocationId { get; set; }
    
    [JsonPropertyName("company_id")]
    public int? CompanyId { get; set; }
}

/// <summary>
/// Request model for checking out an asset
/// </summary>
public class SnipeCheckoutRequest
{
    [JsonPropertyName("status_id")]
    public int StatusId { get; set; }
    
    [JsonPropertyName("checkout_to_type")]
    public string CheckoutToType { get; set; } = "user"; // user, asset, location
    
    [JsonPropertyName("assigned_user")]
    public int? AssignedUser { get; set; }
    
    [JsonPropertyName("assigned_asset")]
    public int? AssignedAsset { get; set; }
    
    [JsonPropertyName("assigned_location")]
    public int? AssignedLocation { get; set; }
    
    [JsonPropertyName("expected_checkin")]
    public string? ExpectedCheckin { get; set; }
    
    [JsonPropertyName("checkout_at")]
    public string? CheckoutAt { get; set; }
    
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    
    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

/// <summary>
/// Request model for checking in an asset
/// </summary>
public class SnipeCheckinRequest
{
    [JsonPropertyName("status_id")]
    public int? StatusId { get; set; }
    
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    
    [JsonPropertyName("note")]
    public string? Note { get; set; }
    
    [JsonPropertyName("location_id")]
    public int? LocationId { get; set; }
}

/// <summary>
/// Request model for auditing an asset
/// </summary>
public class SnipeAuditRequest
{
    [JsonPropertyName("location_id")]
    public int? LocationId { get; set; }
    
    [JsonPropertyName("note")]
    public string? Note { get; set; }
    
    [JsonPropertyName("update_location")]
    public bool UpdateLocation { get; set; }
}
