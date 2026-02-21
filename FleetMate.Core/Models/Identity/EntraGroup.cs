using System.Text.Json.Serialization;

namespace FleetMate.Core.Models.Identity;

/// <summary>
/// Entra ID (Azure AD) group from Microsoft Graph
/// </summary>
public class EntraGroup
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("mail")]
    public string? Mail { get; set; }

    [JsonPropertyName("mailEnabled")]
    public bool? MailEnabled { get; set; }

    [JsonPropertyName("mailNickname")]
    public string? MailNickname { get; set; }

    [JsonPropertyName("securityEnabled")]
    public bool? SecurityEnabled { get; set; }

    [JsonPropertyName("groupTypes")]
    public List<string> GroupTypes { get; set; } = new();

    [JsonPropertyName("membershipRule")]
    public string? MembershipRule { get; set; }

    [JsonPropertyName("membershipRuleProcessingState")]
    public string? MembershipRuleProcessingState { get; set; }

    [JsonPropertyName("createdDateTime")]
    public DateTime? CreatedDateTime { get; set; }

    [JsonPropertyName("onPremisesSyncEnabled")]
    public bool? OnPremisesSyncEnabled { get; set; }

    [JsonPropertyName("onPremisesSecurityIdentifier")]
    public string? OnPremisesSecurityIdentifier { get; set; }

    /// <summary>
    /// Check if this is a dynamic group
    /// </summary>
    [JsonIgnore]
    public bool IsDynamic => GroupTypes.Contains("DynamicMembership");

    /// <summary>
    /// Check if this is a Microsoft 365 group
    /// </summary>
    [JsonIgnore]
    public bool IsM365Group => GroupTypes.Contains("Unified");

    /// <summary>
    /// Members of the group (populated separately)
    /// </summary>
    [JsonIgnore]
    public List<EntraUser> Members { get; set; } = new();

    /// <summary>
    /// Member count (if available)
    /// </summary>
    [JsonIgnore]
    public int? MemberCount { get; set; }
}

/// <summary>
/// Graph API response for group list
/// </summary>
public class EntraGroupListResponse
{
    [JsonPropertyName("value")]
    public List<EntraGroup> Value { get; set; } = new();

    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; set; }
}

/// <summary>
/// Response for group members
/// </summary>
public class GroupMembersResponse
{
    [JsonPropertyName("value")]
    public List<EntraUser> Value { get; set; } = new();

    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; set; }

    [JsonPropertyName("@odata.count")]
    public int? Count { get; set; }
}

/// <summary>
/// Response for checking group membership
/// </summary>
public class CheckMemberGroupsResponse
{
    [JsonPropertyName("value")]
    public List<string> Value { get; set; } = new();
}
