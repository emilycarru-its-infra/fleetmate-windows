using System.Text.Json.Serialization;

namespace FleetMate.Models.Snipe;

/// <summary>
/// User from Snipe-IT API
/// </summary>
public class SnipeUser
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("avatar")]
    public string? Avatar { get; set; }
    
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("first_name")]
    public string FirstName { get; set; } = string.Empty;
    
    [JsonPropertyName("last_name")]
    public string? LastName { get; set; }
    
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;
    
    [JsonPropertyName("employee_num")]
    public string? EmployeeNum { get; set; }
    
    [JsonPropertyName("manager")]
    public SnipeRef? Manager { get; set; }
    
    [JsonPropertyName("jobtitle")]
    public string? JobTitle { get; set; }
    
    [JsonPropertyName("vip")]
    public bool Vip { get; set; }
    
    [JsonPropertyName("phone")]
    public string? Phone { get; set; }
    
    [JsonPropertyName("website")]
    public string? Website { get; set; }
    
    [JsonPropertyName("address")]
    public string? Address { get; set; }
    
    [JsonPropertyName("city")]
    public string? City { get; set; }
    
    [JsonPropertyName("state")]
    public string? State { get; set; }
    
    [JsonPropertyName("country")]
    public string? Country { get; set; }
    
    [JsonPropertyName("zip")]
    public string? Zip { get; set; }
    
    [JsonPropertyName("email")]
    public string? Email { get; set; }
    
    [JsonPropertyName("department")]
    public SnipeRef? Department { get; set; }
    
    [JsonPropertyName("location")]
    public SnipeRef? Location { get; set; }
    
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
    
    [JsonPropertyName("permissions")]
    public Dictionary<string, object>? Permissions { get; set; }
    
    [JsonPropertyName("activated")]
    public bool Activated { get; set; }
    
    [JsonPropertyName("autoassign_licenses")]
    public bool AutoassignLicenses { get; set; }
    
    [JsonPropertyName("ldap_import")]
    public bool LdapImport { get; set; }
    
    [JsonPropertyName("two_factor_enrolled")]
    public bool TwoFactorEnrolled { get; set; }
    
    [JsonPropertyName("two_factor_optin")]
    public bool TwoFactorOptin { get; set; }
    
    [JsonPropertyName("assets_count")]
    public int AssetsCount { get; set; }
    
    [JsonPropertyName("licenses_count")]
    public int LicensesCount { get; set; }
    
    [JsonPropertyName("accessories_count")]
    public int AccessoriesCount { get; set; }
    
    [JsonPropertyName("consumables_count")]
    public int ConsumablesCount { get; set; }
    
    [JsonPropertyName("company")]
    public SnipeRef? Company { get; set; }
    
    [JsonPropertyName("created_at")]
    public SnipeDateTime? CreatedAt { get; set; }
    
    [JsonPropertyName("updated_at")]
    public SnipeDateTime? UpdatedAt { get; set; }
    
    [JsonPropertyName("start_date")]
    public SnipeDate? StartDate { get; set; }
    
    [JsonPropertyName("end_date")]
    public SnipeDate? EndDate { get; set; }
    
    [JsonPropertyName("last_login")]
    public SnipeDateTime? LastLogin { get; set; }
    
    [JsonPropertyName("deleted_at")]
    public SnipeDateTime? DeletedAt { get; set; }
    
    [JsonPropertyName("remote")]
    public bool Remote { get; set; }
    
    [JsonPropertyName("available_actions")]
    public SnipeActions? AvailableActions { get; set; }
    
    [JsonPropertyName("groups")]
    public SnipeRef[]? Groups { get; set; }
}

/// <summary>
/// Request model for creating/updating users
/// </summary>
public class SnipeUserRequest
{
    [JsonPropertyName("first_name")]
    public string FirstName { get; set; } = string.Empty;
    
    [JsonPropertyName("last_name")]
    public string? LastName { get; set; }
    
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;
    
    [JsonPropertyName("password")]
    public string? Password { get; set; }
    
    [JsonPropertyName("password_confirmation")]
    public string? PasswordConfirmation { get; set; }
    
    [JsonPropertyName("email")]
    public string? Email { get; set; }
    
    [JsonPropertyName("activated")]
    public bool Activated { get; set; } = true;
    
    [JsonPropertyName("phone")]
    public string? Phone { get; set; }
    
    [JsonPropertyName("jobtitle")]
    public string? JobTitle { get; set; }
    
    [JsonPropertyName("manager_id")]
    public int? ManagerId { get; set; }
    
    [JsonPropertyName("employee_num")]
    public string? EmployeeNum { get; set; }
    
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
    
    [JsonPropertyName("company_id")]
    public int? CompanyId { get; set; }
    
    [JsonPropertyName("department_id")]
    public int? DepartmentId { get; set; }
    
    [JsonPropertyName("location_id")]
    public int? LocationId { get; set; }
    
    [JsonPropertyName("remote")]
    public bool Remote { get; set; }
    
    [JsonPropertyName("groups")]
    public int[]? Groups { get; set; }
    
    [JsonPropertyName("autoassign_licenses")]
    public bool AutoassignLicenses { get; set; }
    
    [JsonPropertyName("vip")]
    public bool Vip { get; set; }
    
    [JsonPropertyName("start_date")]
    public string? StartDate { get; set; }
    
    [JsonPropertyName("end_date")]
    public string? EndDate { get; set; }
}
