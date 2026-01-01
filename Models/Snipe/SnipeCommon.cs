using System.Text.Json.Serialization;

namespace FleetMate.Models.Snipe;

/// <summary>
/// Common types shared across Snipe-IT API responses
/// </summary>
/// 
/// <summary>
/// Generic reference to another entity (id + name)
/// </summary>
public class SnipeRef
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Status label with type information
/// </summary>
public class SnipeStatusLabel
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("status_type")]
    public string? StatusType { get; set; } // deployable, pending, undeployable, archived
    
    [JsonPropertyName("status_meta")]
    public string? StatusMeta { get; set; }
}

/// <summary>
/// Date-only representation
/// </summary>
public class SnipeDate
{
    [JsonPropertyName("date")]
    public string? Date { get; set; }
    
    [JsonPropertyName("formatted")]
    public string? Formatted { get; set; }
}

/// <summary>
/// Full datetime representation
/// </summary>
public class SnipeDateTime
{
    [JsonPropertyName("datetime")]
    public string? DateTime { get; set; }
    
    [JsonPropertyName("formatted")]
    public string? Formatted { get; set; }
}

/// <summary>
/// Entity an asset is assigned to
/// </summary>
public class SnipeAssignee
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty; // user, asset, location
    
    [JsonPropertyName("username")]
    public string? Username { get; set; }
    
    [JsonPropertyName("first_name")]
    public string? FirstName { get; set; }
    
    [JsonPropertyName("last_name")]
    public string? LastName { get; set; }
    
    [JsonPropertyName("employee_number")]
    public string? EmployeeNumber { get; set; }
}

/// <summary>
/// Custom field value
/// </summary>
public class SnipeCustomField
{
    [JsonPropertyName("field")]
    public string Field { get; set; } = string.Empty;
    
    [JsonPropertyName("value")]
    public string? Value { get; set; }
    
    [JsonPropertyName("field_format")]
    public string? FieldFormat { get; set; }
    
    [JsonPropertyName("element")]
    public string? Element { get; set; }
}

/// <summary>
/// Available actions for an entity
/// </summary>
public class SnipeActions
{
    [JsonPropertyName("checkout")]
    public bool Checkout { get; set; }
    
    [JsonPropertyName("checkin")]
    public bool Checkin { get; set; }
    
    [JsonPropertyName("clone")]
    public bool Clone { get; set; }
    
    [JsonPropertyName("restore")]
    public bool Restore { get; set; }
    
    [JsonPropertyName("update")]
    public bool Update { get; set; }
    
    [JsonPropertyName("delete")]
    public bool Delete { get; set; }
}

/// <summary>
/// Generic paginated response wrapper
/// </summary>
public class SnipeListResponse<T>
{
    [JsonPropertyName("total")]
    public int Total { get; set; }
    
    [JsonPropertyName("rows")]
    public List<T> Rows { get; set; } = new();
}

/// <summary>
/// API response for mutations
/// </summary>
public class SnipeResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
    
    [JsonPropertyName("messages")]
    public string? Messages { get; set; }
    
    [JsonPropertyName("payload")]
    public object? Payload { get; set; }
    
    public bool IsSuccess => Status.Equals("success", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// API response with typed payload
/// </summary>
public class SnipeResponse<T>
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
    
    [JsonPropertyName("messages")]
    public string? Messages { get; set; }
    
    [JsonPropertyName("payload")]
    public T? Payload { get; set; }
    
    public bool IsSuccess => Status.Equals("success", StringComparison.OrdinalIgnoreCase);
}
