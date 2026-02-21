using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FleetMate.Config;
using Serilog;

namespace FleetMate.Core.Services;

/// <summary>
/// Centralized GitHub token resolution for all GitHub API clients.
///
/// Priority order (first source to return a non-empty token wins):
/// 1. config token — explicit override in config.yaml; always respected
/// 2. gh CLI — `gh auth token`; zero-config on developer machines; OS-keychain-backed
/// 3. GITHUB_TOKEN / GH_TOKEN — environment variables; CI/automation/devcontainers
/// 4. Windows Credential Store — DPAPI-encrypted token from a previous Device Flow
/// 5. OAuth Device Flow — interactive first-run; requires OauthClientId in config;
///    stores the resulting token in the credential store for all future runs
/// </summary>
public class GitHubTokenSource : IDisposable
{
    private readonly GitHubProviderConfig _config;
    private readonly HttpClient _http;
    private string? _cachedToken;

    private static readonly string TokenFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FleetMate", "github-token.dat");

    /// <summary>
    /// Called during Device Flow with (user_code, verification_uri, ct).
    /// Open the URI in a browser and display the user code to the user.
    /// </summary>
    public Func<string, string, CancellationToken, Task>? DeviceFlowPrompt { get; set; }

    public GitHubTokenSource(GitHubProviderConfig config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("User-Agent", "FleetMate");
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>Returns a valid token, trying all sources in priority order.</summary>
    public async Task<string?> GetTokenAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(_cachedToken)) return _cachedToken;

        // 1. Explicit config token (user override — always wins)
        if (!string.IsNullOrEmpty(_config.Token)) return Cache(_config.Token!);

        // 2. gh CLI — seamless; wraps OS keychain internally
        if (_config.UseGhCli)
        {
            var ghToken = await GetTokenFromGhCliAsync();
            if (!string.IsNullOrEmpty(ghToken)) return Cache(ghToken!);
        }

        // 3. Environment variables (CI / automation)
        var envToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
                    ?? Environment.GetEnvironmentVariable("GH_TOKEN");
        if (!string.IsNullOrEmpty(envToken)) return Cache(envToken!);

        // 4. Windows Credential Store (DPAPI-encrypted token from previous Device Flow)
        var stored = LoadFromCredentialStore();
        if (!string.IsNullOrEmpty(stored)) return Cache(stored!);

        // 5. OAuth Device Flow (interactive first-run)
        if (!string.IsNullOrEmpty(_config.OauthClientId) && DeviceFlowPrompt != null)
        {
            var deviceToken = await PerformDeviceFlowAsync(_config.OauthClientId!, ct);
            if (!string.IsNullOrEmpty(deviceToken))
            {
                SaveToCredentialStore(deviceToken!);
                return Cache(deviceToken!);
            }
        }

        return null;
    }

    /// <summary>Clears the in-memory cached token (call after a 401 to force re-auth).</summary>
    public void Invalidate() => _cachedToken = null;

    private string Cache(string token) { _cachedToken = token; return token; }

    private static async Task<string?> GetTokenFromGhCliAsync()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = "auth token",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process == null) return null;
            var token = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return process.ExitCode == 0 ? token.Trim() : null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "gh CLI not available");
            return null;
        }
    }

    private static void SaveToCredentialStore(string token)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return;
            var encrypted = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(TokenFilePath)!);
            File.WriteAllBytes(TokenFilePath, encrypted);
        }
        catch (Exception ex) { Log.Debug(ex, "Failed to save token to credential store"); }
    }

    private static string? LoadFromCredentialStore()
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !File.Exists(TokenFilePath)) return null;
            var decrypted = ProtectedData.Unprotect(
                File.ReadAllBytes(TokenFilePath), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex) { Log.Debug(ex, "Failed to load token from credential store"); return null; }
    }

    private async Task<string?> PerformDeviceFlowAsync(string clientId, CancellationToken ct)
    {
        // Step 1: Request device & user codes
        var codeReq = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/device/code")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { client_id = clientId, scope = "repo read:org" }),
                Encoding.UTF8, "application/json")
        };

        var codeResp = await _http.SendAsync(codeReq, ct);
        if (!codeResp.IsSuccessStatusCode) return null;

        using var codeDoc = await JsonDocument.ParseAsync(
            await codeResp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var codeRoot = codeDoc.RootElement;

        if (!codeRoot.TryGetProperty("device_code", out var dcProp) ||
            !codeRoot.TryGetProperty("user_code", out var ucProp) ||
            !codeRoot.TryGetProperty("verification_uri", out var uriProp))
            return null;

        var deviceCode = dcProp.GetString()!;
        var userCode = ucProp.GetString()!;
        var verificationUri = uriProp.GetString()!;
        var interval = codeRoot.TryGetProperty("interval", out var ivProp) ? ivProp.GetInt32() : 5;
        var expiresIn = codeRoot.TryGetProperty("expires_in", out var exProp) ? exProp.GetInt32() : 900;

        // Step 2: Notify caller — open browser, show code
        if (DeviceFlowPrompt != null)
            await DeviceFlowPrompt(userCode, verificationUri, ct);

        // Step 3: Poll until authorized, denied, or expired
        var pollInterval = interval;
        var deadline = DateTime.UtcNow.AddSeconds(expiresIn);

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(pollInterval * 1000, ct);

            var pollReq = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        client_id = clientId,
                        device_code = deviceCode,
                        grant_type = "urn:ietf:params:oauth:grant-type:device_code"
                    }),
                    Encoding.UTF8, "application/json")
            };

            var pollResp = await _http.SendAsync(pollReq, ct);
            if (!pollResp.IsSuccessStatusCode) continue;

            using var pollDoc = await JsonDocument.ParseAsync(
                await pollResp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var pollRoot = pollDoc.RootElement;

            if (pollRoot.TryGetProperty("access_token", out var tokenProp))
            {
                var t = tokenProp.GetString();
                if (!string.IsNullOrEmpty(t)) return t;
            }

            if (pollRoot.TryGetProperty("error", out var errProp))
            {
                switch (errProp.GetString())
                {
                    case "slow_down":
                        pollInterval += pollRoot.TryGetProperty("interval", out var newIv)
                            ? newIv.GetInt32() : 5;
                        break;
                    case "expired_token":
                    case "access_denied":
                        return null;
                }
            }
        }

        return null;
    }

    public void Dispose() => _http.Dispose();
}
