using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Projects;
using Serilog;

namespace FleetMate.Core.Services.Projects;

/// <summary>
/// OAuth2 Authorization Code + PKCE token service for Azure DevOps.
/// Handles PKCE generation, authorize URL construction, token exchange, refresh, and az CLI integration.
/// This is the Core-layer equivalent of the Mac's DevOpsSsoService.swift.
/// </summary>
public class DevOpsSsoService
{
    /// Azure DevOps resource ID for token acquisition
    public const string AdoResourceId = "499b84ac-1321-427f-aa17-267ca6975798";

    /// OAuth2 redirect URI (native client)
    public const string RedirectUri = "https://login.microsoftonline.com/common/oauth2/nativeclient";

    /// OAuth2 scope for Azure DevOps + offline_access for refresh tokens
    public const string Scope = "499b84ac-1321-427f-aa17-267ca6975798/.default offline_access";

    private readonly string _clientId;
    private readonly string _tenantId;
    private readonly string _authorizeEndpoint;
    private readonly string _tokenEndpoint;

    // PKCE state for the current auth flow
    private string? _codeVerifier;

    // Token state
    public string? AccessToken { get; private set; }
    public string? RefreshToken { get; private set; }
    public DateTime TokenExpiry { get; private set; } = DateTime.MinValue;
    public string? UserName { get; private set; }
    public string? UserEmail { get; private set; }

    /// <summary>True if we have a non-expired access token (with 60s buffer)</summary>
    public bool IsAuthenticated =>
        !string.IsNullOrEmpty(AccessToken) && DateTime.UtcNow.AddSeconds(60) < TokenExpiry;

    /// <summary>True if a refresh token is available (in-memory or MSAL cache)</summary>
    public bool HasRefreshToken =>
        !string.IsNullOrEmpty(RefreshToken) || LoadRefreshTokenFromMsalCache() != null;

    public DevOpsSsoService(string clientId, string tenantId)
    {
        _clientId = clientId;
        _tenantId = tenantId;
        _authorizeEndpoint = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/authorize";
        _tokenEndpoint = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";
        Log.Debug("[DevOps SSO] Service initialized (tenant={TenantId})", tenantId);
    }

    // ── PKCE + Authorize URL ────────────────────────────────────────────────

    /// <summary>
    /// Build an OAuth2 authorize URL with PKCE code challenge.
    /// Generates a fresh code verifier each call, stored internally for ExchangeCodeAsync.
    /// </summary>
    public Uri BuildAuthorizeUrl(string? loginHint = null, string? state = null)
    {
        _codeVerifier = GenerateCodeVerifier();
        var challenge = GenerateCodeChallenge(_codeVerifier);

        var qs = new StringBuilder();
        qs.Append($"?client_id={Uri.EscapeDataString(_clientId)}");
        qs.Append("&response_type=code");
        qs.Append($"&redirect_uri={Uri.EscapeDataString(RedirectUri)}");
        qs.Append("&response_mode=query");
        qs.Append($"&scope={Uri.EscapeDataString(Scope)}");
        qs.Append($"&code_challenge={Uri.EscapeDataString(challenge)}");
        qs.Append("&code_challenge_method=S256");

        if (!string.IsNullOrEmpty(loginHint))
            qs.Append($"&login_hint={Uri.EscapeDataString(loginHint)}");
        if (!string.IsNullOrEmpty(state))
            qs.Append($"&state={Uri.EscapeDataString(state)}");

        return new Uri(_authorizeEndpoint + qs);
    }

    /// <summary>Check if a URL is the OAuth2 redirect URI</summary>
    public static bool IsRedirectUri(Uri url) =>
        url.AbsoluteUri.StartsWith(RedirectUri, StringComparison.OrdinalIgnoreCase);

    /// <summary>Extract the authorization code from a redirect URL</summary>
    public static string? ExtractCode(Uri url)
    {
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        return query["code"];
    }

    /// <summary>Extract an error from a redirect URL</summary>
    public static string? ExtractError(Uri url)
    {
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        var error = query["error"];
        if (string.IsNullOrEmpty(error)) return null;
        var description = query["error_description"];
        return description ?? error;
    }

    // ── Token Exchange ──────────────────────────────────────────────────────

    /// <summary>Exchange an authorization code for access + refresh tokens</summary>
    public async Task<DevOpsSsoResult> ExchangeCodeAsync(string code)
    {
        if (string.IsNullOrEmpty(_codeVerifier))
            return DevOpsSsoResult.Failed("No PKCE code verifier — call BuildAuthorizeUrl first");

        var body = new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = _codeVerifier,
            ["scope"] = Scope
        };

        var result = await PostTokenRequestAsync(body);
        _codeVerifier = null; // consumed
        return result;
    }

    /// <summary>
    /// Acquire a valid access token via the 3-tier refresh chain:
    /// (1) az CLI → (2) MSAL cache refresh token → (3) in-memory refresh token.
    /// </summary>
    public async Task<DevOpsSsoResult> RefreshAccessTokenAsync()
    {
        // 1. Try az CLI first — seamless with Platform SSO
        Log.Debug("[DevOps SSO] Attempting token acquisition via az CLI...");
        var azResult = await AcquireTokenFromAzCliAsync();
        if (azResult is { Success: true })
        {
            Log.Information("[DevOps SSO] az CLI token acquired — user={UserName}", azResult.UserName ?? "unknown");
            return azResult;
        }
        Log.Debug("[DevOps SSO] az CLI failed: {Error} — trying MSAL cache...", azResult?.Error ?? "unknown");

        // 2. Try MSAL cache file (same tokens az CLI uses, read directly)
        var msalRefresh = LoadRefreshTokenFromMsalCache();
        if (!string.IsNullOrEmpty(msalRefresh))
        {
            Log.Debug("[DevOps SSO] Found refresh token in MSAL cache, refreshing...");
            var msalResult = await RefreshWithTokenAsync(msalRefresh);
            if (msalResult.Success)
                return msalResult;
            Log.Debug("[DevOps SSO] MSAL cache refresh failed: {Error}", msalResult.Error ?? "unknown");
        }

        // 3. Try in-memory refresh token (from previous PKCE flow in this session)
        if (!string.IsNullOrEmpty(RefreshToken))
        {
            Log.Debug("[DevOps SSO] Trying in-memory refresh token...");
            var result = await RefreshWithTokenAsync(RefreshToken);
            if (!result.Success)
                RefreshToken = null;
            return result;
        }

        return DevOpsSsoResult.Failed("No token source available (az CLI, MSAL cache, or refresh token)");
    }

    /// <summary>Set tokens from an external source (e.g., after interactive login)</summary>
    public void SetTokens(string accessToken, string? refreshToken = null, int expiresIn = 3600,
        string? userName = null, string? userEmail = null)
    {
        AccessToken = accessToken;
        TokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn);
        UserName = userName;
        UserEmail = userEmail;
        if (!string.IsNullOrEmpty(refreshToken))
            RefreshToken = refreshToken;
    }

    /// <summary>Clear all in-memory token state</summary>
    public void ClearTokens()
    {
        AccessToken = null;
        RefreshToken = null;
        TokenExpiry = DateTime.MinValue;
        UserName = null;
        UserEmail = null;
        Log.Debug("[DevOps SSO] Tokens cleared");
    }

    /// <summary>
    /// Get a valid access token, automatically refreshing if needed.
    /// Returns null if not authenticated and refresh fails.
    /// </summary>
    public async Task<string?> GetValidTokenAsync()
    {
        if (IsAuthenticated)
            return AccessToken;

        if (HasRefreshToken)
        {
            var result = await RefreshAccessTokenAsync();
            if (result is { Success: true, Token: not null })
                return result.Token;
        }

        return null;
    }

    // ── Private — Token Request ─────────────────────────────────────────────

    private async Task<DevOpsSsoResult> RefreshWithTokenAsync(string refreshToken)
    {
        var body = new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = Scope
        };
        return await PostTokenRequestAsync(body);
    }

    private async Task<DevOpsSsoResult> PostTokenRequestAsync(Dictionary<string, string> body)
    {
        try
        {
            using var http = new HttpClient();
            var content = new FormUrlEncodedContent(body);
            var response = await http.PostAsync(_tokenEndpoint, content);
            var responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Log.Error("[DevOps SSO] Token request failed: {Status} {Body}",
                    response.StatusCode, responseText.Length > 500 ? responseText[..500] : responseText);
                return DevOpsSsoResult.Failed($"Token request failed ({response.StatusCode})");
            }

            using var json = JsonDocument.Parse(responseText);
            var root = json.RootElement;

            var accessToken = root.GetProperty("access_token").GetString();
            var expiresIn = root.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;
            var refresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
            var idToken = root.TryGetProperty("id_token", out var idTok) ? idTok.GetString() : null;

            if (string.IsNullOrEmpty(accessToken))
                return DevOpsSsoResult.Failed("Token exchange returned empty access token");

            // Extract user info from access token JWT (more reliable than id_token for DevOps)
            var (userName, userEmail) = ExtractUserInfoFromJwt(accessToken);
            // Fallback to id_token if access token didn't have name
            if (string.IsNullOrEmpty(userName) && !string.IsNullOrEmpty(idToken))
            {
                var (idName, idEmail) = ExtractUserInfoFromJwt(idToken);
                userName ??= idName;
                userEmail ??= idEmail;
            }

            // Store tokens in memory
            AccessToken = accessToken;
            TokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn);
            UserName = userName;
            UserEmail = userEmail;
            if (!string.IsNullOrEmpty(refresh))
                RefreshToken = refresh;

            Log.Information("[DevOps SSO] Token acquired — user={UserName}, expires in {ExpiresIn}s",
                userName ?? "unknown", expiresIn);

            return DevOpsSsoResult.Succeeded(
                accessToken,
                TokenExpiry,
                refreshToken: refresh,
                userName: userName,
                userEmail: userEmail);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DevOps SSO] Token request error");
            return DevOpsSsoResult.Failed($"Token request error: {ex.Message}");
        }
    }

    // ── JWT Parsing ─────────────────────────────────────────────────────────

    /// <summary>Extract user info (name, email/UPN) from a JWT token payload</summary>
    public static (string? Name, string? Email) ExtractUserInfoFromJwt(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return (null, null);

            var payload = parts[1]
                .Replace('-', '+')
                .Replace('_', '/');
            var remainder = payload.Length % 4;
            if (remainder > 0) payload += new string('=', 4 - remainder);

            var bytes = Convert.FromBase64String(payload);
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;

            string? name = null;
            foreach (var claim in new[] { "name", "preferred_username", "unique_name", "given_name" })
            {
                if (root.TryGetProperty(claim, out var val) && val.ValueKind == JsonValueKind.String)
                {
                    var v = val.GetString();
                    if (!string.IsNullOrEmpty(v)) { name = v; break; }
                }
            }

            string? email = null;
            foreach (var claim in new[] { "upn", "email", "preferred_username", "unique_name" })
            {
                if (root.TryGetProperty(claim, out var val) && val.ValueKind == JsonValueKind.String)
                {
                    var v = val.GetString();
                    if (!string.IsNullOrEmpty(v)) { email = v; break; }
                }
            }

            return (name, email);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[DevOps SSO] Failed to extract user info from JWT");
            return (null, null);
        }
    }

    // ── az CLI Token Acquisition ────────────────────────────────────────────

    /// <summary>
    /// Acquire a DevOps access token via az CLI.
    /// Seamless with Platform SSO — no prompts needed.
    /// </summary>
    private async Task<DevOpsSsoResult> AcquireTokenFromAzCliAsync()
    {
        try
        {
            var azPath = FindAzureCli();
            if (azPath == null)
                return DevOpsSsoResult.Failed("az CLI not found");

            var psi = new ProcessStartInfo
            {
                FileName = azPath,
                Arguments = $"account get-access-token --resource {AdoResourceId} --output json",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return DevOpsSsoResult.Failed("Failed to start az CLI process");

            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                return DevOpsSsoResult.Failed($"az CLI exited with code {process.ExitCode}");

            if (string.IsNullOrWhiteSpace(stdout))
                return DevOpsSsoResult.Failed("az CLI returned empty output");

            using var json = JsonDocument.Parse(stdout);
            var root = json.RootElement;

            var accessToken = root.GetProperty("accessToken").GetString();
            if (string.IsNullOrEmpty(accessToken))
                return DevOpsSsoResult.Failed("az CLI returned no access token");

            // Calculate expiry
            int expiresIn = 3600;
            if (root.TryGetProperty("expires_on", out var expiresOn) && expiresOn.ValueKind == JsonValueKind.Number)
            {
                var ts = expiresOn.GetInt64();
                expiresIn = Math.Max((int)(ts - DateTimeOffset.UtcNow.ToUnixTimeSeconds()), 60);
            }
            else if (root.TryGetProperty("expiresOn", out var expiresOnStr) && expiresOnStr.ValueKind == JsonValueKind.String)
            {
                if (DateTime.TryParse(expiresOnStr.GetString(), out var parsed))
                    expiresIn = Math.Max((int)(parsed.ToUniversalTime() - DateTime.UtcNow).TotalSeconds, 60);
            }

            var (userName, userEmail) = ExtractUserInfoFromJwt(accessToken);

            // Store in memory
            AccessToken = accessToken;
            TokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn);
            UserName = userName;
            UserEmail = userEmail;

            return DevOpsSsoResult.Succeeded(
                accessToken,
                TokenExpiry,
                userName: userName,
                userEmail: userEmail);
        }
        catch (Exception ex)
        {
            return DevOpsSsoResult.Failed($"az CLI: {ex.Message}");
        }
    }

    // ── MSAL Token Cache (fallback) ─────────────────────────────────────────

    /// <summary>
    /// Read refresh token from az CLI's MSAL token cache file.
    /// Path: %USERPROFILE%\.azure\msal_token_cache.json
    /// </summary>
    private string? LoadRefreshTokenFromMsalCache()
    {
        try
        {
            var cachePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".azure", "msal_token_cache.json");

            if (!File.Exists(cachePath)) return null;

            var data = File.ReadAllBytes(cachePath);
            using var json = JsonDocument.Parse(data);

            if (!json.RootElement.TryGetProperty("RefreshToken", out var refreshTokens))
                return null;

            foreach (var entry in refreshTokens.EnumerateObject())
            {
                var token = entry.Value;
                if (token.TryGetProperty("client_id", out var clientIdProp) &&
                    clientIdProp.GetString() == _clientId &&
                    token.TryGetProperty("secret", out var secretProp))
                {
                    var secret = secretProp.GetString();
                    if (!string.IsNullOrEmpty(secret))
                        return secret;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[DevOps SSO] Failed to read MSAL token cache");
        }

        return null;
    }

    // ── PKCE Helpers ────────────────────────────────────────────────────────

    public static string GenerateCodeVerifier()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    public static string GenerateCodeChallenge(string verifier)
    {
        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(challengeBytes);
    }

    public static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    // ── az CLI Discovery ────────────────────────────────────────────────────

    private static string? FindAzureCli()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"),
            @"C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd",
            @"C:\Program Files (x86)\Microsoft SDKs\Azure\CLI2\wbin\az.cmd"
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var azCmd = Path.Combine(dir, "az.cmd");
            var azExe = Path.Combine(dir, "az.exe");
            if (File.Exists(azCmd)) return azCmd;
            if (File.Exists(azExe)) return azExe;
        }

        return null;
    }
}
