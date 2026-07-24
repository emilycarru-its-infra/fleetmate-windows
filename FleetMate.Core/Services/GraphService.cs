using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Identity;
using FleetMate.Core.Config;
using Serilog;

namespace FleetMate.Core.Services;

/// <summary>
/// Microsoft Graph service for Intune devices and Entra ID users/groups
/// Uses Azure CLI SSO for authentication
/// </summary>
public class GraphService : IDisposable
{
    private readonly HttpClient _client;
    private readonly GraphConfig _config;
    private readonly JsonSerializerOptions _jsonOptions;
    private string? _cachedToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    // Multi-SP: per-scope token caches
    private string? _devicesToken;
    private DateTime _devicesTokenExpiry = DateTime.MinValue;
    private string? _systemsToken;
    private DateTime _systemsTokenExpiry = DateTime.MinValue;

    private const string GraphScope = "https://graph.microsoft.com/.default";

    // Caches
    private readonly Dictionary<string, (EntraUser user, DateTime expiry)> _userCache = new();
    private readonly Dictionary<string, (EntraGroup group, DateTime expiry)> _groupCache = new();
    private readonly TimeSpan _cacheDuration;

    // Microsoft Graph resource ID
    private const string GraphResourceId = "https://graph.microsoft.com";

    // When true, Graph calls run inside an aze elevation session (the domain
    // identity's token never leaves Azure). Default on; FLEETMATE_GRAPH_TRANSPORT=direct
    // falls back to a local az-minted token + direct HTTP.
    private readonly bool _useElevation;

    public GraphService(GraphConfig config, ElevationConfig? elevation = null)
    {
        _config = config;
        _cacheDuration = TimeSpan.FromMinutes(config.CacheMinutes);

        _useElevation = !string.Equals(
            Environment.GetEnvironmentVariable("FLEETMATE_GRAPH_TRANSPORT"), "direct",
            StringComparison.OrdinalIgnoreCase);

        _client = _useElevation
            ? new HttpClient(new ElevationHttpHandler(elevation ?? new ElevationConfig()))
            {
                BaseAddress = new Uri("https://graph.microsoft.com/v1.0/"),
                Timeout = TimeSpan.FromSeconds(120) // allow for the one-time ~30s container cold start
            }
            : new HttpClient
            {
                BaseAddress = new Uri("https://graph.microsoft.com/v1.0/"),
                Timeout = TimeSpan.FromSeconds(60)
            };

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    #region Authentication

    /// <summary>
    /// Get access token using service principal or Azure CLI SSO
    /// </summary>
    private async Task<string?> GetAccessTokenAsync()
    {
        if (_cachedToken != null && DateTime.UtcNow < _tokenExpiry)
        {
            return _cachedToken;
        }

        try
        {
            if (!_config.UseAzureCliAuth &&
                !string.IsNullOrWhiteSpace(_config.TenantId) &&
                !string.IsNullOrWhiteSpace(_config.ClientId) &&
                !string.IsNullOrWhiteSpace(_config.ClientSecret))
            {
                var clientToken = await GetClientCredentialTokenAsync(
                    _config.TenantId,
                    _config.ClientId,
                    _config.ClientSecret);

                if (!string.IsNullOrEmpty(clientToken))
                {
                    _cachedToken = clientToken;
                    _tokenExpiry = DateTime.UtcNow.AddMinutes(55);
                    Log.Debug("Acquired Microsoft Graph access token via client credentials");
                    return _cachedToken;
                }
            }

            var azPath = FindAzureCli();
            if (azPath == null)
            {
                Log.Error("Azure CLI (az) not found. Please install Azure CLI.");
                return null;
            }

            var psi = new ProcessStartInfo
            {
                FileName = azPath,
                Arguments = $"account get-access-token --resource {GraphResourceId} --query accessToken -o tsv",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                Log.Error("Failed to start az CLI process");
                return null;
            }

            var token = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                Log.Error("Azure CLI failed: {Error}", error);
                return null;
            }

            _cachedToken = token.Trim();
            _tokenExpiry = DateTime.UtcNow.AddMinutes(55);

            Log.Debug("Acquired Microsoft Graph access token via Azure CLI");
            return _cachedToken;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get Microsoft Graph access token");
            return null;
        }
    }

    /// <summary>
    /// Get an access token scoped to a specific purpose.
    /// Falls back to the default SP / az CLI if no dedicated SP is configured.
    /// </summary>
    private async Task<string?> GetScopedAccessTokenAsync(string scope)
    {
        switch (scope)
        {
            case "devices" when _config.IsDevicesSpConfigured:
                if (_devicesToken != null && DateTime.UtcNow < _devicesTokenExpiry) return _devicesToken;
                var dt = await GetClientCredentialTokenAsync(_config.TenantId!, _config.DevicesClientId!, _config.DevicesClientSecret!);
                if (!string.IsNullOrEmpty(dt)) { _devicesToken = dt; _devicesTokenExpiry = DateTime.UtcNow.AddMinutes(55); }
                return _devicesToken ?? await GetAccessTokenAsync();

            case "systems" when _config.IsSystemsSpConfigured:
                if (_systemsToken != null && DateTime.UtcNow < _systemsTokenExpiry) return _systemsToken;
                var st = await GetClientCredentialTokenAsync(_config.TenantId!, _config.SystemsClientId!, _config.SystemsClientSecret!);
                if (!string.IsNullOrEmpty(st)) { _systemsToken = st; _systemsTokenExpiry = DateTime.UtcNow.AddMinutes(55); }
                return _systemsToken ?? await GetAccessTokenAsync();

            default:
                return await GetAccessTokenAsync();
        }
    }

    private async Task<string?> GetClientCredentialTokenAsync(string tenantId, string clientId, string clientSecret)
    {
        try
        {
            using var tokenClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            var tokenEndpoint = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["grant_type"] = "client_credentials",
                ["scope"] = GraphScope
            });

            var response = await tokenClient.PostAsync(tokenEndpoint, content);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Log.Error("Graph token request failed: {Status} - {Error}", response.StatusCode, error);
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            if (doc.RootElement.TryGetProperty("access_token", out var tokenProp))
            {
                return tokenProp.GetString();
            }

            Log.Error("Graph token response missing access_token");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get Microsoft Graph access token via client credentials");
            return null;
        }
    }

    private async Task<bool> SetAuthorizationAsync()
    {
        // In elevation mode the in-session `az rest` authenticates as the domain
        // identity; no local token is needed and none is attached.
        if (_useElevation) return true;

        var token = await GetAccessTokenAsync();
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return true;
    }

    /// <summary>
    /// Read a response body for error logging (truncated). In elevation mode the
    /// <see cref="ElevationHttpHandler"/> packs the real failure — e.g. "aze elevation
    /// is not configured" or the underlying Graph error — into a BadGateway body, so
    /// surfacing it here stops elevation/auth failures from silently looking like a
    /// missing user or group.
    /// </summary>
    private static async Task<string> ReadErrorBodyAsync(HttpResponseMessage response)
    {
        try
        {
            var body = (await response.Content.ReadAsStringAsync())?.Trim() ?? "";
            return body.Length > 600 ? body[..600] + "…" : body;
        }
        catch { return "(no body)"; }
    }

    private static string? FindAzureCli()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"),
            @"C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd",
            @"C:\Program Files (x86)\Microsoft SDKs\Azure\CLI2\wbin\az.cmd"
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var azCmd = Path.Combine(dir, "az.cmd");
            if (File.Exists(azCmd)) return azCmd;
        }
        return null;
    }

    #endregion

    #region Intune Devices

    /// <summary>
    /// Get all managed devices from Intune
    /// </summary>
    public async Task<List<IntuneDevice>> GetManagedDevicesAsync(string? filter = null, int limit = 100)
    {
        if (!await SetAuthorizationAsync())
        {
            Log.Warning("Failed to authenticate to Microsoft Graph");
            return new List<IntuneDevice>();
        }

        var allDevices = new List<IntuneDevice>();
        var url = "deviceManagement/managedDevices";

        var queryParams = new List<string> { $"$top={Math.Min(limit, _config.PageSize)}" };
        if (!string.IsNullOrEmpty(filter))
        {
            queryParams.Add($"$filter={Uri.EscapeDataString(filter)}");
        }
        url += "?" + string.Join("&", queryParams);

        try
        {
            while (!string.IsNullOrEmpty(url) && allDevices.Count < limit)
            {
                var response = await _client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Log.Warning("Failed to get managed devices: {Status} - {Error}", response.StatusCode, error);
                    break;
                }

                var result = await response.Content.ReadFromJsonAsync<IntuneDeviceListResponse>(_jsonOptions);
                if (result?.Value != null)
                {
                    allDevices.AddRange(result.Value);
                }

                // Handle pagination
                url = result?.NextLink;
                if (url != null && url.StartsWith(_client.BaseAddress!.ToString()))
                {
                    url = url.Substring(_client.BaseAddress.ToString().Length);
                }
            }

            Log.Debug("Retrieved {Count} managed devices from Intune", allDevices.Count);
            return allDevices.Take(limit).ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get managed devices");
            return new List<IntuneDevice>();
        }
    }

    /// <summary>
    /// Get a device by serial number
    /// </summary>
    public async Task<IntuneDevice?> GetDeviceBySerialAsync(string serialNumber)
    {
        var filter = $"serialNumber eq '{serialNumber}'";
        var devices = await GetManagedDevicesAsync(filter, 1);
        return devices.FirstOrDefault();
    }

    /// <summary>
    /// Get a device by name
    /// </summary>
    public async Task<IntuneDevice?> GetDeviceByNameAsync(string deviceName)
    {
        var filter = $"deviceName eq '{deviceName}'";
        var devices = await GetManagedDevicesAsync(filter, 1);
        return devices.FirstOrDefault();
    }

    /// <summary>
    /// Search devices by name pattern
    /// </summary>
    public async Task<List<IntuneDevice>> SearchDevicesAsync(string query, int limit = 50)
    {
        var filter = $"startswith(deviceName, '{query}')";
        return await GetManagedDevicesAsync(filter, limit);
    }

    /// <summary>
    /// Get compliance policy states for a device
    /// </summary>
    public async Task<List<DeviceCompliancePolicyState>> GetDeviceComplianceAsync(string deviceId)
    {
        if (!await SetAuthorizationAsync())
        {
            return new List<DeviceCompliancePolicyState>();
        }

        try
        {
            var url = $"deviceManagement/managedDevices/{deviceId}/deviceCompliancePolicyStates";
            var response = await _client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Failed to get compliance states for device {DeviceId}: {Status}", deviceId, response.StatusCode);
                return new List<DeviceCompliancePolicyState>();
            }

            var result = await response.Content.ReadFromJsonAsync<CompliancePolicyStatesResponse>(_jsonOptions);
            return result?.Value ?? new List<DeviceCompliancePolicyState>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get compliance states for device {DeviceId}", deviceId);
            return new List<DeviceCompliancePolicyState>();
        }
    }

    /// <summary>
    /// Get non-compliant devices
    /// </summary>
    public async Task<List<IntuneDevice>> GetNonCompliantDevicesAsync(int limit = 100)
    {
        var filter = "complianceState eq 'noncompliant'";
        return await GetManagedDevicesAsync(filter, limit);
    }

    /// <summary>
    /// Get a device by ID
    /// </summary>
    public async Task<IntuneDevice?> GetDeviceByIdAsync(string deviceId)
    {
        if (!await SetAuthorizationAsync())
        {
            return null;
        }

        try
        {
            var url = $"deviceManagement/managedDevices/{deviceId}";
            var response = await _client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Failed to get device {DeviceId}: {Status} - {Error}", deviceId, response.StatusCode, await ReadErrorBodyAsync(response));
                return null;
            }

            return await response.Content.ReadFromJsonAsync<IntuneDevice>(_jsonOptions);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get device {DeviceId}", deviceId);
            return null;
        }
    }

    #endregion

    #region Intune Device Actions

    /// <summary>
    /// Result of a device action
    /// </summary>
    public class DeviceActionResult
    {
        public bool Success { get; set; }
        public string DeviceId { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string? Message { get; set; }
    }

    /// <summary>
    /// Force sync a managed device
    /// </summary>
    public async Task<DeviceActionResult> SyncDeviceAsync(string deviceId)
    {
        if (!await SetAuthorizationAsync())
        {
            return new DeviceActionResult { Success = false, DeviceId = deviceId, Action = "syncDevice", Message = "Not authenticated" };
        }

        try
        {
            var url = $"deviceManagement/managedDevices/{deviceId}/syncDevice";
            var response = await _client.PostAsync(url, null);

            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                Log.Information("Sync triggered for device {DeviceId}", deviceId);
                return new DeviceActionResult { Success = true, DeviceId = deviceId, Action = "syncDevice" };
            }

            var error = await response.Content.ReadAsStringAsync();
            Log.Warning("Failed to sync device {DeviceId}: {Status} - {Error}", deviceId, response.StatusCode, error);
            return new DeviceActionResult { Success = false, DeviceId = deviceId, Action = "syncDevice", Message = error };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to sync device {DeviceId}", deviceId);
            return new DeviceActionResult { Success = false, DeviceId = deviceId, Action = "syncDevice", Message = ex.Message };
        }
    }

    /// <summary>
    /// Sync multiple devices in parallel
    /// </summary>
    public async Task<List<DeviceActionResult>> SyncDevicesAsync(IEnumerable<string> deviceIds)
    {
        var tasks = deviceIds.Select(SyncDeviceAsync);
        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    /// <summary>
    /// Reboot a managed device
    /// </summary>
    public async Task<DeviceActionResult> RebootDeviceAsync(string deviceId)
    {
        if (!await SetAuthorizationAsync())
        {
            return new DeviceActionResult { Success = false, DeviceId = deviceId, Action = "rebootNow", Message = "Not authenticated" };
        }

        try
        {
            var url = $"deviceManagement/managedDevices/{deviceId}/rebootNow";
            var response = await _client.PostAsync(url, null);

            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                Log.Information("Reboot triggered for device {DeviceId}", deviceId);
                return new DeviceActionResult { Success = true, DeviceId = deviceId, Action = "rebootNow" };
            }

            var error = await response.Content.ReadAsStringAsync();
            Log.Warning("Failed to reboot device {DeviceId}: {Status} - {Error}", deviceId, response.StatusCode, error);
            return new DeviceActionResult { Success = false, DeviceId = deviceId, Action = "rebootNow", Message = error };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to reboot device {DeviceId}", deviceId);
            return new DeviceActionResult { Success = false, DeviceId = deviceId, Action = "rebootNow", Message = ex.Message };
        }
    }

    /// <summary>
    /// Reboot multiple devices
    /// </summary>
    public async Task<List<DeviceActionResult>> RebootDevicesAsync(IEnumerable<string> deviceIds)
    {
        var tasks = deviceIds.Select(RebootDeviceAsync);
        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    /// <summary>
    /// Remote lock a device with optional PIN
    /// </summary>
    // Defense-in-depth: destructive fleet actions (remoteLock/wipe/retire/remediation)
    // must be invoked with an explicit confirmed flag. Callers gate on their own
    // confirmation first (CLI --confirm, GUI MessageBox) then pass confirmed: true; a
    // caller that forgets is refused here rather than silently firing the action.
    private static DeviceActionResult? RequireConfirmation(bool confirmed, string action, string? deviceId = null)
    {
        if (confirmed) return null;
        Log.Warning("Refused unconfirmed destructive action {Action} for {Target}", action, deviceId ?? "(fleet)");
        return new DeviceActionResult
        {
            Success = false, DeviceId = deviceId ?? string.Empty, Action = action,
            Message = "Confirmation required: this destructive action must be invoked with confirmed: true."
        };
    }

    public async Task<DeviceActionResult> RemoteLockDeviceAsync(string deviceId, string? pin = null, bool confirmed = false)
    {
        var guard = RequireConfirmation(confirmed, "remoteLock", deviceId);
        if (guard != null) return guard;

        if (!await SetAuthorizationAsync())
        {
            return new DeviceActionResult { Success = false, DeviceId = deviceId, Action = "remoteLock", Message = "Not authenticated" };
        }

        try
        {
            var url = $"deviceManagement/managedDevices/{deviceId}/remoteLock";
            HttpResponseMessage response;

            if (!string.IsNullOrEmpty(pin))
            {
                var body = new { pin };
                var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                response = await _client.PostAsync(url, content);
            }
            else
            {
                response = await _client.PostAsync(url, null);
            }

            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                Log.Information("Remote lock triggered for device {DeviceId}", deviceId);
                return new DeviceActionResult { Success = true, DeviceId = deviceId, Action = "remoteLock" };
            }

            var error = await response.Content.ReadAsStringAsync();
            Log.Warning("Failed to lock device {DeviceId}: {Status} - {Error}", deviceId, response.StatusCode, error);
            return new DeviceActionResult { Success = false, DeviceId = deviceId, Action = "remoteLock", Message = error };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to lock device {DeviceId}", deviceId);
            return new DeviceActionResult { Success = false, DeviceId = deviceId, Action = "remoteLock", Message = ex.Message };
        }
    }

    /// <summary>
    /// Remote lock multiple devices
    /// </summary>
    public async Task<List<DeviceActionResult>> RemoteLockDevicesAsync(IEnumerable<string> deviceIds, string? pin = null, bool confirmed = false)
    {
        var tasks = deviceIds.Select(id => RemoteLockDeviceAsync(id, pin, confirmed));
        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    /// <summary>
    /// Wipe a device (factory reset)
    /// </summary>
    public async Task<DeviceActionResult> WipeDeviceAsync(string deviceId, bool keepEnrollmentData = false, bool keepUserData = false, bool confirmed = false)
    {
        var guard = RequireConfirmation(confirmed, "wipe", deviceId);
        if (guard != null) return guard;

        if (!await SetAuthorizationAsync())
        {
            return new DeviceActionResult { Success = false, DeviceId = deviceId, Action = "wipe", Message = "Not authenticated" };
        }

        try
        {
            var url = $"deviceManagement/managedDevices/{deviceId}/wipe";
            var body = new { keepEnrollmentData, keepUserData };
            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            var response = await _client.PostAsync(url, content);

            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                Log.Information("Wipe triggered for device {DeviceId}", deviceId);
                return new DeviceActionResult { Success = true, DeviceId = deviceId, Action = "wipe" };
            }

            var error = await response.Content.ReadAsStringAsync();
            Log.Warning("Failed to wipe device {DeviceId}: {Status} - {Error}", deviceId, response.StatusCode, error);
            return new DeviceActionResult { Success = false, DeviceId = deviceId, Action = "wipe", Message = error };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to wipe device {DeviceId}", deviceId);
            return new DeviceActionResult { Success = false, DeviceId = deviceId, Action = "wipe", Message = ex.Message };
        }
    }

    /// <summary>
    /// Retire a device (remove company data)
    /// </summary>
    public async Task<DeviceActionResult> RetireDeviceAsync(string deviceId, bool confirmed = false)
    {
        var guard = RequireConfirmation(confirmed, "retire", deviceId);
        if (guard != null) return guard;

        if (!await SetAuthorizationAsync())
        {
            return new DeviceActionResult { Success = false, DeviceId = deviceId, Action = "retire", Message = "Not authenticated" };
        }

        try
        {
            var url = $"deviceManagement/managedDevices/{deviceId}/retire";
            var response = await _client.PostAsync(url, null);

            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                Log.Information("Retire triggered for device {DeviceId}", deviceId);
                return new DeviceActionResult { Success = true, DeviceId = deviceId, Action = "retire" };
            }

            var error = await response.Content.ReadAsStringAsync();
            Log.Warning("Failed to retire device {DeviceId}: {Status} - {Error}", deviceId, response.StatusCode, error);
            return new DeviceActionResult { Success = false, DeviceId = deviceId, Action = "retire", Message = error };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to retire device {DeviceId}", deviceId);
            return new DeviceActionResult { Success = false, DeviceId = deviceId, Action = "retire", Message = ex.Message };
        }
    }

    /// <summary>Factory-reset multiple devices.</summary>
    public async Task<List<DeviceActionResult>> WipeDevicesAsync(IEnumerable<string> deviceIds, bool keepEnrollmentData = false, bool keepUserData = false, bool confirmed = false)
    {
        var tasks = deviceIds.Select(id => WipeDeviceAsync(id, keepEnrollmentData, keepUserData, confirmed));
        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    /// <summary>Retire multiple devices (remove company data, unenroll).</summary>
    public async Task<List<DeviceActionResult>> RetireDevicesAsync(IEnumerable<string> deviceIds, bool confirmed = false)
    {
        var tasks = deviceIds.Select(id => RetireDeviceAsync(id, confirmed));
        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    /// <summary>
    /// Get mobile apps from Intune
    /// </summary>
    public async Task<List<MobileApp>> GetMobileAppsAsync(string? filter = null, int limit = 100)
    {
        if (!await SetAuthorizationAsync())
        {
            return new List<MobileApp>();
        }

        var allApps = new List<MobileApp>();
        var url = "deviceAppManagement/mobileApps";

        var queryParams = new List<string> { $"$top={Math.Min(limit, _config.PageSize)}" };
        if (!string.IsNullOrEmpty(filter))
        {
            queryParams.Add($"$filter={Uri.EscapeDataString(filter)}");
        }
        url += "?" + string.Join("&", queryParams);

        try
        {
            while (!string.IsNullOrEmpty(url) && allApps.Count < limit)
            {
                var response = await _client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Log.Warning("Failed to get mobile apps: {Status} - {Error}", response.StatusCode, error);
                    break;
                }

                var result = await response.Content.ReadFromJsonAsync<MobileAppsResponse>(_jsonOptions);
                if (result?.Value != null)
                {
                    allApps.AddRange(result.Value);
                }

                url = result?.NextLink;
                if (url != null && url.StartsWith(_client.BaseAddress!.ToString()))
                {
                    url = url.Substring(_client.BaseAddress.ToString().Length);
                }
            }

            Log.Debug("Retrieved {Count} mobile apps from Intune", allApps.Count);
            return allApps.Take(limit).ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get mobile apps");
            return new List<MobileApp>();
        }
    }

    /// <summary>
    /// Search mobile apps by name
    /// </summary>
    public async Task<List<MobileApp>> SearchMobileAppsAsync(string query, int limit = 50)
    {
        var filter = $"contains(displayName, '{query}')";
        return await GetMobileAppsAsync(filter, limit);
    }

    /// <summary>
    /// Get detected apps on a device
    /// </summary>
    public async Task<List<DetectedApp>> GetDetectedAppsAsync(string deviceId)
    {
        if (!await SetAuthorizationAsync())
        {
            return new List<DetectedApp>();
        }

        try
        {
            var url = $"deviceManagement/managedDevices/{deviceId}/detectedApps";
            var response = await _client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Failed to get detected apps for device {DeviceId}: {Status}", deviceId, response.StatusCode);
                return new List<DetectedApp>();
            }

            var result = await response.Content.ReadFromJsonAsync<DetectedAppsResponse>(_jsonOptions);
            return result?.Value ?? new List<DetectedApp>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get detected apps for device {DeviceId}", deviceId);
            return new List<DetectedApp>();
        }
    }

    #endregion

    #region Entra Users

    /// <summary>
    /// Get a user by UPN or ID
    /// </summary>
    public async Task<EntraUser?> GetUserAsync(string userPrincipalNameOrId, bool includeGroups = false)
    {
        // Check cache
        if (_userCache.TryGetValue(userPrincipalNameOrId.ToLowerInvariant(), out var cached) && DateTime.UtcNow < cached.expiry)
        {
            return cached.user;
        }

        if (!await SetAuthorizationAsync())
        {
            return null;
        }

        try
        {
            var url = $"users/{Uri.EscapeDataString(userPrincipalNameOrId)}";
            var response = await _client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Failed to get user {User}: {Status} - {Error}", userPrincipalNameOrId, response.StatusCode, await ReadErrorBodyAsync(response));
                return null;
            }

            var user = await response.Content.ReadFromJsonAsync<EntraUser>(_jsonOptions);
            if (user != null)
            {
                // Cache the user
                _userCache[userPrincipalNameOrId.ToLowerInvariant()] = (user, DateTime.UtcNow.Add(_cacheDuration));

                if (includeGroups)
                {
                    user.MemberOf = await GetUserGroupsAsync(userPrincipalNameOrId);
                }
            }

            return user;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get user {User}", userPrincipalNameOrId);
            return null;
        }
    }

    /// <summary>
    /// Search for users by display name, UPN, or mail using fuzzy filter
    /// </summary>
    public async Task<List<EntraUser>> SearchUsersAsync(string query, int limit = 25)
    {
        if (!await SetAuthorizationAsync())
        {
            return new List<EntraUser>();
        }

        try
        {
            var escaped = query.Replace("'", "''");
            var filter = $"startswith(displayName,'{escaped}') or startswith(userPrincipalName,'{escaped}') or startswith(mail,'{escaped}')";
            var select = "id,displayName,userPrincipalName,mail,jobTitle,department,officeLocation";
            var url = $"users?$filter={Uri.EscapeDataString(filter)}&$select={select}&$top={limit}";

            var response = await _client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Failed to search users with query '{Query}': {Status} - {Error}", query, response.StatusCode, await ReadErrorBodyAsync(response));
                return new List<EntraUser>();
            }

            var result = await response.Content.ReadFromJsonAsync<EntraUserListResponse>(_jsonOptions);
            return result?.Value ?? new List<EntraUser>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to search users with query '{Query}'", query);
            return new List<EntraUser>();
        }
    }

    /// <summary>
    /// Get groups a user is a member of
    /// </summary>
    public async Task<List<EntraGroup>> GetUserGroupsAsync(string userPrincipalNameOrId)
    {
        if (!await SetAuthorizationAsync())
        {
            return new List<EntraGroup>();
        }

        var groups = new List<EntraGroup>();

        try
        {
            var url = $"users/{Uri.EscapeDataString(userPrincipalNameOrId)}/memberOf";

            while (!string.IsNullOrEmpty(url))
            {
                var response = await _client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    Log.Warning("Failed to get groups for user {User}: {Status} - {Error}", userPrincipalNameOrId, response.StatusCode, await ReadErrorBodyAsync(response));
                    break;
                }

                var result = await response.Content.ReadFromJsonAsync<UserMemberOfResponse>(_jsonOptions);
                if (result?.Value != null)
                {
                    foreach (var obj in result.Value.Where(o => o.IsGroup))
                    {
                        groups.Add(new EntraGroup
                        {
                            Id = obj.Id,
                            DisplayName = obj.DisplayName ?? string.Empty,
                            Description = obj.Description
                        });
                    }
                }

                url = result?.NextLink;
                if (url != null && url.StartsWith(_client.BaseAddress!.ToString()))
                {
                    url = url.Substring(_client.BaseAddress.ToString().Length);
                }
            }

            return groups;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get groups for user {User}", userPrincipalNameOrId);
            return new List<EntraGroup>();
        }
    }

    /// <summary>
    /// Check if a user is a member of a specific group
    /// </summary>
    public async Task<bool> CheckGroupMembershipAsync(string userPrincipalNameOrId, string groupNameOrId)
    {
        if (!await SetAuthorizationAsync())
        {
            return false;
        }

        try
        {
            // First, try to get the group ID if a name was provided
            var groupId = groupNameOrId;
            if (!Guid.TryParse(groupNameOrId, out _))
            {
                var group = await GetGroupByNameAsync(groupNameOrId);
                if (group == null)
                {
                    Log.Warning("Group not found: {Group}", groupNameOrId);
                    return false;
                }
                groupId = group.Id;
            }

            // Get user ID
            var user = await GetUserAsync(userPrincipalNameOrId);
            if (user == null)
            {
                return false;
            }

            // Check membership using checkMemberGroups
            var url = $"users/{user.Id}/checkMemberGroups";
            var body = new { groupIds = new[] { groupId } };
            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            var response = await _client.PostAsync(url, content);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Failed to check group membership: {Status}", response.StatusCode);
                return false;
            }

            var result = await response.Content.ReadFromJsonAsync<CheckMemberGroupsResponse>(_jsonOptions);
            return result?.Value.Contains(groupId) == true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to check group membership for {User} in {Group}", userPrincipalNameOrId, groupNameOrId);
            return false;
        }
    }

    #endregion

    #region Entra Groups

    /// <summary>
    /// Get a group by name
    /// </summary>
    public async Task<EntraGroup?> GetGroupByNameAsync(string displayName)
    {
        // Check cache
        if (_groupCache.TryGetValue(displayName.ToLowerInvariant(), out var cached) && DateTime.UtcNow < cached.expiry)
        {
            return cached.group;
        }

        if (!await SetAuthorizationAsync())
        {
            return null;
        }

        try
        {
            var filter = $"displayName eq '{displayName}'";
            var url = $"groups?$filter={Uri.EscapeDataString(filter)}";

            var response = await _client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Failed to get group {Group}: {Status} - {Error}", displayName, response.StatusCode, await ReadErrorBodyAsync(response));
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<EntraGroupListResponse>(_jsonOptions);
            var group = result?.Value.FirstOrDefault();

            if (group != null)
            {
                _groupCache[displayName.ToLowerInvariant()] = (group, DateTime.UtcNow.Add(_cacheDuration));
            }

            return group;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get group {Group}", displayName);
            return null;
        }
    }

    /// <summary>
    /// Get a group by ID
    /// </summary>
    public async Task<EntraGroup?> GetGroupByIdAsync(string groupId)
    {
        if (!await SetAuthorizationAsync())
        {
            return null;
        }

        try
        {
            var url = $"groups/{groupId}";
            var response = await _client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Failed to get group {GroupId}: {Status} - {Error}", groupId, response.StatusCode, await ReadErrorBodyAsync(response));
                return null;
            }

            return await response.Content.ReadFromJsonAsync<EntraGroup>(_jsonOptions);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get group {GroupId}", groupId);
            return null;
        }
    }

    /// <summary>
    /// Get members of a group
    /// </summary>
    public async Task<List<EntraUser>> GetGroupMembersAsync(string groupNameOrId, int limit = 100)
    {
        if (!await SetAuthorizationAsync())
        {
            return new List<EntraUser>();
        }

        // Get group ID if name was provided
        var groupId = groupNameOrId;
        if (!Guid.TryParse(groupNameOrId, out _))
        {
            var group = await GetGroupByNameAsync(groupNameOrId);
            if (group == null)
            {
                return new List<EntraUser>();
            }
            groupId = group.Id;
        }

        var members = new List<EntraUser>();

        try
        {
            var url = $"groups/{groupId}/members?$top={Math.Min(limit, _config.PageSize)}";

            while (!string.IsNullOrEmpty(url) && members.Count < limit)
            {
                var response = await _client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    Log.Warning("Failed to get members for group {GroupId}: {Status}", groupId, response.StatusCode);
                    break;
                }

                var result = await response.Content.ReadFromJsonAsync<GroupMembersResponse>(_jsonOptions);
                if (result?.Value != null)
                {
                    members.AddRange(result.Value);
                }

                url = result?.NextLink;
                if (url != null && url.StartsWith(_client.BaseAddress!.ToString()))
                {
                    url = url.Substring(_client.BaseAddress.ToString().Length);
                }
            }

            return members.Take(limit).ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get members for group {GroupId}", groupId);
            return new List<EntraUser>();
        }
    }

    /// <summary>Resolve a group name or id to its object id (passes ids through).</summary>
    private async Task<string?> ResolveGroupIdAsync(string groupNameOrId)
    {
        if (Guid.TryParse(groupNameOrId, out _)) return groupNameOrId;
        return (await GetGroupByNameAsync(groupNameOrId))?.Id;
    }

    /// <summary>Resolve a user UPN or id to its object id (passes ids through).</summary>
    private async Task<string?> ResolveUserIdAsync(string userPrincipalNameOrId)
    {
        if (Guid.TryParse(userPrincipalNameOrId, out _)) return userPrincipalNameOrId;
        return (await GetUserAsync(userPrincipalNameOrId))?.Id;
    }

    /// <summary>Add a directory object (user or device) to a group.</summary>
    public async Task<bool> AddGroupMemberAsync(string groupNameOrId, string objectId)
    {
        if (!await SetAuthorizationAsync()) return false;
        var groupId = await ResolveGroupIdAsync(groupNameOrId);
        if (string.IsNullOrEmpty(groupId)) { Log.Warning("Group not found: {Group}", groupNameOrId); return false; }
        try
        {
            // Serialize so the object id is JSON-escaped (the literal "@odata.id" key is required by Graph).
            var json = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["@odata.id"] = $"{_client.BaseAddress}directoryObjects/{objectId}"
            });
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _client.PostAsync($"groups/{groupId}/members/$ref", content);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Log.Warning("Add member failed: {Status} - {Error}", response.StatusCode, error);
                return false;
            }
            return true;
        }
        catch (Exception ex) { Log.Error(ex, "Failed to add member to group {Group}", groupNameOrId); return false; }
    }

    /// <summary>Remove a directory object from a group.</summary>
    public async Task<bool> RemoveGroupMemberAsync(string groupNameOrId, string objectId)
    {
        if (!await SetAuthorizationAsync()) return false;
        var groupId = await ResolveGroupIdAsync(groupNameOrId);
        if (string.IsNullOrEmpty(groupId)) { Log.Warning("Group not found: {Group}", groupNameOrId); return false; }
        try
        {
            var response = await _client.DeleteAsync($"groups/{groupId}/members/{objectId}/$ref");
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Log.Warning("Remove member failed: {Status} - {Error}", response.StatusCode, error);
                return false;
            }
            return true;
        }
        catch (Exception ex) { Log.Error(ex, "Failed to remove member from group {Group}", groupNameOrId); return false; }
    }

    /// <summary>Enable or disable a user account (PATCH accountEnabled).</summary>
    public async Task<bool> SetUserAccountEnabledAsync(string userPrincipalNameOrId, bool enabled)
    {
        if (!await SetAuthorizationAsync()) return false;
        var userId = await ResolveUserIdAsync(userPrincipalNameOrId);
        if (string.IsNullOrEmpty(userId)) { Log.Warning("User not found: {User}", userPrincipalNameOrId); return false; }
        try
        {
            var json = $"{{\"accountEnabled\":{(enabled ? "true" : "false")}}}";
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _client.PatchAsync($"users/{userId}", content);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Log.Warning("Set accountEnabled failed: {Status} - {Error}", response.StatusCode, error);
                return false;
            }
            return true;
        }
        catch (Exception ex) { Log.Error(ex, "Failed to set accountEnabled for {User}", userPrincipalNameOrId); return false; }
    }

    /// <summary>
    /// Search groups by name pattern
    /// </summary>
    public async Task<List<EntraGroup>> SearchGroupsAsync(string query, int limit = 50)
    {
        if (!await SetAuthorizationAsync())
        {
            return new List<EntraGroup>();
        }

        try
        {
            var filter = $"startswith(displayName, '{query}')";
            var url = $"groups?$filter={Uri.EscapeDataString(filter)}&$top={limit}";

            var response = await _client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Failed to search groups: {Status}", response.StatusCode);
                return new List<EntraGroup>();
            }

            var result = await response.Content.ReadFromJsonAsync<EntraGroupListResponse>(_jsonOptions);
            return result?.Value ?? new List<EntraGroup>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to search groups");
            return new List<EntraGroup>();
        }
    }

    /// <summary>
    /// Get Intune managed devices that belong to an Entra group.
    /// Resolves group members (devices) and cross-references with managed devices.
    /// </summary>
    public async Task<List<IntuneDevice>> GetGroupDevicesAsync(string groupNameOrId, int limit = 500)
    {
        if (!await SetAuthorizationAsync())
        {
            return new List<IntuneDevice>();
        }

        // Resolve group ID
        var groupId = groupNameOrId;
        if (!Guid.TryParse(groupNameOrId, out _))
        {
            var group = await GetGroupByNameAsync(groupNameOrId);
            if (group == null)
            {
                Log.Warning("Group not found: {Group}", groupNameOrId);
                return new List<IntuneDevice>();
            }
            groupId = group.Id;
        }

        var devices = new List<IntuneDevice>();

        try
        {
            // Get device members from the group (filters for device objects)
            var url = $"groups/{groupId}/members?$top={Math.Min(limit, _config.PageSize)}";

            while (!string.IsNullOrEmpty(url) && devices.Count < limit)
            {
                var response = await _client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Log.Warning("Failed to get group device members: {Status} - {Error}", response.StatusCode, error);
                    break;
                }

                using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
                var root = doc.RootElement;

                if (root.TryGetProperty("value", out var values))
                {
                    foreach (var member in values.EnumerateArray())
                    {
                        // Check if this is a device object
                        var odataType = member.TryGetProperty("@odata.type", out var typeProp)
                            ? typeProp.GetString() : null;

                        if (odataType == "#microsoft.graph.device")
                        {
                            var deviceId = member.TryGetProperty("deviceId", out var devIdProp)
                                ? devIdProp.GetString() : null;

                            if (!string.IsNullOrEmpty(deviceId))
                            {
                                // Cross-reference with managed devices by azureADDeviceId
                                var filter = $"azureADDeviceId eq '{deviceId}'";
                                var managed = await GetManagedDevicesAsync(filter, 1);
                                if (managed.Count > 0)
                                {
                                    devices.Add(managed[0]);
                                }
                            }
                        }
                    }
                }

                // Handle pagination
                url = root.TryGetProperty("@odata.nextLink", out var nextLink) 
                    ? nextLink.GetString() : null;
                if (url != null && url.StartsWith(_client.BaseAddress!.ToString()))
                {
                    url = url.Substring(_client.BaseAddress.ToString().Length);
                }
            }

            Log.Information("Resolved {Count} managed devices from group {Group}", devices.Count, groupNameOrId);
            return devices.Take(limit).ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get devices for group {Group}", groupNameOrId);
            return new List<IntuneDevice>();
        }
    }

    #endregion

    #region Intune Proactive Remediations

    /// <summary>
    /// Deploy a proactive remediation script to targeted devices via Intune.
    /// Creates a deviceHealthScript and assigns it to a group.
    /// </summary>
    public async Task<DeviceActionResult> DeployRemediationAsync(
        string displayName,
        string detectionScript,
        string remediationScript,
        string groupId,
        string? description = null,
        bool confirmed = false)
    {
        // Most destructive path: runs PowerShell as SYSTEM across every device in the
        // target group. Requires explicit confirmation, like wipe/retire.
        var guard = RequireConfirmation(confirmed, "deployRemediation", groupId);
        if (guard != null) return guard;

        if (!await SetAuthorizationAsync())
        {
            return new DeviceActionResult { Success = false, Action = "deployRemediation", Message = "Not authenticated" };
        }

        try
        {
            // Base64-encode the scripts (Graph API requirement)
            var detectionBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(detectionScript));
            var remediationBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(remediationScript));

            // Create the deviceHealthScript
            var scriptPayload = new
            {
                displayName,
                description = description ?? $"Deployed by FleetMate at {DateTime.UtcNow:u}",
                publisher = "FleetMate",
                runAsAccount = "system",
                enforceSignatureCheck = false,
                runAs32Bit = false,
                detectionScriptContent = detectionBase64,
                remediationScriptContent = remediationBase64
            };

            var content = new StringContent(
                JsonSerializer.Serialize(scriptPayload),
                Encoding.UTF8,
                "application/json");

            var createResponse = await _client.PostAsync("deviceManagement/deviceHealthScripts", content);

            if (!createResponse.IsSuccessStatusCode)
            {
                var error = await createResponse.Content.ReadAsStringAsync();
                Log.Error("Failed to create remediation script: {Status} - {Error}", createResponse.StatusCode, error);
                return new DeviceActionResult
                {
                    Success = false, Action = "deployRemediation",
                    Message = $"Failed to create script: {createResponse.StatusCode}"
                };
            }

            using var createDoc = await JsonDocument.ParseAsync(await createResponse.Content.ReadAsStreamAsync());
            var scriptId = createDoc.RootElement.GetProperty("id").GetString();

            if (string.IsNullOrEmpty(scriptId))
            {
                return new DeviceActionResult { Success = false, Action = "deployRemediation", Message = "Script created but no ID returned" };
            }

            // Assign the script to the target group
            var assignPayload = new
            {
                deviceHealthScriptAssignments = new[]
                {
                    new
                    {
                        target = new
                        {
                            @OdataType = "#microsoft.graph.groupAssignmentTarget",
                            groupId
                        },
                        runRemediationScript = true,
                        runSchedule = new
                        {
                            @OdataType = "#microsoft.graph.deviceHealthScriptRunOnceSchedule",
                            date = DateTime.UtcNow.ToString("yyyy-MM-dd"),
                            time = DateTime.UtcNow.ToString("HH:mm:ss"),
                            useUtc = true
                        }
                    }
                }
            };

            var assignContent = new StringContent(
                JsonSerializer.Serialize(assignPayload),
                Encoding.UTF8,
                "application/json");

            var assignUrl = $"deviceManagement/deviceHealthScripts/{scriptId}/assign";
            var assignResponse = await _client.PostAsync(assignUrl, assignContent);

            if (!assignResponse.IsSuccessStatusCode)
            {
                var error = await assignResponse.Content.ReadAsStringAsync();
                Log.Warning("Remediation created but assignment failed: {Error}", error);
                return new DeviceActionResult
                {
                    Success = false, Action = "deployRemediation", DeviceId = scriptId,
                    Message = $"Script created (ID: {scriptId}) but group assignment failed"
                };
            }

            Log.Information("Deployed remediation '{Name}' (ID: {Id}) to group {Group}", displayName, scriptId, groupId);
            return new DeviceActionResult
            {
                Success = true, Action = "deployRemediation", DeviceId = scriptId,
                Message = $"Remediation deployed (ID: {scriptId})"
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to deploy remediation '{Name}'", displayName);
            return new DeviceActionResult { Success = false, Action = "deployRemediation", Message = ex.Message };
        }
    }

    /// <summary>
    /// Deploy the Cimian push trigger remediation to a group.
    /// Creates a proactive remediation that writes .cimian.headless on target devices.
    /// </summary>
    public async Task<DeviceActionResult> DeployCimianPushRemediationAsync(string groupNameOrId, bool confirmed = false)
    {
        // Resolve group ID
        var groupId = groupNameOrId;
        if (!Guid.TryParse(groupNameOrId, out _))
        {
            var group = await GetGroupByNameAsync(groupNameOrId);
            if (group == null)
            {
                return new DeviceActionResult
                {
                    Success = false, Action = "cimianPush",
                    Message = $"Group not found: {groupNameOrId}"
                };
            }
            groupId = group.Id;
        }

        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        var displayName = $"Cimian Push Trigger - {timestamp}";

        var detectionScript = @"# Cimian Push - Detection Script
# Exit 0 = remediation needed (trigger file does not exist)
# Exit 1 = no action needed (trigger file already exists or MSU is running)

$triggerFile = 'C:\ProgramData\ManagedInstalls\.cimian.headless'
$msuProcess = Get-Process -Name 'managedsoftwareupdate' -ErrorAction SilentlyContinue

if ($msuProcess) {
    Write-Output 'managedsoftwareupdate is already running'
    exit 1
}

if (Test-Path $triggerFile) {
    $age = (Get-Date) - (Get-Item $triggerFile).LastWriteTime
    if ($age.TotalMinutes -lt 5) {
        Write-Output 'Trigger file exists and is recent'
        exit 1
    }
}

Write-Output 'Cimian push trigger needed'
exit 0
";

        var remediationScript = @"# Cimian Push - Remediation Script
# Creates .cimian.headless trigger file for CimianWatcher to pick up

$managedInstallsDir = 'C:\ProgramData\ManagedInstalls'
$triggerFile = Join-Path $managedInstallsDir '.cimian.headless'

# Ensure directory exists
if (-not (Test-Path $managedInstallsDir)) {
    New-Item -ItemType Directory -Path $managedInstallsDir -Force | Out-Null
}

# Write trigger file
$content = @""
Bootstrap triggered at: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
Mode: Headless
Triggered by: FleetMate Intune Push
""@

Set-Content -Path $triggerFile -Value $content -Force
Write-Output ""Cimian push trigger created at $triggerFile""

# Verify CimianWatcher service is running
$svc = Get-Service -Name 'CimianWatcher' -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne 'Running') {
    Start-Service -Name 'CimianWatcher' -ErrorAction SilentlyContinue
    Write-Output 'CimianWatcher service was stopped, started it'
}
";

        return await DeployRemediationAsync(
            displayName,
            detectionScript,
            remediationScript,
            groupId,
            "FleetMate-initiated Cimian push trigger. Creates .cimian.headless to force an immediate managed software update run.",
            confirmed);
    }

    #endregion

    public void Dispose()
    {
        _client.Dispose();
    }
}
