using System.Text.Json.Serialization;

namespace FleetMate.Core.Models.Inventory;

/// <summary>
/// Category from Snipe-IT API
/// </summary>
public class SnipeCategory
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("image")]
    public string? Image { get; set; }
    
    [JsonPropertyName("category_type")]
    public string CategoryType { get; set; } = string.Empty; // asset, accessory, consumable, component, license
    
    [JsonPropertyName("has_eula")]
    public bool HasEula { get; set; }
    
    [JsonPropertyName("use_default_eula")]
    public bool UseDefaultEula { get; set; }
    
    [JsonPropertyName("eula")]
    public string? Eula { get; set; }
    
    [JsonPropertyName("checkin_email")]
    public bool CheckinEmail { get; set; }
    
    [JsonPropertyName("require_acceptance")]
    public bool RequireAcceptance { get; set; }
    
    [JsonPropertyName("item_count")]
    public int ItemCount { get; set; }
    
    [JsonPropertyName("assets_count")]
    public int AssetsCount { get; set; }
    
    [JsonPropertyName("accessories_count")]
    public int AccessoriesCount { get; set; }
    
    [JsonPropertyName("consumables_count")]
    public int ConsumablesCount { get; set; }
    
    [JsonPropertyName("components_count")]
    public int ComponentsCount { get; set; }
    
    [JsonPropertyName("licenses_count")]
    public int LicensesCount { get; set; }
    
    [JsonPropertyName("created_at")]
    public SnipeDateTime? CreatedAt { get; set; }
    
    [JsonPropertyName("updated_at")]
    public SnipeDateTime? UpdatedAt { get; set; }
    
    [JsonPropertyName("available_actions")]
    public SnipeActions? AvailableActions { get; set; }
}

/// <summary>
/// Request model for creating/updating categories
/// </summary>
public class SnipeCategoryRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("category_type")]
    public string CategoryType { get; set; } = "asset"; // asset, accessory, consumable, component, license
    
    [JsonPropertyName("use_default_eula")]
    public bool UseDefaultEula { get; set; }
    
    [JsonPropertyName("require_acceptance")]
    public bool RequireAcceptance { get; set; }
    
    [JsonPropertyName("checkin_email")]
    public bool CheckinEmail { get; set; }
    
    [JsonPropertyName("eula_text")]
    public string? EulaText { get; set; }
}
