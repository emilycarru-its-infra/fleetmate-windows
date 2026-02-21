using System.Text.Json.Serialization;

namespace FleetMate.Core.Models.Inventory;

/// <summary>
/// Location from Snipe-IT API
/// </summary>
public class SnipeLocation
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("image")]
    public string? Image { get; set; }
    
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
    
    [JsonPropertyName("phone")]
    public string? Phone { get; set; }
    
    [JsonPropertyName("fax")]
    public string? Fax { get; set; }
    
    [JsonPropertyName("parent")]
    public SnipeRef? Parent { get; set; }
    
    [JsonPropertyName("manager")]
    public SnipeRef? Manager { get; set; }
    
    [JsonPropertyName("currency")]
    public string? Currency { get; set; }
    
    [JsonPropertyName("ldap_ou")]
    public string? LdapOu { get; set; }
    
    [JsonPropertyName("assets_count")]
    public int AssetsCount { get; set; }
    
    [JsonPropertyName("assigned_assets_count")]
    public int AssignedAssetsCount { get; set; }
    
    [JsonPropertyName("users_count")]
    public int UsersCount { get; set; }
    
    [JsonPropertyName("created_at")]
    public SnipeDateTime? CreatedAt { get; set; }
    
    [JsonPropertyName("updated_at")]
    public SnipeDateTime? UpdatedAt { get; set; }
    
    [JsonPropertyName("children")]
    public List<SnipeLocation>? Children { get; set; }
    
    [JsonPropertyName("available_actions")]
    public SnipeActions? AvailableActions { get; set; }
}

/// <summary>
/// Request model for creating/updating locations
/// </summary>
public class SnipeLocationRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
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
    
    [JsonPropertyName("phone")]
    public string? Phone { get; set; }
    
    [JsonPropertyName("fax")]
    public string? Fax { get; set; }
    
    [JsonPropertyName("parent_id")]
    public int? ParentId { get; set; }
    
    [JsonPropertyName("manager_id")]
    public int? ManagerId { get; set; }
    
    [JsonPropertyName("ldap_ou")]
    public string? LdapOu { get; set; }
    
    [JsonPropertyName("currency")]
    public string? Currency { get; set; }
}
