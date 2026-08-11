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

    // Token caching lives in EntraTokenSource, which is shared across services
    // so one broker call serves every consumer of a given audience.

    /// <summary>
    /// Graph's hard ceiling for <c>$top</c> on directory collections.
    ///
    /// Asking for more is not merely capped — the request fails outright with
    /// <c>Request_UnsupportedQuery</c> ("Invalid page size specified: '1000'.
    /// Must be between 1 and 999 inclusive"), so a caller wanting 1000 groups
    /// gets zero of them and the UI shows an empty state.
    /// </summary>
    private const int MaxGraphPageSize = 999;

    /// <summary>
    /// Page size for a request that wants <paramref name="limit"/> results in
    /// total. This is a <em>page</em> size, not a cap on the result: the paged
    /// calls here follow <c>@odata.nextLink</c> until they reach the limit, so
    /// the 999 ceiling bounds a page rather than the answer.
    /// </summary>
    private int PageSizeFor(int limit) =>
        Math.Min(Math.Min(limit, _config.PageSize), MaxGraphPageSize);

    /// <summary>
    /// Fields to request for a user.
    ///
    /// Graph's default projection for /users omits most of this — including
    /// <c>accountEnabled</c>, which then decodes to null and renders every
    /// single user as Disabled. An explicit $select is not an optimisation here;
    /// without it the data is silently wrong.
    /// </summary>
    private const string UserSelect =
        "id,displayName,userPrincipalName,mail,givenName,surname,jobTitle,department," +
        "officeLocation,mobilePhone,businessPhones,accountEnabled,createdDateTime," +
        "employeeId,employeeType,companyName,usageLocation," +
        "onPremisesSamAccountName,onPremisesDistinguishedName,onPremisesSyncEnabled";

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
    /// Get a Graph access token for the operator, brokered from their Windows
    /// sign-in.
    ///
    /// Graph is secretless. There are exactly two ways FleetMate reaches it:
    /// this delegated token, and — for privileged Intune/Entra writes — an
    /// <see cref="ElevationSession"/> running as a managed identity, whose token
    /// never leaves Azure. Client secrets and per-scope service principals were
    /// removed deliberately; a secret sitting in the registry of every admin
    /// workstation is the thing this design exists to avoid.
    /// </summary>
    private async Task<string?> GetAccessTokenAsync()
    {
        var source = EntraTokenSource.Shared
            ?? EntraTokenSource.Configure(_config.TenantId, _config.ClientId);

        try
        {
            return await source.GetTokenAsync(GraphResourceId);
        }
        catch (EntraTokenException ex)
        {
            Log.Error("Failed to get Microsoft Graph access token: {Message}", ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get Microsoft Graph access token");
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

        var queryParams = new List<string> { $"$top={PageSizeFor(limit)}" };
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

        var queryParams = new List<string> { $"$top={PageSizeFor(limit)}" };
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
            var url = $"users/{Uri.EscapeDataString(userPrincipalNameOrId)}?$select={UserSelect}";
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
            // Same $select as GetUserAsync — the list renders the enabled badge
            // too, and a narrower projection here put every result in the list
            // at odds with its own detail pane.
            var url = $"users?$filter={Uri.EscapeDataString(filter)}&$select={UserSelect}&$top={PageSizeFor(limit)}";

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
    /// <summary>
    /// The user's manager, or null when they have none.
    ///
    /// Graph answers 404 rather than an empty body for a user with no manager,
    /// which is a normal state and not an error worth logging loudly.
    /// </summary>
    public async Task<EntraUser?> GetUserManagerAsync(string userPrincipalNameOrId)
    {
        if (!await SetAuthorizationAsync()) return null;

        try
        {
            var url = $"users/{Uri.EscapeDataString(userPrincipalNameOrId)}/manager?$select={UserSelect}";
            var response = await _client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
                {
                    Log.Debug("[graph] Manager lookup for {User} returned {Status}",
                        userPrincipalNameOrId, response.StatusCode);
                }
                return null;
            }

            return await response.Content.ReadFromJsonAsync<EntraUser>(_jsonOptions);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[graph] Failed to get the manager for {User}", userPrincipalNameOrId);
            return null;
        }
    }

    /// <summary>
    /// Intune devices whose primary user is this person.
    ///
    /// Filtered server-side on userPrincipalName rather than fetching the estate
    /// and matching locally — the managed device collection is the largest thing
    /// in the tenant and an inspector pane must not pull all of it.
    /// </summary>
    public async Task<List<IntuneDevice>> GetUserDevicesAsync(string userPrincipalName, int limit = 100)
    {
        if (string.IsNullOrWhiteSpace(userPrincipalName)) return new List<IntuneDevice>();

        var escaped = userPrincipalName.Replace("'", "''");
        return await GetManagedDevicesAsync($"userPrincipalName eq '{escaped}'", limit);
    }

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
            var url = $"groups/{groupId}/members?$top={PageSizeFor(limit)}";

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

        var groups = new List<EntraGroup>();

        try
        {
            var escaped = query.Replace("'", "''");
            var filter = $"startswith(displayName, '{escaped}')";
            var url = $"groups?$filter={Uri.EscapeDataString(filter)}&$top={PageSizeFor(limit)}";

            // Follow @odata.nextLink until the caller's limit is met. A single
            // page silently truncated the result at the page ceiling, so a
            // tenant with more than 999 Devices-* groups lost the tail — and the
            // client-side filter could not match what was never fetched.
            while (!string.IsNullOrEmpty(url) && groups.Count < limit)
            {
                var response = await _client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    Log.Warning("Failed to search groups: {Status} - {Error}",
                        response.StatusCode, await ReadErrorBodyAsync(response));
                    break;
                }

                var result = await response.Content.ReadFromJsonAsync<EntraGroupListResponse>(_jsonOptions);
                if (result?.Value == null || result.Value.Count == 0) break;

                groups.AddRange(result.Value);
                url = result.NextLink;
            }

            // nextLink pages are whole, so the last one can overshoot the limit.
            return groups.Count > limit ? groups.Take(limit).ToList() : groups;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to search groups");
            return groups;
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
            var url = $"groups/{groupId}/members?$top={PageSizeFor(limit)}";

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

    #region AutoPilot identity and directory records

    /// <summary>
    /// Look up a machine's AutoPilot device identity by serial.
    ///
    /// Uses contains() rather than eq: AutoPilot serials are recorded exactly as
    /// the OEM wrote them into firmware, and Lenovo units in particular arrive
    /// with trailing whitespace that makes an equality filter silently miss.
    /// </summary>
    public async Task<AutopilotDevice?> GetAutopilotDeviceBySerialAsync(string serialNumber)
    {
        var devices = await GetAutopilotDevicesAsync($"contains(serialNumber,'{serialNumber}')", 5);
        return devices.FirstOrDefault(d =>
                   string.Equals(d.SerialNumber?.Trim(), serialNumber.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? devices.FirstOrDefault();
    }

    /// <summary>List AutoPilot device identities, following paging to <paramref name="limit"/>.</summary>
    public async Task<List<AutopilotDevice>> GetAutopilotDevicesAsync(string? filter = null, int limit = 100)
    {
        if (!await SetAuthorizationAsync())
        {
            Log.Warning("Failed to authenticate to Microsoft Graph");
            return new List<AutopilotDevice>();
        }

        var all = new List<AutopilotDevice>();
        var url = "deviceManagement/windowsAutopilotDeviceIdentities";

        var queryParams = new List<string> { $"$top={PageSizeFor(limit)}" };
        if (!string.IsNullOrEmpty(filter))
            queryParams.Add($"$filter={Uri.EscapeDataString(filter)}");
        url += "?" + string.Join("&", queryParams);

        try
        {
            while (!string.IsNullOrEmpty(url) && all.Count < limit)
            {
                var response = await _client.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    Log.Warning("Failed to get AutoPilot identities: {Status} - {Error}",
                        response.StatusCode, await ReadErrorBodyAsync(response));
                    break;
                }

                var result = await response.Content.ReadFromJsonAsync<AutopilotDeviceListResponse>(_jsonOptions);
                if (result?.Value != null) all.AddRange(result.Value);

                url = result?.NextLink;
                if (url != null && url.StartsWith(_client.BaseAddress!.ToString()))
                    url = url.Substring(_client.BaseAddress.ToString().Length);
            }

            return all.Take(limit).ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get AutoPilot identities");
            return new List<AutopilotDevice>();
        }
    }

    /// <summary>
    /// Delete an Intune managedDevice record.
    ///
    /// This is not a wipe and sends nothing to the machine — it removes the
    /// server-side enrollment record so the device can enroll clean. Deleting
    /// the record of a machine that is still running leaves that machine
    /// unmanaged until it re-enrolls.
    /// </summary>
    public async Task<DeviceActionResult> DeleteManagedDeviceAsync(string deviceId, bool confirmed = false)
    {
        var guard = RequireConfirmation(confirmed, "deleteManagedDevice", deviceId);
        if (guard != null) return guard;

        if (!await SetAuthorizationAsync())
            return new DeviceActionResult { Success = false, DeviceId = deviceId, Action = "deleteManagedDevice", Message = "Not authenticated" };

        try
        {
            var response = await _client.DeleteAsync($"deviceManagement/managedDevices/{deviceId}");

            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                Log.Information("Deleted Intune managedDevice {DeviceId}", deviceId);
                return new DeviceActionResult { Success = true, DeviceId = deviceId, Action = "deleteManagedDevice" };
            }

            var error = await ReadErrorBodyAsync(response);
            Log.Warning("Failed to delete managedDevice {DeviceId}: {Status} - {Error}", deviceId, response.StatusCode, error);
            return new DeviceActionResult { Success = false, DeviceId = deviceId, Action = "deleteManagedDevice", Message = error };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to delete managedDevice {DeviceId}", deviceId);
            return new DeviceActionResult { Success = false, DeviceId = deviceId, Action = "deleteManagedDevice", Message = ex.Message };
        }
    }

    /// <summary>List Entra device objects matching an OData filter.</summary>
    public async Task<List<EntraDevice>> GetEntraDevicesAsync(string? filter = null, int limit = 100)
    {
        if (!await SetAuthorizationAsync())
            return new List<EntraDevice>();

        var all = new List<EntraDevice>();
        var url = "devices";

        var queryParams = new List<string> { $"$top={PageSizeFor(limit)}" };
        if (!string.IsNullOrEmpty(filter))
            queryParams.Add($"$filter={Uri.EscapeDataString(filter)}");
        url += "?" + string.Join("&", queryParams);

        try
        {
            while (!string.IsNullOrEmpty(url) && all.Count < limit)
            {
                var response = await _client.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    Log.Warning("Failed to get Entra devices: {Status} - {Error}",
                        response.StatusCode, await ReadErrorBodyAsync(response));
                    break;
                }

                var result = await response.Content.ReadFromJsonAsync<EntraDeviceListResponse>(_jsonOptions);
                if (result?.Value != null) all.AddRange(result.Value);

                url = result?.NextLink;
                if (url != null && url.StartsWith(_client.BaseAddress!.ToString()))
                    url = url.Substring(_client.BaseAddress.ToString().Length);
            }

            return all.Take(limit).ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get Entra devices");
            return new List<EntraDevice>();
        }
    }

    /// <summary>Find the Entra device object for a device id (not the object id).</summary>
    public async Task<EntraDevice?> GetEntraDeviceByDeviceIdAsync(string deviceId)
    {
        var devices = await GetEntraDevicesAsync($"deviceId eq '{deviceId}'", 1);
        return devices.FirstOrDefault();
    }

    /// <summary>Find Entra device objects by display name. Duplicates are the point — all matches are returned.</summary>
    public async Task<List<EntraDevice>> GetEntraDevicesByNameAsync(string displayName)
        => await GetEntraDevicesAsync($"displayName eq '{displayName}'", 50);

    /// <summary>
    /// Delete an Entra device object by its directory object id.
    ///
    /// Takes the object id, not the deviceId — passing the latter yields a
    /// confusing 404, so callers should resolve through
    /// <see cref="GetEntraDeviceByDeviceIdAsync"/> first.
    /// </summary>
    public async Task<DeviceActionResult> DeleteEntraDeviceAsync(string objectId, bool confirmed = false)
    {
        var guard = RequireConfirmation(confirmed, "deleteEntraDevice", objectId);
        if (guard != null) return guard;

        if (!await SetAuthorizationAsync())
            return new DeviceActionResult { Success = false, DeviceId = objectId, Action = "deleteEntraDevice", Message = "Not authenticated" };

        try
        {
            var response = await _client.DeleteAsync($"devices/{objectId}");

            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                Log.Information("Deleted Entra device object {ObjectId}", objectId);
                return new DeviceActionResult { Success = true, DeviceId = objectId, Action = "deleteEntraDevice" };
            }

            var error = await ReadErrorBodyAsync(response);
            Log.Warning("Failed to delete Entra device {ObjectId}: {Status} - {Error}", objectId, response.StatusCode, error);
            return new DeviceActionResult { Success = false, DeviceId = objectId, Action = "deleteEntraDevice", Message = error };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to delete Entra device {ObjectId}", objectId);
            return new DeviceActionResult { Success = false, DeviceId = objectId, Action = "deleteEntraDevice", Message = ex.Message };
        }
    }

    /// <summary>
    /// What the three directory records say about one machine, gathered in one
    /// place so a re-provisioning failure can be read at a glance.
    /// </summary>
    public class DeviceRecordState
    {
        public string Serial { get; set; } = string.Empty;
        public AutopilotDevice? Autopilot { get; set; }
        public IntuneDevice? Intune { get; set; }
        public List<EntraDevice> EntraDevices { get; set; } = new();

        /// <summary>
        /// True when Entra still holds a device object but Intune has no record —
        /// the state that fails the next OOBE at "Registering your device for
        /// mobile management" after "Securing your hardware" finally passes.
        /// </summary>
        public bool IsOrphaned => Intune == null && EntraDevices.Count > 0;

        /// <summary>True when the AutoPilot identity points at a managedDevice that no longer exists.</summary>
        public bool HasDanglingManagedDeviceId =>
            Autopilot != null && !string.IsNullOrEmpty(Autopilot.ManagedDeviceId) && Intune == null;
    }

    /// <summary>
    /// Gather the AutoPilot identity, the Intune record and every Entra device
    /// object for one serial.
    ///
    /// Entra objects are found two ways — by the deviceId the AutoPilot identity
    /// points at, and by the Intune record's display name — because an orphan is
    /// precisely the case where one of those links is already broken.
    /// </summary>
    public async Task<DeviceRecordState> GetDeviceRecordStateAsync(string serialNumber)
    {
        var state = new DeviceRecordState { Serial = serialNumber };

        state.Autopilot = await GetAutopilotDeviceBySerialAsync(serialNumber);
        state.Intune = await GetDeviceBySerialAsync(serialNumber);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(EntraDevice? device)
        {
            if (device != null && seen.Add(device.Id)) state.EntraDevices.Add(device);
        }

        if (!string.IsNullOrEmpty(state.Autopilot?.AzureActiveDirectoryDeviceId))
            Add(await GetEntraDeviceByDeviceIdAsync(state.Autopilot!.AzureActiveDirectoryDeviceId!));

        if (!string.IsNullOrEmpty(state.Intune?.AzureAdDeviceId))
            Add(await GetEntraDeviceByDeviceIdAsync(state.Intune!.AzureAdDeviceId!));

        var name = state.Intune?.DeviceName;
        if (!string.IsNullOrWhiteSpace(name))
            foreach (var d in await GetEntraDevicesByNameAsync(name!)) Add(d);

        return state;
    }

    /// <summary>Outcome of a directory-record cleanup for one machine.</summary>
    public class RecordCleanupResult
    {
        public string Serial { get; set; } = string.Empty;
        public bool Success { get; set; }
        public List<string> Deleted { get; set; } = new();
        public List<string> Skipped { get; set; } = new();
        public List<string> Errors { get; set; } = new();

        /// <summary>The AutoPilot identity, always retained — reported so the caller can prove it survived.</summary>
        public string? RetainedAutopilotId { get; set; }
    }

    /// <summary>
    /// Remove the stale directory records that block a machine from re-enrolling:
    /// the Intune managedDevice and every Entra device object bound to it.
    ///
    /// The AutoPilot identity is deliberately retained — it holds the hardware
    /// hash, and both deleted records are re-created by the next enrollment.
    /// Deleting only the Intune record (the hand runbook) leaves the Entra object
    /// behind, which re-binds by ZTDID at the next OOBE and fails enrollment one
    /// ESP phase later; that is the whole reason this is one operation.
    /// </summary>
    public async Task<RecordCleanupResult> CleanDeviceRecordsAsync(string serialNumber, bool confirmed = false)
    {
        var result = new RecordCleanupResult { Serial = serialNumber };

        if (!confirmed)
        {
            result.Errors.Add("Confirmation required: this destructive action must be invoked with confirmed: true.");
            Log.Warning("Refused unconfirmed destructive action cleanDeviceRecords for {Serial}", serialNumber);
            return result;
        }

        var state = await GetDeviceRecordStateAsync(serialNumber);
        result.RetainedAutopilotId = state.Autopilot?.Id;

        if (state.Intune != null)
        {
            var deleted = await DeleteManagedDeviceAsync(state.Intune.Id, confirmed: true);
            if (deleted.Success) result.Deleted.Add($"Intune managedDevice {state.Intune.Id} ({state.Intune.DeviceName})");
            else result.Errors.Add($"Intune managedDevice {state.Intune.Id}: {deleted.Message}");
        }
        else
        {
            result.Skipped.Add("Intune managedDevice: no record");
        }

        foreach (var entra in state.EntraDevices)
        {
            var deleted = await DeleteEntraDeviceAsync(entra.Id, confirmed: true);
            if (deleted.Success) result.Deleted.Add($"Entra device {entra.Id} ({entra.DisplayName})");
            else result.Errors.Add($"Entra device {entra.Id}: {deleted.Message}");
        }

        if (state.EntraDevices.Count == 0) result.Skipped.Add("Entra device object: no record");

        result.Success = result.Errors.Count == 0;
        return result;
    }

    #endregion

    public void Dispose()
    {
        _client.Dispose();
    }
}
