using System.Text.Json.Serialization;

namespace FleetMate.Models.Snipe;

/// <summary>
/// Asset model from Snipe-IT API
/// </summary>
public class SnipeModel
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("manufacturer")]
    public SnipeRef? Manufacturer { get; set; }
    
    [JsonPropertyName("image")]
    public string? Image { get; set; }
    
    [JsonPropertyName("model_number")]
    public string? ModelNumber { get; set; }
    
    [JsonPropertyName("min_amt")]
    public int? MinAmt { get; set; }
    
    [JsonPropertyName("depreciation")]
    public SnipeRef? Depreciation { get; set; }
    
    [JsonPropertyName("assets_count")]
    public int AssetsCount { get; set; }
    
    [JsonPropertyName("category")]
    public SnipeRef? Category { get; set; }
    
    [JsonPropertyName("fieldset")]
    public SnipeRef? Fieldset { get; set; }
    
    [JsonPropertyName("eol")]
    public int? Eol { get; set; } // months until EOL
    
    [JsonPropertyName("requestable")]
    public bool Requestable { get; set; }
    
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
    
    [JsonPropertyName("created_at")]
    public SnipeDateTime? CreatedAt { get; set; }
    
    [JsonPropertyName("updated_at")]
    public SnipeDateTime? UpdatedAt { get; set; }
    
    [JsonPropertyName("deleted_at")]
    public SnipeDateTime? DeletedAt { get; set; }
    
    [JsonPropertyName("available_actions")]
    public SnipeActions? AvailableActions { get; set; }
}

/// <summary>
/// Request model for creating/updating models
/// </summary>
public class SnipeModelRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("model_number")]
    public string? ModelNumber { get; set; }
    
    [JsonPropertyName("category_id")]
    public int CategoryId { get; set; }
    
    [JsonPropertyName("manufacturer_id")]
    public int? ManufacturerId { get; set; }
    
    [JsonPropertyName("eol")]
    public int? Eol { get; set; }
    
    [JsonPropertyName("fieldset_id")]
    public int? FieldsetId { get; set; }
    
    [JsonPropertyName("depreciation_id")]
    public int? DepreciationId { get; set; }
    
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
    
    [JsonPropertyName("requestable")]
    public bool Requestable { get; set; }
}
