namespace FleetMate.Models.Graph;

/// <summary>
/// Microsoft Graph API configuration
/// </summary>
public class GraphConfig
{
    /// <summary>
    /// Azure tenant ID (optional, uses default from az login)
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Azure application (client) ID for service principal auth
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Azure application client secret for service principal auth
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Use Azure CLI for authentication (default: true)
    /// </summary>
    public bool UseAzureCliAuth { get; set; } = true;

    /// <summary>
    /// Cache duration in minutes for user/group lookups
    /// </summary>
    public int CacheMinutes { get; set; } = 10;

    /// <summary>
    /// Maximum results per page for device queries
    /// </summary>
    public int PageSize { get; set; } = 100;
}
