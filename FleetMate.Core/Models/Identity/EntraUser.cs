using System.Text.Json.Serialization;

namespace FleetMate.Core.Models.Identity;

/// <summary>
/// Entra ID (Azure AD) user from Microsoft Graph
/// </summary>
public class EntraUser
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("userPrincipalName")]
    public string UserPrincipalName { get; set; } = string.Empty;

    [JsonPropertyName("mail")]
    public string? Mail { get; set; }

    [JsonPropertyName("givenName")]
    public string? GivenName { get; set; }

    [JsonPropertyName("surname")]
    public string? Surname { get; set; }

    [JsonPropertyName("jobTitle")]
    public string? JobTitle { get; set; }

    [JsonPropertyName("department")]
    public string? Department { get; set; }

    [JsonPropertyName("officeLocation")]
    public string? OfficeLocation { get; set; }

    [JsonPropertyName("mobilePhone")]
    public string? MobilePhone { get; set; }

    [JsonPropertyName("businessPhones")]
    public List<string> BusinessPhones { get; set; } = new();

    [JsonPropertyName("accountEnabled")]
    public bool? AccountEnabled { get; set; }

    [JsonPropertyName("createdDateTime")]
    public DateTime? CreatedDateTime { get; set; }

    [JsonPropertyName("lastSignInDateTime")]
    public DateTime? LastSignInDateTime { get; set; }

    [JsonPropertyName("employeeId")]
    public string? EmployeeId { get; set; }

    [JsonPropertyName("employeeType")]
    public string? EmployeeType { get; set; }

    [JsonPropertyName("companyName")]
    public string? CompanyName { get; set; }

    [JsonPropertyName("usageLocation")]
    public string? UsageLocation { get; set; }

    [JsonPropertyName("onPremisesSamAccountName")]
    public string? OnPremisesSamAccountName { get; set; }

    [JsonPropertyName("onPremisesDistinguishedName")]
    public string? OnPremisesDistinguishedName { get; set; }

    [JsonPropertyName("onPremisesSyncEnabled")]
    public bool? OnPremisesSyncEnabled { get; set; }

    [JsonPropertyName("@odata.type")]
    public string? OdataType { get; set; }

    /// <summary>
    /// Groups the user is a member of (populated separately)
    /// </summary>
    [JsonIgnore]
    public List<EntraGroup> MemberOf { get; set; } = new();
}

/// <summary>
/// Graph API response for user list
/// </summary>
public class EntraUserListResponse
{
    [JsonPropertyName("value")]
    public List<EntraUser> Value { get; set; } = new();

    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; set; }
}

/// <summary>
/// User's group membership response
/// </summary>
public class UserMemberOfResponse
{
    [JsonPropertyName("value")]
    public List<DirectoryObject> Value { get; set; } = new();

    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; set; }
}

/// <summary>
/// Generic directory object (can be group, role, etc.)
/// </summary>
public class DirectoryObject
{
    [JsonPropertyName("@odata.type")]
    public string? ODataType { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Check if this is a group
    /// </summary>
    [JsonIgnore]
    public bool IsGroup => ODataType?.Contains("group", StringComparison.OrdinalIgnoreCase) == true;
}
