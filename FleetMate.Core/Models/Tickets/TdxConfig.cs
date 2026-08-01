namespace FleetMate.Core.Models.Tickets;

using FleetMate.Core.Services;

/// <summary>
/// TeamDynamix (TDX) API configuration.
///
/// Authentication is SSO only. The BEID/WebServicesKey service account, the
/// username/password login and the Key Vault that fed them are gone: every TDX
/// action is now attributed to the operator who took it, not to a shared
/// identity that made the audit trail useless.
///
/// The way in is <c>GET /TDWebApi/api/auth/loginsso</c>, reached with the
/// operator's Windows credentials over Negotiate/Kerberos. It redirects through
/// Shibboleth to Entra and returns that person's own Bearer JWT.
///
/// Required Environment Variables:
/// - TDX_BASE_URL: TDX Web API base URL (e.g., https://your-instance.teamdynamix.com/TDWebApi)
/// - TDX_APP_ID: TDX application ID for tickets
/// </summary>
public class TdxConfig
{
    /// <summary>
    /// TDX Web API base URL (required - set via TDX_BASE_URL env var)
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// TDX Application ID for tickets
    /// </summary>
    public int AppId { get; set; }

    /// <summary>
    /// Responsible group ID to filter tickets by in the GUI
    /// </summary>
    public int ResponsibleGroupId { get; set; }

    /// <summary>
    /// Default ticket type ID for new tickets
    /// </summary>
    public int? DefaultTypeId { get; set; }

    /// <summary>
    /// Default ticket source ID (e.g., "API", "FleetMate")
    /// </summary>
    public int? DefaultSourceId { get; set; }

    /// <summary>
    /// Default priority ID for new tickets
    /// </summary>
    public int? DefaultPriorityId { get; set; }

    /// <summary>
    /// Default status ID for new tickets
    /// </summary>
    public int? DefaultStatusId { get; set; }

    /// <summary>
    /// Default account/department ID for new tickets
    /// </summary>
    public int? DefaultAccountId { get; set; }

    /// <summary>
    /// Cache duration in minutes for reference data
    /// </summary>
    public int CacheMinutes { get; set; } = 30;
    
    /// <summary>
    /// Separate Application ID for ticketing (if different from AppId)
    /// </summary>
    public int? TicketingAppId { get; set; }
    
    /// <summary>
    /// Separate Application ID for assets (if different from AppId)
    /// </summary>
    public int? AssetsAppId { get; set; }
    
    /// <summary>
    /// SSO is the only authentication path, so this is true wherever TDX is
    /// configured at all. Kept as a named property because call sites read
    /// better asking about SSO than asking about a URL.
    /// </summary>
    public bool SsoEnabled => !string.IsNullOrEmpty(BaseUrl);

    /// <summary>
    /// Get the API URL for a specific endpoint
    /// </summary>
    public string GetApiUrl(string endpoint)
    {
        if (string.IsNullOrEmpty(BaseUrl))
            throw new InvalidOperationException("TDX BaseUrl is not configured. Set TDX_BASE_URL environment variable.");

        var baseUrl = GetNormalizedApiBaseUrl();
        return $"{baseUrl}/api/{endpoint}";
    }

    /// <summary>
    /// Get the tickets API URL
    /// </summary>
    public string GetTicketsUrl(string? path = null)
    {
        if (string.IsNullOrEmpty(BaseUrl))
            throw new InvalidOperationException("TDX BaseUrl is not configured. Set TDX_BASE_URL environment variable.");

        var baseUrl = GetNormalizedApiBaseUrl();
        var appId = TicketingAppId ?? AppId;
        return string.IsNullOrEmpty(path)
            ? $"{baseUrl}/api/{appId}/tickets"
            : $"{baseUrl}/api/{appId}/tickets/{path}";
    }

    /// <summary>
    /// Get the assets API URL
    /// </summary>
    public string GetAssetsUrl(string? path = null)
    {
        if (string.IsNullOrEmpty(BaseUrl))
            throw new InvalidOperationException("TDX BaseUrl is not configured. Set TDX_BASE_URL environment variable.");

        var baseUrl = GetNormalizedApiBaseUrl();
        var appId = AssetsAppId ?? AppId;
        return string.IsNullOrEmpty(path)
            ? $"{baseUrl}/api/{appId}/assets"
            : $"{baseUrl}/api/{appId}/assets/{path}";
    }

    /// <summary>
    /// Check if TDX is configured (has required settings)
    /// </summary>
    public bool IsConfigured => !string.IsNullOrEmpty(BaseUrl) && AppId > 0;

    private string GetNormalizedApiBaseUrl()
    {
        var baseUrl = ServiceUri.Normalize(BaseUrl!);
        return baseUrl.EndsWith("/TDWebApi", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : baseUrl + "/TDWebApi";
    }
}
