using System.Text.Json.Serialization;

namespace FleetMate.Core.Models.Inventory;

/// <summary>
/// Accessory from Snipe-IT API
/// </summary>
public class SnipeAccessory
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("image")]
    public string? Image { get; set; }
    
    [JsonPropertyName("company")]
    public SnipeRef? Company { get; set; }
    
    [JsonPropertyName("manufacturer")]
    public SnipeRef? Manufacturer { get; set; }
    
    [JsonPropertyName("supplier")]
    public SnipeRef? Supplier { get; set; }
    
    [JsonPropertyName("model_number")]
    public string? ModelNumber { get; set; }
    
    [JsonPropertyName("category")]
    public SnipeRef? Category { get; set; }
    
    [JsonPropertyName("location")]
    public SnipeRef? Location { get; set; }
    
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
    
    [JsonPropertyName("qty")]
    public int Qty { get; set; }
    
    [JsonPropertyName("purchase_date")]
    public SnipeDate? PurchaseDate { get; set; }
    
    [JsonPropertyName("purchase_cost")]
    public string? PurchaseCost { get; set; }
    
    [JsonPropertyName("order_number")]
    public string? OrderNumber { get; set; }
    
    [JsonPropertyName("min_qty")]
    public int MinQty { get; set; }
    
    [JsonPropertyName("remaining_qty")]
    public int RemainingQty { get; set; }
    
    [JsonPropertyName("created_at")]
    public SnipeDateTime? CreatedAt { get; set; }
    
    [JsonPropertyName("updated_at")]
    public SnipeDateTime? UpdatedAt { get; set; }
    
    [JsonPropertyName("user_can_checkout")]
    public bool UserCanCheckout { get; set; }
    
    [JsonPropertyName("available_actions")]
    public SnipeActions? AvailableActions { get; set; }
}

/// <summary>
/// Consumable from Snipe-IT API
/// </summary>
public class SnipeConsumable
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("image")]
    public string? Image { get; set; }
    
    [JsonPropertyName("category")]
    public SnipeRef? Category { get; set; }
    
    [JsonPropertyName("company")]
    public SnipeRef? Company { get; set; }
    
    [JsonPropertyName("item_no")]
    public string? ItemNo { get; set; }
    
    [JsonPropertyName("location")]
    public SnipeRef? Location { get; set; }
    
    [JsonPropertyName("manufacturer")]
    public SnipeRef? Manufacturer { get; set; }
    
    [JsonPropertyName("supplier")]
    public SnipeRef? Supplier { get; set; }
    
    [JsonPropertyName("min_amt")]
    public int MinAmt { get; set; }
    
    [JsonPropertyName("model_number")]
    public string? ModelNumber { get; set; }
    
    [JsonPropertyName("remaining")]
    public int Remaining { get; set; }
    
    [JsonPropertyName("order_number")]
    public string? OrderNumber { get; set; }
    
    [JsonPropertyName("purchase_cost")]
    public string? PurchaseCost { get; set; }
    
    [JsonPropertyName("purchase_date")]
    public SnipeDate? PurchaseDate { get; set; }
    
    [JsonPropertyName("qty")]
    public int Qty { get; set; }
    
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
    
    [JsonPropertyName("created_at")]
    public SnipeDateTime? CreatedAt { get; set; }
    
    [JsonPropertyName("updated_at")]
    public SnipeDateTime? UpdatedAt { get; set; }
    
    [JsonPropertyName("user_can_checkout")]
    public bool UserCanCheckout { get; set; }
    
    [JsonPropertyName("available_actions")]
    public SnipeActions? AvailableActions { get; set; }
}

/// <summary>
/// Component from Snipe-IT API
/// </summary>
public class SnipeComponent
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("image")]
    public string? Image { get; set; }
    
    [JsonPropertyName("serial")]
    public string? Serial { get; set; }
    
    [JsonPropertyName("location")]
    public SnipeRef? Location { get; set; }
    
    [JsonPropertyName("qty")]
    public int Qty { get; set; }
    
    [JsonPropertyName("min_amt")]
    public int MinAmt { get; set; }
    
    [JsonPropertyName("category")]
    public SnipeRef? Category { get; set; }
    
    [JsonPropertyName("company")]
    public SnipeRef? Company { get; set; }
    
    [JsonPropertyName("order_number")]
    public string? OrderNumber { get; set; }
    
    [JsonPropertyName("purchase_date")]
    public SnipeDate? PurchaseDate { get; set; }
    
    [JsonPropertyName("purchase_cost")]
    public string? PurchaseCost { get; set; }
    
    [JsonPropertyName("remaining")]
    public int Remaining { get; set; }
    
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
    
    [JsonPropertyName("created_at")]
    public SnipeDateTime? CreatedAt { get; set; }
    
    [JsonPropertyName("updated_at")]
    public SnipeDateTime? UpdatedAt { get; set; }
    
    [JsonPropertyName("user_can_checkout")]
    public int UserCanCheckout { get; set; }
    
    [JsonPropertyName("available_actions")]
    public SnipeActions? AvailableActions { get; set; }
}

/// <summary>
/// Asset maintenance record from Snipe-IT API
/// </summary>
public class SnipeMaintenance
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("asset")]
    public SnipeAsset? Asset { get; set; }
    
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;
    
    [JsonPropertyName("supplier")]
    public SnipeRef? Supplier { get; set; }
    
    [JsonPropertyName("asset_maintenance_type")]
    public string? AssetMaintenanceType { get; set; }
    
    [JsonPropertyName("is_warranty")]
    public bool IsWarranty { get; set; }
    
    [JsonPropertyName("cost")]
    public string? Cost { get; set; }
    
    [JsonPropertyName("start_date")]
    public string? StartDate { get; set; }
    
    [JsonPropertyName("completion_date")]
    public string? CompletionDate { get; set; }
    
    [JsonPropertyName("asset_maintenance_time")]
    public int? AssetMaintenanceTime { get; set; }
    
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
    
    [JsonPropertyName("user_id")]
    public int UserId { get; set; }
    
    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }
    
    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; set; }
    
    [JsonPropertyName("deleted_at")]
    public string? DeletedAt { get; set; }
    
    [JsonPropertyName("available_actions")]
    public SnipeActions? AvailableActions { get; set; }
}

/// <summary>
/// Activity log entry from Snipe-IT API
/// </summary>
public class SnipeActivity
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("icon")]
    public string? Icon { get; set; }
    
    [JsonPropertyName("file")]
    public object? File { get; set; }
    
    [JsonPropertyName("item")]
    public SnipeActivityItem? Item { get; set; }
    
    [JsonPropertyName("location")]
    public SnipeRef? Location { get; set; }
    
    [JsonPropertyName("created_at")]
    public SnipeDateTime? CreatedAt { get; set; }
    
    [JsonPropertyName("updated_at")]
    public SnipeDateTime? UpdatedAt { get; set; }
    
    [JsonPropertyName("next_audit_date")]
    public SnipeDate? NextAuditDate { get; set; }
    
    [JsonPropertyName("days_to_next_audit")]
    public int? DaysToNextAudit { get; set; }
    
    [JsonPropertyName("action_type")]
    public string? ActionType { get; set; }
    
    [JsonPropertyName("admin")]
    public SnipeAssignee? Admin { get; set; }
    
    [JsonPropertyName("target")]
    public SnipeAssignee? Target { get; set; }
    
    [JsonPropertyName("note")]
    public string? Note { get; set; }
    
    [JsonPropertyName("signature_file")]
    public string? SignatureFile { get; set; }
    
    [JsonPropertyName("log_meta")]
    public object? LogMeta { get; set; }
    
    [JsonPropertyName("action_date")]
    public SnipeDateTime? ActionDate { get; set; }
}

/// <summary>
/// Activity item reference
/// </summary>
public class SnipeActivityItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    
    [JsonPropertyName("type")]
    public string? Type { get; set; }
    
    [JsonPropertyName("serial")]
    public string? Serial { get; set; }
}
