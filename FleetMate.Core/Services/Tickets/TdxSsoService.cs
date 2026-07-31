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
    /// The Web API's own SSO entry point, whether or not the configured base URL
    /// already names <c>/TDWebApi</c>.
    ///
    /// This must be the API endpoint, not the web UI. Pointing at
    /// <c>/TDWorkManagement/</c> logs you into TDNext and leaves a *web session
    /// cookie* behind — not an API credential, which is why a scraped "SSO token"
    /// gets rejected with a 400 and the app silently falls back to the service
    /// account.
    ///
    /// <c>GET /TDWebApi/api/auth/loginsso</c> redirects into Shibboleth → Entra
    /// and, once the assertion comes back, returns a real Bearer JWT as the
    /// response body. That token carries the signed-in person's identity, so
    /// their actions are attributed to them rather than to a shared account.
    /// </summary>
    public static string BuildLoginSsoUrl(string baseUrl)
    {
        var root = baseUrl.TrimEnd('/');

        // Strip a trailing /TDWebApi (any casing) so appending it back is
        // idempotent. Previously the root was computed and then thrown away, so
        // a base URL *without* /TDWebApi produced a 404 that looked like an
        // auth failure.
        const string apiSegment = "/TDWebApi";
        if (root.EndsWith(apiSegment, StringComparison.OrdinalIgnoreCase))
            root = root[..^apiSegment.Length];

        return $"{root.TrimEnd('/')}{apiSegment}/api/auth/loginsso";
    }

    /// <summary>The TDX web entry point that drives the SAML redirect chain.</summary>
    public static string BuildEntryUrl(string baseUrl)
    {
        var root = baseUrl.TrimEnd('/');
        const string apiSegment = "/TDWebApi";
        if (root.EndsWith(apiSegment, StringComparison.OrdinalIgnoreCase))
            root = root[..^apiSegment.Length];

        return $"{root.TrimEnd('/')}/TDWorkManagement/";
    }

    /// <summary>
    /// Phase 1: Attempt silent SSO using Windows Negotiate/Kerberos credentials.
    /// No UI required — pure HTTP call chain.
    /// </summary>
    public async Task<TdxSsoResult> TrySilentSsoAsync(CancellationToken ct = default)
    {
        var loginSsoUrl = BuildLoginSsoUrl(_baseUrl);
        var entryUrl = BuildEntryUrl(_baseUrl);

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

        if (!LooksLikeJwt(token))
            return null;

        var (name, email) = ExtractUserInfoFromJwt(token);
        _token = token;
        _tokenExpiry = ReadExpiry(token);
        _userName = name;
        _userEmail = email;
        Log.Information("[tdx-sso-core] ✓ JWT acquired — user={UserName}, expires={Expiry:u}",
            name ?? "(unknown)", _tokenExpiry);
        return new TdxSsoResult
        {
            Success = true,
            Token = token,
            UserName = name,
            UserEmail = email,
            Expiry = _tokenExpiry
        };
    }

    /// <summary>
    /// A JWT is three dot-separated base64url segments starting <c>eyJ</c>.
    /// Anything else is an error page or a redirect body, and must not be handed
    /// out as a credential — an <c>eyJ</c> prefix alone was enough to let one
    /// through.
    /// </summary>
    public static bool LooksLikeJwt(string token) =>
        token.StartsWith("eyJ", StringComparison.Ordinal)
        && token.Split('.').Length == 3
        && token.Length > 20;

    /// <summary>
    /// Real expiry from the token's own <c>exp</c> claim.
    ///
    /// The previous fixed 23-hour guess outlived any shorter-lived token TDX
    /// issues, so <c>HasValidToken</c> would keep reporting healthy while every
    /// call came back 401.
    /// </summary>
    public static DateTime ReadExpiry(string token)
    {
        const int fallbackHours = 23;
        try
        {
            var payload = DecodePayload(token);
            if (payload == null) return DateTime.UtcNow.AddHours(fallbackHours);

            using var json = JsonDocument.Parse(payload);
            if (json.RootElement.TryGetProperty("exp", out var exp) &&
                exp.ValueKind == JsonValueKind.Number &&
                exp.TryGetInt64(out var seconds))
            {
                // Refresh a little early so a token never expires mid-flight.
                return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.AddMinutes(-5);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[tdx-sso-core] Could not read exp claim; assuming {Hours}h", fallbackHours);
        }

        return DateTime.UtcNow.AddHours(fallbackHours);
    }

    /// <summary>Base64url-decode a JWT's payload segment.</summary>
    private static byte[]? DecodePayload(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2) return null;

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        var remainder = payload.Length % 4;
        if (remainder > 0) payload += new string('=', 4 - remainder);

        return Convert.FromBase64String(payload);
    }

    /// <summary>Extract user info from a JWT payload (no signature verification).</summary>
    public static (string? userName, string? userEmail) ExtractUserInfoFromJwt(string token)
    {
        try
        {
            var bytes = DecodePayload(token);
            if (bytes == null) return (null, null);

            using var json = JsonDocument.Parse(bytes);
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
