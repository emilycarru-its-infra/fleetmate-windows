using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FleetMate.Config;
using FleetMate.Models.AzureDevOps;
using Serilog;

namespace FleetMate.Services;

/// <summary>
/// Azure DevOps service for work item management
/// Uses Azure CLI SSO for authentication
/// </summary>
public class AzureDevOpsService : IDisposable
{
    private readonly HttpClient _client;
    private readonly AzureDevOpsConfig _config;
    private readonly JsonSerializerOptions _jsonOptions;
    private string? _cachedToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    // SSO token (set externally via OAuth2 PKCE flow)
    private string? _ssoToken;
    private DateTime _ssoTokenExpiry = DateTime.MinValue;
    private string? _ssoUserName;

    // Caches
    private List<Sprint>? _sprintCache;
    private DateTime _sprintCacheExpiry = DateTime.MinValue;
    private readonly TimeSpan _cacheDuration;

    // Azure DevOps resource ID for token acquisition
    private const string AdoResourceId = "499b84ac-1321-427f-aa17-267ca6975798";

    /// <summary>Whether the user is authenticated via SSO</summary>
    public bool IsSsoAuthenticated => _ssoToken != null && DateTime.UtcNow < _ssoTokenExpiry;

    /// <summary>Display name of the SSO-authenticated user</summary>
    public string? SsoUserName => _ssoUserName;

    /// <summary>
    /// Set an OAuth2 SSO access token (from WebView2 PKCE flow)
    /// </summary>
    public void SetSsoToken(string token, DateTime expiry, string? userName = null)
    {
        _ssoToken = token;
        _ssoTokenExpiry = expiry;
        _ssoUserName = userName;
        // Also set as cached token so existing auth flow uses it
        _cachedToken = token;
        _tokenExpiry = expiry;
        Log.Information("AzureDevOpsService: SSO token set for {UserName}, expires {Expiry}", userName ?? "(unknown)", expiry);
    }

    /// <summary>
    /// Clear the SSO token (sign out)
    /// </summary>
    public void ClearSsoToken()
    {
        _ssoToken = null;
        _ssoTokenExpiry = DateTime.MinValue;
        _ssoUserName = null;
        _cachedToken = null;
        _tokenExpiry = DateTime.MinValue;
        Log.Information("AzureDevOpsService: SSO token cleared");
    }

    public AzureDevOpsService(AzureDevOpsConfig config)
    {
        _config = config;
        _cacheDuration = TimeSpan.FromMinutes(config.CacheMinutes);

        _client = new HttpClient
        {
            BaseAddress = new Uri($"https://dev.azure.com/{config.Organization}/"),
            Timeout = TimeSpan.FromSeconds(60)
        };

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    /// <summary>
    /// Get access token using Azure CLI SSO
    /// </summary>
    private async Task<string?> GetAccessTokenAsync()
    {
        // Check cached token
        if (_cachedToken != null && DateTime.UtcNow < _tokenExpiry)
        {
            return _cachedToken;
        }

        try
        {
            // Find Azure CLI - on Windows it's az.cmd in Program Files
            var azPath = FindAzureCli();
            if (azPath == null)
            {
                Log.Error("Azure CLI (az) not found. Please install Azure CLI.");
                return null;
            }

            var psi = new ProcessStartInfo
            {
                FileName = azPath,
                Arguments = $"account get-access-token --resource {AdoResourceId} --query accessToken -o tsv",
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
            _tokenExpiry = DateTime.UtcNow.AddMinutes(55); // Tokens typically expire in 60 min

            Log.Debug("Acquired Azure DevOps access token via Azure CLI");
            return _cachedToken;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get Azure DevOps access token");
            return null;
        }
    }

    /// <summary>
    /// Set authorization header for request
    /// </summary>
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

    /// <summary>
    /// Run a WIQL query to get work items
    /// </summary>
    public async Task<List<WorkItem>> QueryWorkItemsAsync(string wiql)
    {
        if (!await SetAuthorizationAsync())
        {
            Log.Warning("Failed to authenticate to Azure DevOps");
            return new List<WorkItem>();
        }

        try
        {
            // Execute WIQL query
            var queryUrl = $"{_config.Project}/_apis/wit/wiql?api-version=7.0";
            var queryBody = new { query = wiql };
            var queryJson = JsonSerializer.Serialize(queryBody);
            var content = new StringContent(queryJson, Encoding.UTF8, "application/json");

            var response = await _client.PostAsync(queryUrl, content);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Log.Warning("WIQL query failed: {Status} - {Error}", response.StatusCode, error);
                return new List<WorkItem>();
            }

            var queryResult = await response.Content.ReadFromJsonAsync<WorkItemQueryResult>(_jsonOptions);
            if (queryResult?.WorkItems == null || queryResult.WorkItems.Count == 0)
            {
                return new List<WorkItem>();
            }

            // Get full work item details
            var ids = queryResult.WorkItems.Select(w => w.Id).ToList();
            return await GetWorkItemsByIdsAsync(ids);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to query work items");
            return new List<WorkItem>();
        }
    }

    /// <summary>
    /// Get work items by IDs
    /// </summary>
    public async Task<List<WorkItem>> GetWorkItemsByIdsAsync(List<int> ids)
    {
        if (ids.Count == 0) return new List<WorkItem>();

        if (!await SetAuthorizationAsync())
        {
            return new List<WorkItem>();
        }

        try
        {
            // Batch get work items (max 200 per request)
            var allItems = new List<WorkItem>();

            foreach (var batch in ids.Chunk(200))
            {
                var idsParam = string.Join(",", batch);
                var url = $"{_config.Project}/_apis/wit/workitems?ids={idsParam}&api-version=7.0";

                var response = await _client.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    Log.Warning("Failed to get work items batch: {Status}", response.StatusCode);
                    continue;
                }

                var result = await response.Content.ReadFromJsonAsync<WorkItemBatchResponse>(_jsonOptions);
                if (result?.Value != null)
                {
                    allItems.AddRange(result.Value);
                }
            }

            return allItems;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get work items by IDs");
            return new List<WorkItem>();
        }
    }

    /// <summary>
    /// Get a single work item by ID
    /// </summary>
    public async Task<WorkItem?> GetWorkItemAsync(int id)
    {
        if (!await SetAuthorizationAsync())
        {
            return null;
        }

        try
        {
            var url = $"{_config.Project}/_apis/wit/workitems/{id}?api-version=7.0";
            var response = await _client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Failed to get work item {Id}: {Status}", id, response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<WorkItem>(_jsonOptions);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get work item {Id}", id);
            return null;
        }
    }

    /// <summary>
    /// Get work items with optional filters
    /// </summary>
    public async Task<List<WorkItem>> GetWorkItemsAsync(
        string? state = null,
        string? type = null,
        string? assignedTo = null,
        int limit = 50)
    {
        var conditions = new List<string> { "[System.TeamProject] = @project" };

        if (!string.IsNullOrEmpty(state))
            conditions.Add($"[System.State] = '{state}'");
        if (!string.IsNullOrEmpty(type))
            conditions.Add($"[System.WorkItemType] = '{type}'");
        if (!string.IsNullOrEmpty(assignedTo))
            conditions.Add($"[System.AssignedTo] = '{assignedTo}'");

        var wiql = $"SELECT [System.Id] FROM WorkItems WHERE {string.Join(" AND ", conditions)} ORDER BY [System.ChangedDate] DESC";

        var items = await QueryWorkItemsAsync(wiql);
        return items.Take(limit).ToList();
    }

    /// <summary>
    /// Create a new work item
    /// </summary>
    public async Task<WorkItem?> CreateWorkItemAsync(CreateWorkItemRequest request)
    {
        if (!await SetAuthorizationAsync())
        {
            return null;
        }

        try
        {
            var operations = new List<JsonPatchOperation>
            {
                new() { Op = "add", Path = "/fields/System.Title", Value = request.Title }
            };

            if (!string.IsNullOrEmpty(request.Description))
                operations.Add(new() { Op = "add", Path = "/fields/System.Description", Value = request.Description });
            if (!string.IsNullOrEmpty(request.AssignedTo))
                operations.Add(new() { Op = "add", Path = "/fields/System.AssignedTo", Value = request.AssignedTo });
            if (request.Priority.HasValue)
                operations.Add(new() { Op = "add", Path = "/fields/Microsoft.VSTS.Common.Priority", Value = request.Priority.Value });
            if (!string.IsNullOrEmpty(request.IterationPath))
                operations.Add(new() { Op = "add", Path = "/fields/System.IterationPath", Value = request.IterationPath });
            if (!string.IsNullOrEmpty(request.AreaPath))
                operations.Add(new() { Op = "add", Path = "/fields/System.AreaPath", Value = request.AreaPath });
            if (request.Tags?.Count > 0)
                operations.Add(new() { Op = "add", Path = "/fields/System.Tags", Value = string.Join("; ", request.Tags) });

            var url = $"{_config.Project}/_apis/wit/workitems/${request.Type}?api-version=7.0";
            var json = JsonSerializer.Serialize(operations, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json-patch+json");

            var response = await _client.PostAsync(url, content);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Log.Warning("Failed to create work item: {Status} - {Error}", response.StatusCode, error);
                return null;
            }

            var workItem = await response.Content.ReadFromJsonAsync<WorkItem>(_jsonOptions);
            Log.Information("Created work item {Id}: {Title}", workItem?.Id, request.Title);
            return workItem;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create work item");
            return null;
        }
    }

    /// <summary>
    /// Update an existing work item
    /// </summary>
    public async Task<WorkItem?> UpdateWorkItemAsync(int id, UpdateWorkItemRequest request)
    {
        if (!await SetAuthorizationAsync())
        {
            return null;
        }

        try
        {
            var operations = new List<JsonPatchOperation>();

            if (!string.IsNullOrEmpty(request.Title))
                operations.Add(new() { Op = "add", Path = "/fields/System.Title", Value = request.Title });
            if (!string.IsNullOrEmpty(request.State))
                operations.Add(new() { Op = "add", Path = "/fields/System.State", Value = request.State });
            if (!string.IsNullOrEmpty(request.AssignedTo))
                operations.Add(new() { Op = "add", Path = "/fields/System.AssignedTo", Value = request.AssignedTo });
            if (request.Priority.HasValue)
                operations.Add(new() { Op = "add", Path = "/fields/Microsoft.VSTS.Common.Priority", Value = request.Priority.Value });
            if (!string.IsNullOrEmpty(request.IterationPath))
                operations.Add(new() { Op = "add", Path = "/fields/System.IterationPath", Value = request.IterationPath });
            if (!string.IsNullOrEmpty(request.Comment))
                operations.Add(new() { Op = "add", Path = "/fields/System.History", Value = request.Comment });

            if (operations.Count == 0)
            {
                Log.Warning("No updates specified for work item {Id}", id);
                return await GetWorkItemAsync(id);
            }

            var url = $"{_config.Project}/_apis/wit/workitems/{id}?api-version=7.0";
            var json = JsonSerializer.Serialize(operations, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json-patch+json");

            var request2 = new HttpRequestMessage(new HttpMethod("PATCH"), url) { Content = content };
            var response = await _client.SendAsync(request2);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Log.Warning("Failed to update work item {Id}: {Status} - {Error}", id, response.StatusCode, error);
                return null;
            }

            var workItem = await response.Content.ReadFromJsonAsync<WorkItem>(_jsonOptions);
            Log.Information("Updated work item {Id}", id);
            return workItem;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to update work item {Id}", id);
            return null;
        }
    }

    /// <summary>
    /// Get sprints/iterations
    /// </summary>
    public async Task<List<Sprint>> GetSprintsAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _sprintCache != null && DateTime.UtcNow < _sprintCacheExpiry)
        {
            return _sprintCache;
        }

        if (!await SetAuthorizationAsync())
        {
            return _sprintCache ?? new List<Sprint>();
        }

        try
        {
            var url = $"{_config.Project}/_apis/work/teamsettings/iterations?api-version=7.0";
            var response = await _client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Failed to get sprints: {Status}", response.StatusCode);
                return _sprintCache ?? new List<Sprint>();
            }

            var result = await response.Content.ReadFromJsonAsync<IterationsResponse>(_jsonOptions);
            _sprintCache = result?.Value ?? new List<Sprint>();
            _sprintCacheExpiry = DateTime.UtcNow.Add(_cacheDuration);

            Log.Debug("Cached {Count} sprints from Azure DevOps", _sprintCache.Count);
            return _sprintCache;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get sprints");
            return _sprintCache ?? new List<Sprint>();
        }
    }

    /// <summary>
    /// Get current sprint
    /// </summary>
    public async Task<Sprint?> GetCurrentSprintAsync()
    {
        var sprints = await GetSprintsAsync();
        return sprints.FirstOrDefault(s => s.IsCurrent);
    }

    /// <summary>
    /// Get boards
    /// </summary>
    public async Task<List<Board>> GetBoardsAsync()
    {
        if (!await SetAuthorizationAsync())
        {
            return new List<Board>();
        }

        try
        {
            var url = $"{_config.Project}/_apis/work/boards?api-version=7.0";
            var response = await _client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Failed to get boards: {Status}", response.StatusCode);
                return new List<Board>();
            }

            var result = await response.Content.ReadFromJsonAsync<BoardsResponse>(_jsonOptions);
            return result?.Value ?? new List<Board>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get boards");
            return new List<Board>();
        }
    }

    /// <summary>
    /// Create a work item from a FleetMate error
    /// </summary>
    public async Task<WorkItem?> CreateFromErrorAsync(
        string deviceName,
        string itemName,
        string errorMessage,
        string? assignedTo = null,
        int priority = 2)
    {
        var title = $"[FleetMate] {itemName} failed on {deviceName}";
        var description = $@"<h3>Installation Failure</h3>
<p><strong>Device:</strong> {deviceName}</p>
<p><strong>Package:</strong> {itemName}</p>
<p><strong>Error:</strong></p>
<pre>{errorMessage}</pre>
<hr/>
<p><em>Created automatically by FleetMate</em></p>";

        var request = new CreateWorkItemRequest
        {
            Title = title,
            Type = _config.DefaultWorkItemType,
            Description = description,
            AssignedTo = assignedTo,
            Priority = priority,
            Tags = new List<string> { "FleetMate", "AutoGenerated", itemName }
        };

        return await CreateWorkItemAsync(request);
    }

    /// <summary>
    /// Find Azure CLI executable path
    /// </summary>
    private static string? FindAzureCli()
    {
        // Try common paths on Windows
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"),
            @"C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd",
            @"C:\Program Files (x86)\Microsoft SDKs\Azure\CLI2\wbin\az.cmd"
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Try to find in PATH
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

    public void Dispose()
    {
        _client.Dispose();
    }
}
