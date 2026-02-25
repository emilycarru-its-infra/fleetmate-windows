using System.Net;
using System.Text;
using System.Text.Json;
using Serilog;

namespace FleetMate.Core.Services.Tickets;

/// <summary>
/// Core-layer TDX SSO service for non-GUI token acquisition.
/// Handles Phase 1 (silent HTTP Negotiate/Kerberos) and JWT parsing.
/// Phase 1.5/2 (WebView2) remain in the GUI layer.
/// </summary>
public class TdxSsoService
{
    private readonly string _baseUrl;
    private string? _token;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private string? _userName;
    private string? _userEmail;

    public bool HasValidToken => !string.IsNullOrEmpty(_token) && DateTime.UtcNow < _tokenExpiry;
    public string? UserName => _userName;
    public string? UserEmail => _userEmail;
    public string? Token => HasValidToken ? _token : null;

    public TdxSsoService(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Phase 1: Attempt silent SSO using Windows Negotiate/Kerberos credentials.
    /// No UI required — pure HTTP call chain.
    /// </summary>
    public async Task<TdxSsoResult> TrySilentSsoAsync(CancellationToken ct = default)
    {
        var rootUrl = _baseUrl.Replace("/TDWebApi", "");
        var loginSsoUrl = _baseUrl + "/api/auth/loginSSO";
        var entryUrl = rootUrl + "/TDWorkManagement/";

        Log.Information("[tdx-sso-core] Starting silent HTTP SSO (Negotiate/Kerberos)");

        using var handler = new HttpClientHandler
        {
            UseDefaultCredentials = true,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 20,
            UseCookies = true,
            CookieContainer = new CookieContainer()
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Add("User-Agent", "FleetMate/1.0");

        try
        {
            // Step 1: Check loginSSO for existing session
            var resp = await client.GetAsync(loginSsoUrl, ct);
            if (resp.IsSuccessStatusCode)
            {
                var result = await TryExtractJwt(resp, ct);
                if (result != null) return result;
            }

            // Step 2: Follow full SAML redirect chain
            await client.GetAsync(entryUrl, ct);

            // Step 3: Retry loginSSO with SAML cookies
            var jwtResp = await client.GetAsync(loginSsoUrl, ct);
            if (jwtResp.IsSuccessStatusCode)
            {
                var result = await TryExtractJwt(jwtResp, ct);
                if (result != null) return result;
            }
        }
        catch (OperationCanceledException)
        {
            Log.Debug("[tdx-sso-core] Cancelled");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[tdx-sso-core] Silent SSO failed");
        }

        return new TdxSsoResult { Success = false, Error = "Silent SSO did not produce a JWT" };
    }

    /// <summary>
    /// Set a token obtained externally (e.g., from GUI WebView2 phases).
    /// </summary>
    public void SetToken(string token, DateTime? expiry = null, string? userName = null, string? userEmail = null)
    {
        _token = token;
        _tokenExpiry = expiry ?? DateTime.UtcNow.AddHours(23);
        _userName = userName;
        _userEmail = userEmail;
    }

    /// <summary>Clear the current token.</summary>
    public void ClearToken()
    {
        _token = null;
        _tokenExpiry = DateTime.MinValue;
        _userName = null;
        _userEmail = null;
    }

    private async Task<TdxSsoResult?> TryExtractJwt(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        var token = body.Trim().Trim('"');
        if (token.StartsWith("eyJ") && token.Length > 20)
        {
            var (name, email) = ExtractUserInfoFromJwt(token);
            _token = token;
            _tokenExpiry = DateTime.UtcNow.AddHours(23);
            _userName = name;
            _userEmail = email;
            Log.Information("[tdx-sso-core] ✓ JWT acquired — user={UserName}", name ?? "(unknown)");
            return new TdxSsoResult
            {
                Success = true,
                Token = token,
                UserName = name,
                UserEmail = email,
                Expiry = _tokenExpiry
            };
        }
        return null;
    }

    /// <summary>Extract user info from a JWT payload (no signature verification).</summary>
    public static (string? userName, string? userEmail) ExtractUserInfoFromJwt(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return (null, null);

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            var remainder = payload.Length % 4;
            if (remainder > 0) payload += new string('=', 4 - remainder);

            var bytes = Convert.FromBase64String(payload);
            var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;

            string? name = null;
            string? email = null;

            foreach (var claim in new[] { "given_name", "name", "unique_name" })
            {
                if (root.TryGetProperty(claim, out var val) && val.ValueKind == JsonValueKind.String)
                {
                    name = val.GetString();
                    if (!string.IsNullOrEmpty(name)) break;
                }
            }

            foreach (var claim in new[] { "email", "upn", "unique_name" })
            {
                if (root.TryGetProperty(claim, out var val) && val.ValueKind == JsonValueKind.String)
                {
                    email = val.GetString();
                    if (!string.IsNullOrEmpty(email)) break;
                }
            }

            return (name, email);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to extract user info from JWT");
            return (null, null);
        }
    }
}

/// <summary>
/// Result from a TDX SSO attempt (Core-layer).
/// </summary>
public class TdxSsoResult
{
    public bool Success { get; init; }
    public string? Token { get; init; }
    public string? UserName { get; init; }
    public string? UserEmail { get; init; }
    public DateTime Expiry { get; init; }
    public string? Error { get; init; }

    public static TdxSsoResult Failed(string error) => new() { Success = false, Error = error };
}
