using System.Text.Json.Serialization;

namespace FleetMate.Core.Models.Inventory;

/// <summary>
/// Manufacturer from Snipe-IT API
/// </summary>
public class SnipeManufacturer
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("url")]
    public string? Url { get; set; }
    
    [JsonPropertyName("image")]
    public string? Image { get; set; }
    
    [JsonPropertyName("support_url")]
    public string? SupportUrl { get; set; }
    
    [JsonPropertyName("support_phone")]
    public string? SupportPhone { get; set; }
    
    [JsonPropertyName("support_email")]
    public string? SupportEmail { get; set; }
    
    [JsonPropertyName("assets_count")]
    public int AssetsCount { get; set; }
    
    [JsonPropertyName("licenses_count")]
    public int LicensesCount { get; set; }
    
    [JsonPropertyName("consumables_count")]
    public int ConsumablesCount { get; set; }
    
    [JsonPropertyName("accessories_count")]
    public int AccessoriesCount { get; set; }
    
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
/// Request model for creating/updating manufacturers
/// </summary>
public class SnipeManufacturerRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("url")]
    public string? Url { get; set; }
    
    [JsonPropertyName("support_url")]
    public string? SupportUrl { get; set; }
    
    [JsonPropertyName("support_phone")]
    public string? SupportPhone { get; set; }
    
    [JsonPropertyName("support_email")]
    public string? SupportEmail { get; set; }
}

/// <summary>
/// Supplier from Snipe-IT API
/// </summary>
public class SnipeSupplier
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("image")]
    public string? Image { get; set; }
    
    [JsonPropertyName("url")]
    public string? Url { get; set; }
    
    [JsonPropertyName("address")]
    public string? Address { get; set; }
    
    [JsonPropertyName("address2")]
    public string? Address2 { get; set; }
    
    [JsonPropertyName("city")]
    public string? City { get; set; }
    
    [JsonPropertyName("state")]
    public string? State { get; set; }
    
    [JsonPropertyName("country")]
    public string? Country { get; set; }
    
    [JsonPropertyName("zip")]
    public string? Zip { get; set; }
    
    [JsonPropertyName("fax")]
    public string? Fax { get; set; }
    
    [JsonPropertyName("phone")]
    public string? Phone { get; set; }
    
    [JsonPropertyName("email")]
    public string? Email { get; set; }
    
    [JsonPropertyName("contact")]
    public string? Contact { get; set; }
    
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
    
    [JsonPropertyName("assets_count")]
    public int AssetsCount { get; set; }
    
    [JsonPropertyName("licenses_count")]
    public int LicensesCount { get; set; }
    
    [JsonPropertyName("accessories_count")]
    public int AccessoriesCount { get; set; }
    
    [JsonPropertyName("created_at")]
    public SnipeDateTime? CreatedAt { get; set; }
    
    [JsonPropertyName("updated_at")]
    public SnipeDateTime? UpdatedAt { get; set; }
    
    [JsonPropertyName("available_actions")]
    public SnipeActions? AvailableActions { get; set; }
}

/// <summary>
/// Company from Snipe-IT API
/// </summary>
public class SnipeCompany
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("image")]
    public string? Image { get; set; }
    
    [JsonPropertyName("assets_count")]
    public int AssetsCount { get; set; }
    
    [JsonPropertyName("licenses_count")]
    public int LicensesCount { get; set; }
    
    [JsonPropertyName("accessories_count")]
    public int AccessoriesCount { get; set; }
    
    [JsonPropertyName("consumables_count")]
    public int ConsumablesCount { get; set; }
    
    [JsonPropertyName("components_count")]
    public int ComponentsCount { get; set; }
    
    [JsonPropertyName("users_count")]
    public int UsersCount { get; set; }
    
    [JsonPropertyName("created_at")]
    public SnipeDateTime? CreatedAt { get; set; }
    
    [JsonPropertyName("updated_at")]
    public SnipeDateTime? UpdatedAt { get; set; }
    
    [JsonPropertyName("available_actions")]
    public SnipeActions? AvailableActions { get; set; }
}

/// <summary>
/// Department from Snipe-IT API
/// </summary>
public class SnipeDepartment
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("image")]
    public string? Image { get; set; }
    
    [JsonPropertyName("company")]
    public SnipeRef? Company { get; set; }
    
    [JsonPropertyName("manager")]
    public SnipeRef? Manager { get; set; }
    
    [JsonPropertyName("location")]
    public SnipeRef? Location { get; set; }
    
    [JsonPropertyName("users_count")]
    public string? UsersCount { get; set; }
    
    [JsonPropertyName("created_at")]
    public SnipeDateTime? CreatedAt { get; set; }
    
    [JsonPropertyName("updated_at")]
    public SnipeDateTime? UpdatedAt { get; set; }
    
    [JsonPropertyName("available_actions")]
    public SnipeActions? AvailableActions { get; set; }
}

/// <summary>
/// Status label from Snipe-IT API (full entity, not just ref)
/// </summary>
public class SnipeStatusLabelFull
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty; // deployable, pending, undeployable, archived
    
    [JsonPropertyName("color")]
    public string? Color { get; set; }
    
    [JsonPropertyName("show_in_nav")]
    public bool ShowInNav { get; set; }
    
    [JsonPropertyName("default_label")]
    public bool DefaultLabel { get; set; }
    
    [JsonPropertyName("assets_count")]
    public int AssetsCount { get; set; }
    
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
    
    [JsonPropertyName("created_at")]
    public SnipeDateTime? CreatedAt { get; set; }
    
    [JsonPropertyName("updated_at")]
    public SnipeDateTime? UpdatedAt { get; set; }
    
    [JsonPropertyName("available_actions")]
    public SnipeActions? AvailableActions { get; set; }
}

/// <summary>
/// Request model for creating/updating status labels
/// </summary>
public class SnipeStatusLabelRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("type")]
    public string Type { get; set; } = "deployable"; // deployable, pending, undeployable, archived
    
    [JsonPropertyName("color")]
    public string? Color { get; set; }
    
    [JsonPropertyName("show_in_nav")]
    public bool ShowInNav { get; set; }
    
    [JsonPropertyName("default_label")]
    public bool DefaultLabel { get; set; }
    
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}
