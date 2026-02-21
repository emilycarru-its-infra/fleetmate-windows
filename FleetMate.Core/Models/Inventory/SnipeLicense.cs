using System.Text.Json.Serialization;

namespace FleetMate.Core.Models.Inventory;

/// <summary>
/// Software license from Snipe-IT API
/// </summary>
public class SnipeLicense
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("company")]
    public SnipeRef? Company { get; set; }
    
    [JsonPropertyName("manufacturer")]
    public SnipeRef? Manufacturer { get; set; }
    
    [JsonPropertyName("product_key")]
    public string? ProductKey { get; set; }
    
    [JsonPropertyName("order_number")]
    public string? OrderNumber { get; set; }
    
    [JsonPropertyName("purchase_order")]
    public string? PurchaseOrder { get; set; }
    
    [JsonPropertyName("purchase_date")]
    public SnipeDate? PurchaseDate { get; set; }
    
    [JsonPropertyName("purchase_cost")]
    public string? PurchaseCost { get; set; }
    
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
    
    [JsonPropertyName("expiration_date")]
    public SnipeDate? ExpirationDate { get; set; }
    
    [JsonPropertyName("seats")]
    public int Seats { get; set; }
    
    [JsonPropertyName("free_seats_count")]
    public int FreeSeatsCount { get; set; }
    
    [JsonPropertyName("license_name")]
    public string? LicenseName { get; set; }
    
    [JsonPropertyName("license_email")]
    public string? LicenseEmail { get; set; }
    
    [JsonPropertyName("maintained")]
    public bool Maintained { get; set; }
    
    [JsonPropertyName("reassignable")]
    public bool Reassignable { get; set; }
    
    [JsonPropertyName("supplier")]
    public SnipeRef? Supplier { get; set; }
    
    [JsonPropertyName("category")]
    public SnipeRef? Category { get; set; }
    
    [JsonPropertyName("created_at")]
    public SnipeDateTime? CreatedAt { get; set; }
    
    [JsonPropertyName("updated_at")]
    public SnipeDateTime? UpdatedAt { get; set; }
    
    [JsonPropertyName("deleted_at")]
    public SnipeDateTime? DeletedAt { get; set; }
    
    [JsonPropertyName("user_can_checkout")]
    public bool UserCanCheckout { get; set; }
    
    [JsonPropertyName("available_actions")]
    public SnipeActions? AvailableActions { get; set; }
}

/// <summary>
/// License seat from Snipe-IT API
/// </summary>
public class SnipeLicenseSeat
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("license_id")]
    public int LicenseId { get; set; }
    
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    
    [JsonPropertyName("assigned_user")]
    public SnipeAssignee? AssignedUser { get; set; }
    
    [JsonPropertyName("assigned_asset")]
    public SnipeRef? AssignedAsset { get; set; }
    
    [JsonPropertyName("location")]
    public SnipeRef? Location { get; set; }
    
    [JsonPropertyName("reassignable")]
    public bool Reassignable { get; set; }
    
    [JsonPropertyName("user_can_checkout")]
    public bool UserCanCheckout { get; set; }
    
    [JsonPropertyName("available_actions")]
    public SnipeActions? AvailableActions { get; set; }
}

/// <summary>
/// Request model for creating/updating licenses
/// </summary>
public class SnipeLicenseRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("seats")]
    public int Seats { get; set; }
    
    [JsonPropertyName("category_id")]
    public int CategoryId { get; set; }
    
    [JsonPropertyName("company_id")]
    public int? CompanyId { get; set; }
    
    [JsonPropertyName("expiration_date")]
    public string? ExpirationDate { get; set; }
    
    [JsonPropertyName("license_email")]
    public string? LicenseEmail { get; set; }
    
    [JsonPropertyName("license_name")]
    public string? LicenseName { get; set; }
    
    [JsonPropertyName("serial")]
    public string? Serial { get; set; }
    
    [JsonPropertyName("maintained")]
    public bool Maintained { get; set; }
    
    [JsonPropertyName("manufacturer_id")]
    public int? ManufacturerId { get; set; }
    
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
    
    [JsonPropertyName("order_number")]
    public string? OrderNumber { get; set; }
    
    [JsonPropertyName("purchase_cost")]
    public decimal? PurchaseCost { get; set; }
    
    [JsonPropertyName("purchase_date")]
    public string? PurchaseDate { get; set; }
    
    [JsonPropertyName("purchase_order")]
    public string? PurchaseOrder { get; set; }
    
    [JsonPropertyName("reassignable")]
    public bool Reassignable { get; set; } = true;
    
    [JsonPropertyName("supplier_id")]
    public int? SupplierId { get; set; }
    
    [JsonPropertyName("termination_date")]
    public string? TerminationDate { get; set; }
}

/// <summary>
/// Request for updating a license seat assignment
/// </summary>
public class SnipeLicenseSeatRequest
{
    [JsonPropertyName("assigned_to")]
    public int? AssignedTo { get; set; }
    
    [JsonPropertyName("asset_id")]
    public int? AssetId { get; set; }
    
    [JsonPropertyName("note")]
    public string? Note { get; set; }
}
