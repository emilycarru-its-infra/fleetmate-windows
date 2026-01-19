using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FleetMate.Models.Graph;
using Serilog;

namespace FleetMate.Services;

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

    private const string GraphScope = "https://graph.microsoft.com/.default";

    // Caches
    private readonly Dictionary<string, (EntraUser user, DateTime expiry)> _userCache = new();
    private readonly Dictionary<string, (EntraGroup group, DateTime expiry)> _groupCache = new();
    private readonly TimeSpan _cacheDuration;

    // Microsoft Graph resource ID
    private const string GraphResourceId = "https://graph.microsoft.com";

    public GraphService(GraphConfig config)
    {
        _config = config;
        _cacheDuration = TimeSpan.FromMinutes(config.CacheMinutes);

        _client = new HttpClient
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
        var token = await GetAccessTokenAsync();
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return true;
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
                Log.Warning("Failed to get user {User}: {Status}", userPrincipalNameOrId, response.StatusCode);
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
                    Log.Warning("Failed to get groups for user {User}: {Status}", userPrincipalNameOrId, response.StatusCode);
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
                Log.Warning("Failed to get group {Group}: {Status}", displayName, response.StatusCode);
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
                Log.Warning("Failed to get group {GroupId}: {Status}", groupId, response.StatusCode);
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

    #endregion

    public void Dispose()
    {
        _client.Dispose();
    }
}
