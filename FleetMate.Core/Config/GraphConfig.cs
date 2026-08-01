namespace FleetMate.Core.Config;

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
    /// Client ID FleetMate presents to the Entra broker. Optional — defaults to
    /// the Azure CLI's public client. This is not a credential.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Cache duration in minutes for user/group lookups
    /// </summary>
    public int CacheMinutes { get; set; } = 10;

    /// <summary>
    /// Maximum results per page for device queries
    /// </summary>
    public int PageSize { get; set; } = 100;

    // The per-scope Devices/Systems service principals were removed along with
    // the rest of the secret-bearing auth. Scope separation is now the operator's
    // own Entra role assignments, and privileged writes go through the
    // managed-identity elevation session rather than a second set of app creds.
}
