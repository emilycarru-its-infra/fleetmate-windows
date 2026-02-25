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

    // Multi-SP: separate credentials for Devices vs Systems scopes (matches macOS)

    /// <summary>Client ID for the Devices-scoped service principal.</summary>
    public string? DevicesClientId { get; set; }

    /// <summary>Client secret for the Devices-scoped service principal.</summary>
    public string? DevicesClientSecret { get; set; }

    /// <summary>Client ID for the Systems-scoped service principal.</summary>
    public string? SystemsClientId { get; set; }

    /// <summary>Client secret for the Systems-scoped service principal.</summary>
    public string? SystemsClientSecret { get; set; }

    /// <summary>True when a dedicated Devices SP is configured.</summary>
    public bool IsDevicesSpConfigured =>
        !string.IsNullOrWhiteSpace(DevicesClientId) && !string.IsNullOrWhiteSpace(DevicesClientSecret);

    /// <summary>True when a dedicated Systems SP is configured.</summary>
    public bool IsSystemsSpConfigured =>
        !string.IsNullOrWhiteSpace(SystemsClientId) && !string.IsNullOrWhiteSpace(SystemsClientSecret);
}
