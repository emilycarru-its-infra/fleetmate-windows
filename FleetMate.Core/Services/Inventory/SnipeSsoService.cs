using Serilog;

namespace FleetMate.Core.Services.Inventory;

/// <summary>Result of a Snipe-IT SSO token acquisition.</summary>
public sealed class SnipeSsoResult
{
    public bool Success { get; init; }
    public string? Token { get; init; }
    public DateTime Expiry { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Secretless auth for Snipe-IT: mints a delegated Entra bearer for the configured
/// Snipe resource off the operator's <c>az</c> session (via <see cref="AzTokenSource"/>),
/// matching the macOS "OIDC-default" direction. This is the Core-side silent path;
/// an interactive WebView2 cookie flow (if ever needed) lives in the GUI. When no
/// resource is configured, the caller falls back to the static Snipe API key.
/// </summary>
public sealed class SnipeSsoService
{
    private readonly string _resource;
    private readonly Services.AzTokenSource _tokens;

    public string? CurrentToken { get; private set; }
    public DateTime TokenExpiry { get; private set; } = DateTime.MinValue;
    public bool IsAuthenticated => CurrentToken != null && DateTime.UtcNow < TokenExpiry;

    /// <param name="resource">Snipe API's Entra app/client id (GUID) or api:// URI.</param>
    public SnipeSsoService(string resource, Services.AzTokenSource? tokens = null)
    {
        _resource = resource ?? "";
        _tokens = tokens ?? new Services.AzTokenSource();
    }

    /// <summary>Acquire (or refresh) a delegated bearer for Snipe-IT via az.</summary>
    public async Task<SnipeSsoResult> AcquireTokenAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_resource))
            return new SnipeSsoResult { Success = false, Error = "No Snipe resource id configured" };

        try
        {
            var token = await _tokens.GetTokenAsync(_resource, ct);
            if (string.IsNullOrEmpty(token))
                return new SnipeSsoResult { Success = false, Error = "az returned no token (run az login)" };

            CurrentToken = token;
            TokenExpiry = DateTime.UtcNow.AddMinutes(50);
            Log.Information("SnipeSsoService: acquired delegated token for Snipe (expires {Expiry})", TokenExpiry);
            return new SnipeSsoResult { Success = true, Token = token, Expiry = TokenExpiry };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SnipeSsoService: token acquisition failed");
            return new SnipeSsoResult { Success = false, Error = ex.Message };
        }
    }

    public void Clear()
    {
        CurrentToken = null;
        TokenExpiry = DateTime.MinValue;
        _tokens.Clear();
    }
}
