using System.Net;
using System.Security.Principal;
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
        _baseUrl = ServiceUri.Normalize(baseUrl);
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
        var root = ServiceUri.Normalize(baseUrl);

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
        var root = ServiceUri.Normalize(baseUrl);
        const string apiSegment = "/TDWebApi";
        if (root.EndsWith(apiSegment, StringComparison.OrdinalIgnoreCase))
            root = root[..^apiSegment.Length];

        return $"{root.TrimEnd('/')}/TDWorkManagement/";
    }

    /// <summary>
    /// Phase 1: Attempt silent SSO using Windows Negotiate/Kerberos credentials.
    /// No UI required — pure HTTP call chain.
    /// </summary>
    /// <param name="expectedUpn">The Windows account the session must belong to;
    /// resolved from the device when not given. A JWT that cannot be confirmed
    /// as that account's fails the attempt (<see cref="TdxSsoResult.IdentityRefused"/>)
    /// and is not kept.</param>
    public async Task<TdxSsoResult> TrySilentSsoAsync(CancellationToken ct = default, string? expectedUpn = null)
    {
        var loginSsoUrl = BuildLoginSsoUrl(_baseUrl);
        var entryUrl = BuildEntryUrl(_baseUrl);
        expectedUpn ??= await TdxSsoIdentity.ResolveWindowsUpnAsync(ct);

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
                var result = await TryExtractJwt(resp, expectedUpn, ct);
                if (result != null) return result;
            }

            // Step 2: Follow full SAML redirect chain
            await client.GetAsync(entryUrl, ct);

            // Step 3: Retry loginSSO with SAML cookies
            var jwtResp = await client.GetAsync(loginSsoUrl, ct);
            if (jwtResp.IsSuccessStatusCode)
            {
                var result = await TryExtractJwt(jwtResp, expectedUpn, ct);
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

    private async Task<TdxSsoResult?> TryExtractJwt(HttpResponseMessage response, string? expectedUpn, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        var token = body.Trim().Trim('"');

        if (!LooksLikeJwt(token))
            return null;

        var result = TdxSsoIdentity.Verify(token, expectedUpn);
        if (!result.Success)
        {
            Log.Error("[tdx-sso-core] {Reason}; token discarded, sign-in refused", result.Error);
            return result;
        }

        _token = token;
        _tokenExpiry = result.Expiry;
        _userName = result.UserName;
        _userEmail = result.UserEmail;
        Log.Information("[tdx-sso-core] ✓ JWT acquired — user={UserName}, expires={Expiry:u}",
            result.UserName ?? "(unknown)", _tokenExpiry);
        return result;
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

    /// <summary>
    /// The sign-in finished, but its identity could not be confirmed as the
    /// Windows account's: it is someone else, or one side has no address to
    /// compare. That is final: another attempt in the same session reaches the
    /// same account, so callers stop rather than move on to another phase.
    /// </summary>
    public bool IdentityRefused { get; init; }

    public const string NoAddressInTokenReason =
        "TDX sign-in refused: the session carries no email or UPN to confirm whose it is";

    public const string UnknownExpectedAddressReason =
        "TDX sign-in refused: the signed-in user's own address could not be determined";

    public static TdxSsoResult Failed(string error) => new() { Success = false, Error = error };

    /// <summary>A sign-in refused by the identity check; no token is carried.</summary>
    public static TdxSsoResult Refused(string reason) =>
        new() { Success = false, IdentityRefused = true, Error = reason };
}

/// <summary>
/// Checks that a TeamDynamix session belongs to the person signed in to
/// Windows. Entra can hold more than one account, and the JWT that loginsso
/// returns is for whichever one the chain ended on. A sign-in is accepted only
/// when the token's address is known and equals the Windows account's; anything
/// else is refused outright: the token is never stored, and the sign-in is
/// reported as failed.
/// </summary>
public static class TdxSsoIdentity
{
    /// <summary>
    /// Turn a JWT into a sign-in result, checked against the expected address.
    /// <list type="bullet">
    /// <item>A token whose email/UPN matches (ignoring case and surrounding space) succeeds.</item>
    /// <item>A token for a different address fails, and the token is dropped.</item>
    /// <item>With no expected address there is nothing to compare against, so
    /// the sign-in fails.</item>
    /// <item>A token with no address claim cannot be checked, so the sign-in fails.</item>
    /// </list>
    /// </summary>
    public static TdxSsoResult Verify(string token, string? expectedUpn)
    {
        var (name, claimed) = TdxSsoService.ExtractUserInfoFromJwt(token);
        var expected = Normalize(expectedUpn);
        var actual = Normalize(claimed);

        if (expected == null)
            return TdxSsoResult.Refused(TdxSsoResult.UnknownExpectedAddressReason);
        if (actual == null)
            return TdxSsoResult.Refused(TdxSsoResult.NoAddressInTokenReason);
        if (actual != expected)
            return TdxSsoResult.Refused($"TDX session belongs to {actual}; expected {expected}");

        return new TdxSsoResult
        {
            Success = true,
            Token = token,
            UserName = name,
            UserEmail = actual,
            Expiry = TdxSsoService.ReadExpiry(token),
        };
    }

    /// <summary>
    /// The Windows account's address, from the logon first (<c>GetUserNameEx</c>),
    /// then the broker's operating-system account, then the Entra account in the
    /// registry (<see cref="WindowsAccount"/>). Null only when every source is empty.
    /// </summary>
    public static async Task<string?> ResolveWindowsUpnAsync(CancellationToken ct = default)
    {
        if (EntraTokenSource.Shared is { } source)
            return await source.ResolveAccountUpnAsync(ct);
        var (upn, from) = await WindowsAccount.FirstAsync(WindowsAccount.Sources(broker: null), ct);
        if (upn != null) Log.Information("[tdx-sso] Windows account {Upn} (from the {Source})", upn, from);
        return upn;
    }

    /// <summary>A trimmed, lower-cased address, or null for anything that is not one.</summary>
    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(trimmed) || !trimmed.Contains('@') ? null : trimmed;
    }
}
