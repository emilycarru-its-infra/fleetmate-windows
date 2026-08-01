using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Shared;
using Serilog;

namespace FleetMate.Core.Services.Projects;

/// <summary>
/// Azure DevOps service for work item management.
///
/// Authentication: an SSO token injected from the browser OAuth2 PKCE flow, or
/// one minted silently by the Windows account broker. No PAT, no service
/// account — every call is attributed to the operator who made it.
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

    /// <summary>Whether we have a valid Bearer token (with 60s buffer)</summary>
    public bool HasValidToken =>
        !string.IsNullOrEmpty(_cachedToken) && DateTime.UtcNow.AddSeconds(60) < _tokenExpiry;

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
            BaseAddress = new Uri($"https://azure-devops.example.com/{config.Organization}/"),
            Timeout = TimeSpan.FromSeconds(60)
        };

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    /// <summary>
    /// Get access token using SSO or Azure CLI.
    /// NO PAT — Azure DevOps uses SSO only (browser OAuth2 PKCE or Azure CLI with Platform SSO).
    /// </summary>
    private async Task<string?> GetAccessTokenAsync()
    {
        // Check cached token
        if (_cachedToken != null && DateTime.UtcNow < _tokenExpiry)
        {
            return _cachedToken;
        }

        // Re-acquire silently through the Windows broker.
        //
        // This is what makes a mid-session 401 recoverable. Previously the
        // service was handed a token from outside and had no way to ask for
        // another, so one rejection was terminal: the board stayed populated
        // from cache while opening any item reported "Not authenticated to Azure
        // DevOps. SSO login required." — even though a fresh token was available
        // for the asking.
        try
        {
            var source = EntraTokenSource.Shared;
            if (source == null)
            {
                Log.Error("[azdo] Entra sign-in is not configured — cannot acquire an Azure DevOps token");
                return null;
            }

            _cachedToken = await source.GetTokenAsync(AdoResourceId);
            _tokenExpiry = DateTime.UtcNow.AddMinutes(55);
            Log.Debug("[azdo] Acquired an Azure DevOps access token via the Entra broker");
            return _cachedToken;
        }
        catch (EntraTokenException ex)
        {
            Log.Error("[azdo] Could not acquire an Azure DevOps token: {Message}", ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[azdo] Failed to get Azure DevOps access token");
            return null;
        }
    }

    /// <summary>
    /// Force the next request to mint a fresh token.
    ///
    /// Called when Azure DevOps rejects the token we hold. A 401 is not
    /// necessarily expiry — Azure DevOps has been observed answering TF400813
    /// for the anonymous user on a token with 40 minutes left — so the local
    /// staleness check cannot be trusted to catch it.
    /// </summary>
    public void InvalidateToken()
    {
        _cachedToken = null;
        _tokenExpiry = DateTime.MinValue;
        EntraTokenSource.Shared?.Invalidate();
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
    /// Escape a value for safe inclusion in a WIQL single-quoted string literal.
    /// WIQL uses single-quoted strings; escape ' as ''.
    /// </summary>
    private static string EscapeWiql(string value) => value.Replace("'", "''");

    /// <summary>
    /// Run a WIQL query to get work items.
    /// Set orgLevel=true to query across all projects in the organization.
    /// </summary>
    public async Task<List<WorkItem>> QueryWorkItemsAsync(string wiql, bool orgLevel = false)
    {
        if (!await SetAuthorizationAsync())
        {
            Log.Warning("Failed to authenticate to Azure DevOps");
            return new List<WorkItem>();
        }

        try
        {
            // Execute WIQL query — org-level or project-scoped
            var queryUrl = orgLevel
                ? "_apis/wit/wiql?api-version=7.0"
                : $"{_config.Project}/_apis/wit/wiql?api-version=7.0";
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
    /// Get work items with optional filters.
    /// Queries at org level (across all projects) by default.
    /// </summary>
    public async Task<List<WorkItem>> GetWorkItemsAsync(
        string? state = null,
        string? type = null,
        string? assignedTo = null,
        int limit = 50,
        bool orgLevel = true)
    {
        var conditions = new List<string>();

        if (!orgLevel)
            conditions.Add("[System.TeamProject] = @project");

        if (!string.IsNullOrEmpty(state))
            conditions.Add($"[System.State] = '{EscapeWiql(state)}'");
        if (!string.IsNullOrEmpty(type))
            conditions.Add($"[System.WorkItemType] = '{EscapeWiql(type)}'");
        if (!string.IsNullOrEmpty(assignedTo))
            conditions.Add($"[System.AssignedTo] = '{EscapeWiql(assignedTo)}'");

        var whereClause = conditions.Count > 0
            ? $" WHERE {string.Join(" AND ", conditions)}"
            : "";
        var wiql = $"SELECT [System.Id] FROM WorkItems{whereClause} ORDER BY [System.ChangedDate] DESC";

        var items = await QueryWorkItemsAsync(wiql, orgLevel);
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

    // MARK: - Auth Verification

    /// <summary>
    /// Verify the REST API is accessible with the current token (lightweight check).
    /// </summary>
    public async Task<bool> VerifyAuthAsync()
    {
        if (!await SetAuthorizationAsync()) return false;

        try
        {
            var response = await _client.GetAsync("_apis/projects?api-version=7.0&$top=1");
            var ok = response.IsSuccessStatusCode;
            Log.Debug("AzureDevOps auth verified: {Result} (status={Status})", ok, response.StatusCode);
            return ok;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AzureDevOps auth verification failed");
            return false;
        }
    }

    // MARK: - Project Discovery

    /// <summary>
    /// List all projects in the organization.
    /// </summary>
    #region My Pull Requests (organization-wide)

    private DevOpsIdentitySummary? _identityCache;

    /// <summary>Organization root, used to build web links to pull requests.</summary>
    private string OrgUrl => $"https://azure-devops.example.com/{_config.Organization}";

    /// <summary>
    /// Cached identity of the signed-in user. Returns an unresolved summary if it
    /// cannot be determined, which pushes <see cref="GetMyPullRequestsAsync"/>
    /// onto its client-side matching fallback rather than returning nothing.
    /// </summary>
    public async Task<DevOpsIdentitySummary> GetCurrentIdentityAsync()
    {
        if (_identityCache != null) return _identityCache;

        try
        {
            if (!await SetAuthorizationAsync()) return new DevOpsIdentitySummary();

            var response = await _client.GetAsync("_apis/connectionData?api-version=7.1-preview");
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("[azdo] connectionData failed: {Status}", response.StatusCode);
                return new DevOpsIdentitySummary();
            }

            var data = await response.Content.ReadFromJsonAsync<DevOpsConnectionData>(_jsonOptions);
            var identity = data?.AuthorizedUser ?? data?.AuthenticatedUser;

            _identityCache = new DevOpsIdentitySummary
            {
                Id = identity?.Id,
                DisplayName = identity?.DisplayName,
                Account = identity?.UniqueName,
            };

            Log.Information("[azdo] identity: id={Id} account={Account}",
                _identityCache.Id ?? "(unresolved)", _identityCache.Account ?? "(unknown)");
            return _identityCache;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[azdo] Failed to resolve the signed-in identity");
            return new DevOpsIdentitySummary();
        }
    }

    /// <summary>
    /// The signed-in user's pull requests across <em>every</em> project in the
    /// organization, split into "Created by me" and "Assigned to me" the same way
    /// the Azure DevOps web queue does.
    /// </summary>
    /// <param name="status">active, completed, abandoned, or all.</param>
    /// <param name="topPerQuery">Page size per project query.</param>
    public async Task<PullRequestQueue> GetMyPullRequestsAsync(
        string status = "active", int topPerQuery = 100)
    {
        var queue = new PullRequestQueue();

        try
        {
            var identity = await GetCurrentIdentityAsync();
            var projects = await ListProjectsAsync();

            Log.Information("[azdo] Building the PR queue across {Count} projects (identity={Identity})",
                projects.Count, identity.Id ?? "unresolved");

            // Fan out per project — each is an independent REST call, and one
            // project the user cannot read must not sink the whole queue.
            var perProject = await Task.WhenAll(projects.Select(p =>
                PullRequestsForProjectAsync(p.Name ?? "", identity, status, topPerQuery)));

            foreach (var pr in perProject.SelectMany(x => x)) queue.Insert(pr);

            Log.Information("[azdo] PR queue → {Count} pull requests", queue.PullRequests.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[azdo] Failed to build the pull request queue");
            queue.Errors.Add(new PullRequestQueueError
            {
                Source = PullRequestSource.AzureDevOps,
                Message = ex.Message,
            });
        }

        return queue;
    }

    private async Task<List<UnifiedPullRequest>> PullRequestsForProjectAsync(
        string projectName, DevOpsIdentitySummary identity, string status, int top)
    {
        var byId = new Dictionary<int, UnifiedPullRequest>();

        void Absorb(IEnumerable<GitPullRequest> prs, PullRequestRelation relation)
        {
            foreach (var pr in prs)
            {
                if (byId.TryGetValue(pr.PullRequestId, out var existing))
                {
                    existing.Relations.Add(relation);
                    continue;
                }

                var unified = MapPullRequest(pr, projectName, relation, OrgUrl);
                if (unified != null) byId[pr.PullRequestId] = unified;
            }
        }

        if (identity.IsResolved)
        {
            var encodedId = Uri.EscapeDataString(identity.Id!);
            var created = FetchProjectPullRequestsAsync(
                projectName, $"&searchCriteria.creatorId={encodedId}", status, top);
            var reviewing = FetchProjectPullRequestsAsync(
                projectName, $"&searchCriteria.reviewerId={encodedId}", status, top);

            Absorb(await created, PullRequestRelation.CreatedByMe);
            Absorb(await reviewing, PullRequestRelation.AssignedToMe);
        }
        else
        {
            // Identity GUID unavailable — pull the project's PRs and match on
            // whatever account name we do know.
            var all = await FetchProjectPullRequestsAsync(projectName, "", status, top);

            Absorb(all.Where(pr => MatchesMe(pr.CreatedBy, identity)), PullRequestRelation.CreatedByMe);
            Absorb(
                all.Where(pr => (pr.Reviewers ?? new List<GitPullRequestReviewer>())
                    .Any(r => r.IsContainer != true && MatchesReviewer(r, identity))),
                PullRequestRelation.AssignedToMe);
        }

        return byId.Values.ToList();
    }

    /// <summary>
    /// One PR search against one project. Swallows failures deliberately — a
    /// project the user cannot read should not sink the whole queue.
    /// </summary>
    private async Task<List<GitPullRequest>> FetchProjectPullRequestsAsync(
        string projectName, string criteria, string status, int top)
    {
        try
        {
            var path = $"{Uri.EscapeDataString(projectName)}/_apis/git/pullrequests" +
                       $"?searchCriteria.status={status}{criteria}&$top={top}&api-version=7.0";

            var response = await _client.GetAsync(path);
            if (!response.IsSuccessStatusCode)
            {
                Log.Debug("[azdo] PR query for {Project} returned {Status}", projectName, response.StatusCode);
                return new List<GitPullRequest>();
            }

            var result = await response.Content.ReadFromJsonAsync<GitPullRequestsResponse>(_jsonOptions);
            return result?.Value ?? new List<GitPullRequest>();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[azdo] PR query failed for {Project}", projectName);
            return new List<GitPullRequest>();
        }
    }

    private static bool MatchesMe(IdentityRef? reference, DevOpsIdentitySummary identity)
    {
        if (reference == null) return false;

        static bool Same(string? a, string? b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
            && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        return Same(identity.Id, reference.Id)
            || Same(identity.Account, reference.UniqueName)
            || Same(identity.DisplayName, reference.DisplayName);
    }

    private static bool MatchesReviewer(GitPullRequestReviewer reviewer, DevOpsIdentitySummary identity) =>
        MatchesMe(
            new IdentityRef
            {
                Id = reviewer.Id ?? string.Empty,
                DisplayName = reviewer.DisplayName ?? string.Empty,
                UniqueName = reviewer.UniqueName ?? string.Empty,
            },
            identity);

    /// <summary>
    /// Convert an Azure DevOps PR into the unified shape. Returns null when the
    /// payload lacks the repository context needed to build a web URL — a row
    /// nobody can open is worse than no row.
    /// </summary>
    internal static UnifiedPullRequest? MapPullRequest(
        GitPullRequest pr, string project, PullRequestRelation relation, string? orgUrl = null)
    {
        var repo = pr.Repository?.Name;
        if (string.IsNullOrEmpty(repo)) return null;

        var projectName = pr.Repository?.Project?.Name ?? project;

        // Draft wins over status: Azure DevOps reports a draft as "active".
        var state = pr.IsDraft == true
            ? PullRequestState.Draft
            : pr.Status?.ToLowerInvariant() switch
            {
                "completed" => PullRequestState.Merged,
                "abandoned" => PullRequestState.Closed,
                _ => PullRequestState.Open,
            };

        var reviewers = (pr.Reviewers ?? new List<GitPullRequestReviewer>())
            .Where(r => r.IsContainer != true)
            .Select(r => new PullRequestReviewer
            {
                Id = r.Id ?? r.DisplayName ?? Guid.NewGuid().ToString(),
                DisplayName = r.DisplayName ?? r.UniqueName ?? "Unknown",
                Vote = PullRequestReviewVoteExtensions.FromAzureDevOps(r.Vote),
                IsRequired = r.IsRequired ?? false,
            })
            .ToList();

        return new UnifiedPullRequest
        {
            Source = PullRequestSource.AzureDevOps,
            Number = pr.PullRequestId,
            Title = pr.Title ?? "(untitled)",
            AuthorName = pr.CreatedBy?.DisplayName ?? pr.CreatedBy?.UniqueName ?? "Unknown",
            Container = projectName,
            Repository = repo,
            SourceBranch = ShortBranchName(pr.SourceRefName),
            TargetBranch = ShortBranchName(pr.TargetRefName),
            CreatedAt = PullRequestDateParser.Parse(pr.CreationDate),
            UpdatedAt = PullRequestDateParser.Parse(pr.ClosedDate),
            State = state,
            HasConflicts = pr.HasConflicts,
            Reviewers = reviewers,
            WebUrl = PullRequestWebUrl(orgUrl, projectName, repo, pr.PullRequestId),
            Relations = new HashSet<PullRequestRelation> { relation },
        };
    }

    /// <summary>Strip the <c>refs/heads/</c> prefix Azure DevOps puts on branch names.</summary>
    internal static string ShortBranchName(string? refName)
    {
        if (string.IsNullOrEmpty(refName)) return string.Empty;
        const string prefix = "refs/heads/";
        return refName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? refName[prefix.Length..]
            : refName;
    }

    internal static string PullRequestWebUrl(string? orgUrl, string project, string repository, int pullRequestId)
    {
        var root = (orgUrl ?? "https://azure-devops.example.com").TrimEnd('/');
        return $"{root}/{Uri.EscapeDataString(project)}/_git/{Uri.EscapeDataString(repository)}/pullrequest/{pullRequestId}";
    }

    /// <summary>
    /// Abandon a pull request.
    ///
    /// Reversible in Azure DevOps — an abandoned PR can be reactivated — but it
    /// notifies reviewers, so callers should confirm first.
    /// </summary>
    public async Task<PullRequestActionResult> AbandonPullRequestAsync(
        string repository, int pullRequestId, string? project = null)
    {
        if (!await SetAuthorizationAsync())
            return PullRequestActionResult.Failed("Not authenticated to Azure DevOps");

        Log.Information("[azdo] Abandoning {Repository}#{Id}", repository, pullRequestId);

        try
        {
            var path = PullRequestPath(repository, pullRequestId, project);
            var body = new StringContent(
                JsonSerializer.Serialize(new { status = "abandoned" }, _jsonOptions),
                Encoding.UTF8, "application/json");

            var response = await _client.SendAsync(
                new HttpRequestMessage(HttpMethod.Patch, path) { Content = body });

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Log.Warning("[azdo] Abandon failed for {Repository}#{Id}: {Status} - {Error}",
                    repository, pullRequestId, response.StatusCode, error);
                return PullRequestActionResult.Failed($"{(int)response.StatusCode}: {Truncate(error)}");
            }

            return PullRequestActionResult.Ok();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[azdo] Abandon failed for {Repository}#{Id}", repository, pullRequestId);
            return PullRequestActionResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Complete (merge) a pull request.
    ///
    /// Two calls, because Azure DevOps requires <c>lastMergeSourceCommit</c> to be
    /// echoed back on completion and rejects the request if the source branch has
    /// moved since — so the current value has to be read immediately beforehand.
    ///
    /// <c>completionOptions</c> is deliberately omitted. The PR already carries
    /// the merge strategy and delete-source-branch settings chosen when it was
    /// opened, and sending our own would silently override branch policy.
    /// </summary>
    public async Task<PullRequestActionResult> CompletePullRequestAsync(
        string repository, int pullRequestId, string? project = null)
    {
        if (!await SetAuthorizationAsync())
            return PullRequestActionResult.Failed("Not authenticated to Azure DevOps");

        Log.Information("[azdo] Completing {Repository}#{Id}", repository, pullRequestId);

        try
        {
            var path = PullRequestPath(repository, pullRequestId, project);

            var currentResponse = await _client.GetAsync(path);
            if (!currentResponse.IsSuccessStatusCode)
            {
                var error = await currentResponse.Content.ReadAsStringAsync();
                return PullRequestActionResult.Failed(
                    $"Could not read the pull request: {(int)currentResponse.StatusCode}: {Truncate(error)}");
            }

            var current = await currentResponse.Content.ReadFromJsonAsync<GitPullRequest>(_jsonOptions);
            var commitId = current?.LastMergeSourceCommit?.CommitId;

            if (string.IsNullOrEmpty(commitId))
            {
                // Reporting this beats sending a completion that is certain to
                // fail, and names the likely cause rather than surfacing a raw 409.
                return PullRequestActionResult.Failed(
                    "Azure DevOps has not produced a merge commit for this pull request yet. " +
                    "This usually means it still has conflicts, or the merge is still being evaluated.");
            }

            var body = new StringContent(
                JsonSerializer.Serialize(new
                {
                    status = "completed",
                    lastMergeSourceCommit = new { commitId },
                }, _jsonOptions),
                Encoding.UTF8, "application/json");

            var response = await _client.SendAsync(
                new HttpRequestMessage(HttpMethod.Patch, path) { Content = body });

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Log.Warning("[azdo] Complete failed for {Repository}#{Id}: {Status} - {Error}",
                    repository, pullRequestId, response.StatusCode, error);
                return PullRequestActionResult.Failed($"{(int)response.StatusCode}: {Truncate(error)}");
            }

            return PullRequestActionResult.Ok();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[azdo] Complete failed for {Repository}#{Id}", repository, pullRequestId);
            return PullRequestActionResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Everything the in-app viewer needs: description, commits, comment
    /// threads and per-file diffs.
    ///
    /// Azure DevOps has no unified-diff endpoint of any kind, so the diffs are
    /// computed here from the blob contents at either end of the pull request.
    /// That means two extra reads per file, which is why the file list is capped
    /// and the cap is reported rather than silently applied.
    /// </summary>
    public async Task<PullRequestDetail> GetPullRequestDetailAsync(
        string repository, int pullRequestId, string? project = null, int fileCap = 40)
    {
        if (!await SetAuthorizationAsync())
            throw new InvalidOperationException("Not authenticated to Azure DevOps");

        var repo = Uri.EscapeDataString(repository);
        var prefix = string.IsNullOrWhiteSpace(project) ? "" : $"{Uri.EscapeDataString(project)}/";
        var prBase = $"{prefix}_apis/git/repositories/{repo}/pullRequests/{pullRequestId}";

        var headTask = GetJsonAsync($"{prBase}?api-version=7.0");
        var commitsTask = GetJsonAsync($"{prBase}/commits?api-version=7.0");
        var threadsTask = GetJsonAsync($"{prBase}/threads?api-version=7.0");

        await Task.WhenAll(headTask, commitsTask, threadsTask);

        var head = await headTask;
        var commits = await commitsTask;
        var threads = await threadsTask;

        var (files, truncated) = await BuildPullRequestDiffsAsync(prBase, repo, prefix, head, fileCap);

        return new PullRequestDetail
        {
            Body = Str(head, "description"),
            Commits = ParseDevOpsCommits(commits),
            Comments = ParseDevOpsComments(threads),
            Files = files,
            Truncated = truncated,
        };
    }

    private async Task<(List<DiffFile> Files, bool Truncated)> BuildPullRequestDiffsAsync(
        string prBase, string repo, string prefix, JsonElement head, int fileCap)
    {
        var files = new List<DiffFile>();

        var source = head.TryGetProperty("lastMergeSourceCommit", out var s) ? Str(s, "commitId") : null;
        var target = head.TryGetProperty("lastMergeTargetCommit", out var t) ? Str(t, "commitId") : null;

        // Without both ends there is nothing to compare — an unmergeable PR or
        // one whose merge is still being evaluated.
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target)) return (files, false);

        var iterations = await GetJsonAsync($"{prBase}/iterations?api-version=7.0");
        var latest = 0;

        if (iterations.TryGetProperty("value", out var iterationList) && iterationList.ValueKind == JsonValueKind.Array)
        {
            foreach (var iteration in iterationList.EnumerateArray())
            {
                if (iteration.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
                    latest = Math.Max(latest, id.GetInt32());
            }
        }

        if (latest == 0) return (files, false);

        var changes = await GetJsonAsync(
            $"{prBase}/iterations/{latest}/changes?api-version=7.0&$compareTo=0");

        if (!changes.TryGetProperty("changeEntries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return (files, false);

        var all = entries.EnumerateArray().ToList();
        var truncated = all.Count > fileCap;

        foreach (var entry in all.Take(fileCap))
        {
            if (!entry.TryGetProperty("item", out var item)) continue;

            var path = Str(item, "path");
            if (string.IsNullOrEmpty(path)) continue;

            // Folder entries carry no content to diff.
            if (item.TryGetProperty("isFolder", out var isFolder) && isFolder.ValueKind == JsonValueKind.True)
                continue;

            var changeType = (Str(entry, "changeType") ?? "").ToLowerInvariant();
            var isAdd = changeType.Contains("add");
            var isDelete = changeType.Contains("delete");

            // An added file has no old side and a deleted one has no new side;
            // asking for the missing blob would 404 for every such file.
            var oldContent = isAdd ? "" : await ItemContentAsync(repo, prefix, path, target!);
            var newContent = isDelete ? "" : await ItemContentAsync(repo, prefix, path, source!);

            if (oldContent == null && newContent == null) continue;

            var name = path.StartsWith('/') ? path[1..] : path;
            var file = DiffBuilder.Build(name, oldContent ?? "", newContent ?? "");

            files.Add(isDelete
                ? new DiffFile
                {
                    HeaderLines = file.HeaderLines,
                    OldPath = file.OldPath,
                    NewPath = "/dev/null",
                    Hunks = file.Hunks,
                }
                : file);
        }

        return (files, truncated);
    }

    /// <summary>
    /// Text of one file at one commit, or null for binary, oversized or missing.
    ///
    /// Failures degrade to null deliberately: one unreadable file must not sink
    /// the whole diff, and a 400KB ceiling keeps a vendored bundle from stalling
    /// the viewer.
    /// </summary>
    private async Task<string?> ItemContentAsync(string repo, string prefix, string path, string commit)
    {
        try
        {
            var url = $"{prefix}_apis/git/repositories/{repo}/items" +
                      $"?path={Uri.EscapeDataString(path)}" +
                      $"&versionDescriptor.versionType=commit&versionDescriptor.version={commit}" +
                      "&includeContent=true&$format=json&api-version=7.0";

            var response = await _client.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            var item = await response.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
            var content = Str(item, "content");

            return content is { Length: < 400_000 } ? content : null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[azdo] Could not read {Path} at {Commit}", path, commit);
            return null;
        }
    }

    private static List<PullRequestCommit> ParseDevOpsCommits(JsonElement commits)
    {
        var result = new List<PullRequestCommit>();
        if (!commits.TryGetProperty("value", out var list) || list.ValueKind != JsonValueKind.Array) return result;

        foreach (var node in list.EnumerateArray())
        {
            var author = node.TryGetProperty("author", out var a) ? a : default;

            result.Add(new PullRequestCommit
            {
                Id = Str(node, "commitId") ?? "",
                Message = Str(node, "comment") ?? "",
                AuthorName = Str(author, "name"),
                Date = PullRequestDateParser.Parse(Str(author, "date")),
            });
        }

        return result;
    }

    private static List<PullRequestComment> ParseDevOpsComments(JsonElement threads)
    {
        var result = new List<PullRequestComment>();
        if (!threads.TryGetProperty("value", out var list) || list.ValueKind != JsonValueKind.Array) return result;

        foreach (var thread in list.EnumerateArray())
        {
            // A deleted thread still comes back; rendering it would resurrect
            // something someone chose to remove.
            if (thread.TryGetProperty("isDeleted", out var deleted) && deleted.ValueKind == JsonValueKind.True)
                continue;

            var threadId = thread.TryGetProperty("id", out var tid) && tid.ValueKind == JsonValueKind.Number
                ? tid.GetInt32()
                : 0;

            if (!thread.TryGetProperty("comments", out var comments) || comments.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var comment in comments.EnumerateArray())
            {
                var content = Str(comment, "content");
                if (string.IsNullOrEmpty(content)) continue;

                var commentId = comment.TryGetProperty("id", out var cid) && cid.ValueKind == JsonValueKind.Number
                    ? cid.GetInt32()
                    : 0;

                result.Add(new PullRequestComment
                {
                    Id = $"{threadId}-{commentId}",
                    AuthorName = comment.TryGetProperty("author", out var author)
                        ? Str(author, "displayName") ?? "unknown"
                        : "unknown",
                    Body = content,
                    Date = PullRequestDateParser.Parse(Str(comment, "publishedDate")),
                    // Azure DevOps folds vote changes into the thread list as
                    // system comments; they are noise beside real conversation.
                    IsSystem = string.Equals(Str(comment, "commentType"), "system", StringComparison.OrdinalIgnoreCase),
                });
            }
        }

        return result
            .OrderBy(c => c.Date ?? DateTime.MinValue)
            .ToList();
    }

    private async Task<JsonElement> GetJsonAsync(string url)
    {
        var response = await _client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
    }

    private static string? Str(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static string PullRequestPath(string repository, int pullRequestId, string? project)
    {
        var repoSegment = Uri.EscapeDataString(repository);
        var prefix = string.IsNullOrWhiteSpace(project) ? "" : $"{Uri.EscapeDataString(project)}/";
        return $"{prefix}_apis/git/repositories/{repoSegment}/pullrequests/{pullRequestId}?api-version=7.0";
    }

    private static string Truncate(string s) =>
        string.IsNullOrEmpty(s) ? "(no body)" : (s.Length > 300 ? s[..300] + "…" : s);

    #endregion

    public async Task<List<DevOpsProject>> ListProjectsAsync()
    {
        if (!await SetAuthorizationAsync()) return new List<DevOpsProject>();

        try
        {
            var response = await _client.GetAsync("_apis/projects?api-version=7.0");
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Failed to list projects: {Status}", response.StatusCode);
                return new List<DevOpsProject>();
            }

            var result = await response.Content.ReadFromJsonAsync<DevOpsProjectsResponse>(_jsonOptions);
            return result?.Value ?? new List<DevOpsProject>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to list projects");
            return new List<DevOpsProject>();
        }
    }

    /// <summary>
    /// Auto-discover the best project (most active work items). Returns the project name or null.
    /// </summary>
    public async Task<string?> DiscoverProjectAsync()
    {
        try
        {
            var projects = await ListProjectsAsync();
            if (projects.Count == 0) return null;

            if (projects.Count == 1)
            {
                Log.Information("AzureDevOps discover: using '{Project}' (only project)", projects[0].Name);
                return projects[0].Name;
            }

            // Multiple projects — pick the one with the most active work items
            string bestProject = projects[0].Name;
            int bestCount = 0;

            foreach (var p in projects)
            {
                try
                {
                    var wiql = $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = '{EscapeWiql(p.Name)}' AND [System.State] <> 'Closed' AND [System.State] <> 'Done' AND [System.State] <> 'Removed'";
                    var items = await QueryWorkItemsAsync(wiql, orgLevel: true);
                    if (items.Count > bestCount)
                    {
                        bestCount = items.Count;
                        bestProject = p.Name;
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug("AzureDevOps discover: '{Project}' query failed: {Error}", p.Name, ex.Message);
                }
            }

            Log.Information("AzureDevOps discover: using '{Project}'{Info}", bestProject, bestCount > 0 ? " (has active items)" : " (fallback)");
            return bestProject;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AzureDevOps project discovery failed");
            return null;
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
