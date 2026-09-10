using System.Text.Json.Serialization;

namespace FleetMate.Core.Models.Inventory;

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
    
    /// <summary>
    /// End of life period (e.g., "48 months" - returned as string, not object)
    /// </summary>
    [JsonPropertyName("eol")]
    public string? Eol { get; set; }
    
    /// <summary>
    /// Computed asset end of life date
    /// </summary>
    [JsonPropertyName("asset_eol_date")]
    public SnipeDate? AssetEolDate { get; set; }
    
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
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? WarrantyMonths { get; set; }

    [JsonPropertyName("warranty_expires")]
    public SnipeDate? WarrantyExpires { get; set; }

    [JsonPropertyName("created_at")]
    public SnipeDateTime? CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public SnipeDateTime? UpdatedAt { get; set; }

    [JsonPropertyName("last_audit_date")]
    public SnipeDateTime? LastAuditDate { get; set; }

    [JsonPropertyName("next_audit_date")]
    public SnipeDateTime? NextAuditDate { get; set; }
    
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

    [JsonPropertyName("book_value")]
    public string? BookValue { get; set; }

    [JsonPropertyName("byod")]
    public bool? Byod { get; set; }

    [JsonPropertyName("requestable")]
    public bool? Requestable { get; set; }

    [JsonPropertyName("decommission_date")]
    public SnipeDate? DecommissionDate { get; set; }

    // Native lease / purchasing columns. The ECU fork's F2 migration promoted
    // this cluster out of _snipeit_* custom fields into typed assets columns
    // and then dropped the custom fields, so these no longer arrive in
    // custom_fields — they are first-class keys on the asset payload.

    [JsonPropertyName("lease_contract_id")]
    public string? LeaseContractId { get; set; }

    [JsonPropertyName("lease_contract_name")]
    public string? LeaseContractName { get; set; }

    [JsonPropertyName("ownership_type")]
    public string? OwnershipType { get; set; }

    [JsonPropertyName("lease_end_date")]
    public SnipeDate? LeaseEndDate { get; set; }

    [JsonPropertyName("lease_rent")]
    public SnipeMoney? LeaseRent { get; set; }

    [JsonPropertyName("buyout_cost")]
    public SnipeMoney? BuyoutCost { get; set; }

    [JsonPropertyName("warranty_soft_cost")]
    public SnipeMoney? WarrantySoftCost { get; set; }

    [JsonPropertyName("lease_book_value")]
    public SnipeMoney? LeaseBookValue { get; set; }

    [JsonPropertyName("po_number")]
    public string? PoNumber { get; set; }

    [JsonPropertyName("invoice_number")]
    public string? InvoiceNumber { get; set; }

    [JsonPropertyName("lease_usage")]
    public string? LeaseUsage { get; set; }

    [JsonPropertyName("lease_area")]
    public string? LeaseArea { get; set; }

    /// <summary>
    /// Display name for the asset, HTML entities decoded (Snipe stores
    /// "24&amp;quot; UltraSharp" style names).
    /// </summary>
    [JsonIgnore]
    public string DisplayName =>
        System.Net.WebUtility.HtmlDecode(!string.IsNullOrEmpty(Name) ? Name : AssetTag);

    /// <summary>Location name with HTML entities decoded.</summary>
    [JsonIgnore]
    public string? LocationName =>
        Location?.Name is { Length: > 0 } n ? System.Net.WebUtility.HtmlDecode(n) : null;

    /// <summary>
    /// Custom field value by display name — the dictionary KEY is the display
    /// name ("Platform"); the entry's Field property is the DB column
    /// ("_snipeit_platform_11").
    /// </summary>
    public string? CustomFieldByName(string displayName) =>
        CustomFields != null && CustomFields.TryGetValue(displayName, out var field)
            ? field.Value
            : null;

    /// <summary>
    /// Usage and Area read native first, falling back to the legacy custom
    /// fields for any instance that predates the fork's F2 migration.
    /// </summary>
    [JsonIgnore]
    public string? Usage =>
        !string.IsNullOrEmpty(LeaseUsage) ? LeaseUsage : CustomFieldByName("Usage");

    [JsonIgnore]
    public string? Area =>
        !string.IsNullOrEmpty(LeaseArea) ? LeaseArea : CustomFieldByName("Area");

    [JsonIgnore]
    public string? Platform => CustomFieldByName("Platform");

    [JsonIgnore]
    public string? Catalog => CustomFieldByName("Catalog");

    /// <summary>
    /// The most recent thing that happened to this asset — whichever of the
    /// edit, checkout and audit timestamps is latest. Snipe-IT writes them all
    /// as "yyyy-MM-dd HH:mm:ss", so the raw strings compare chronologically.
    /// </summary>
    [JsonIgnore]
    public SnipeDateTime? LastActivity
    {
        get
        {
            SnipeDateTime? latest = null;
            foreach (var candidate in new[] { UpdatedAt, LastCheckout, LastAuditDate })
            {
                if (string.IsNullOrEmpty(candidate?.DateTime)) continue;
                if (latest == null || string.CompareOrdinal(candidate!.DateTime, latest.DateTime) > 0)
                    latest = candidate;
            }
            return latest;
        }
    }

    [JsonIgnore]
    public string LastActivityFormatted => LastActivity?.Formatted ?? "";
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
