using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FleetMate.Config;
using FleetMate.Core.Models.Tasks;
using Serilog;

namespace FleetMate.Core.Services.Tasks;

/// <summary>
/// Gitea task provider using Gitea Issues API.
/// Maps Gitea Issues to UnifiedTask format.
/// </summary>
public class GiteaTaskProvider : ITaskProvider, IDisposable
{
    private readonly HttpClient _client;
    private readonly GiteaProviderConfig _config;
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _isAuthenticated;

    public string ProviderId => "gitea";
    public string ProviderName => "Gitea";
    
    public bool IsEnabled => _config.Enabled && 
                             !string.IsNullOrEmpty(_config.Url) &&
                             !string.IsNullOrEmpty(_config.Owner) && 
                             !string.IsNullOrEmpty(_config.Repo);

    public GiteaTaskProvider(FleetMateConfig config)
    {
        _config = config.Tasks?.Providers?.Gitea ?? new GiteaProviderConfig();
        
        var baseUrl = _config.Url?.TrimEnd('/') ?? "";
        
        _client = new HttpClient
        {
            BaseAddress = new Uri($"{baseUrl}/api/v1/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        
        _client.DefaultRequestHeaders.Add("Accept", "application/json");
        
        // Set token auth header
        if (!string.IsNullOrEmpty(_config.Token))
        {
            _client.DefaultRequestHeaders.Add("Authorization", $"token {_config.Token}");
        }
        
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    public async Task<bool> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_config.Token))
        {
            // Try environment variable
            var envToken = Environment.GetEnvironmentVariable("GITEA_TOKEN");
            if (!string.IsNullOrEmpty(envToken))
            {
                _client.DefaultRequestHeaders.Remove("Authorization");
                _client.DefaultRequestHeaders.Add("Authorization", $"token {envToken}");
            }
            else
            {
                Log.Warning("Gitea: No token configured");
                return false;
            }
        }
        
        try
        {
            // Verify token by getting authenticated user
            var response = await _client.GetAsync("user", cancellationToken);
            _isAuthenticated = response.IsSuccessStatusCode;
            
            if (_isAuthenticated)
            {
                Log.Information("Authenticated with Gitea: {Url}", _config.Url);
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                Log.Warning("Gitea authentication failed: {Error}", error);
            }
            
            return _isAuthenticated;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to authenticate with Gitea");
            return false;
        }
    }

    public async Task<List<UnifiedTask>> ListTasksAsync(TaskFilter? filter = null, CancellationToken cancellationToken = default)
    {
        var queryParams = new List<string>();
        
        // State filter
        if (filter?.States?.Count > 0)
        {
            if (filter.States.All(s => s == TaskState.Closed))
                queryParams.Add("state=closed");
            else
                queryParams.Add("state=open");
        }
        else if (filter?.IncludeClosed == true)
        {
            queryParams.Add("state=all");
        }
        else
        {
            queryParams.Add("state=open");
        }
        
        // Labels filter
        if (filter?.Labels?.Count > 0)
        {
            foreach (var label in filter.Labels)
            {
                queryParams.Add($"labels={Uri.EscapeDataString(label)}");
            }
        }
        
        // Milestone (bucket) filter
        if (!string.IsNullOrEmpty(filter?.Bucket))
        {
            queryParams.Add($"milestones={Uri.EscapeDataString(filter.Bucket)}");
        }
        
        // Limit
        var limit = filter?.Limit ?? 50;
        queryParams.Add($"limit={limit}");
        
        var url = $"repos/{_config.Owner}/{_config.Repo}/issues?{string.Join("&", queryParams)}";
        
        try
        {
            var response = await _client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                Log.Warning("Gitea: Failed to list issues: {Error}", error);
                return new List<UnifiedTask>();
            }
            
            var issues = await response.Content.ReadFromJsonAsync<List<GiteaIssue>>(_jsonOptions, cancellationToken)
                ?? new List<GiteaIssue>();
            
            // Filter out pull requests
            issues = issues.Where(i => i.PullRequest == null).ToList();
            
            // Apply search text filter client-side if needed
            if (!string.IsNullOrEmpty(filter?.SearchText))
            {
                var searchLower = filter.SearchText.ToLowerInvariant();
                issues = issues.Where(i => 
                    i.Title.ToLowerInvariant().Contains(searchLower) ||
                    (i.Body?.ToLowerInvariant().Contains(searchLower) ?? false)
                ).ToList();
            }
            
            // Assignee filter (client-side for Gitea)
            if (filter?.Assignees?.Count > 0)
            {
                issues = issues.Where(i => 
                    i.Assignees?.Any(a => filter.Assignees.Contains(a.Login)) ?? false
                ).ToList();
            }
            
            return issues.Select(MapToUnifiedTask).ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Gitea: Failed to list issues");
            return new List<UnifiedTask>();
        }
    }

    public async Task<UnifiedTask?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"repos/{_config.Owner}/{_config.Repo}/issues/{taskId}";
            var response = await _client.GetAsync(url, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            
            var issue = await response.Content.ReadFromJsonAsync<GiteaIssue>(_jsonOptions, cancellationToken);
            return issue != null ? MapToUnifiedTask(issue) : null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Gitea: Failed to get issue {Id}", taskId);
            return null;
        }
    }

    public async Task<UnifiedTask> CreateTaskAsync(CreateTaskRequest request, CancellationToken cancellationToken = default)
    {
        var labels = request.Labels?.ToList() ?? new List<string>();
        labels.AddRange(_config.DefaultLabels);
        
        // Get label IDs (Gitea requires IDs, not names)
        var labelIds = await GetLabelIdsByNames(labels.Distinct().ToList(), cancellationToken);
        
        var body = new Dictionary<string, object?>
        {
            ["title"] = request.Title,
            ["body"] = request.Description
        };
        
        if (labelIds.Count > 0)
            body["labels"] = labelIds;
        if (request.Assignees?.Count > 0)
            body["assignees"] = request.Assignees;
        if (!string.IsNullOrEmpty(request.Bucket) && long.TryParse(request.Bucket, out var milestoneId))
            body["milestone"] = milestoneId;
        
        var url = $"repos/{_config.Owner}/{_config.Repo}/issues";
        var content = new StringContent(JsonSerializer.Serialize(body, _jsonOptions), Encoding.UTF8, "application/json");
        
        var response = await _client.PostAsync(url, content, cancellationToken);
        
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Gitea: Failed to create issue: {error}");
        }
        
        var issue = await response.Content.ReadFromJsonAsync<GiteaIssue>(_jsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Gitea: Empty response when creating issue");
        
        Log.Information("Gitea: Created issue #{Number}: {Title}", issue.Number, request.Title);
        return MapToUnifiedTask(issue);
    }

    public async Task<UnifiedTask> UpdateTaskAsync(string taskId, UpdateTaskRequest request, CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>();
        
        if (request.Title != null)
            body["title"] = request.Title;
        if (request.Description != null)
            body["body"] = request.Description;
        if (request.State.HasValue)
            body["state"] = request.State.Value == TaskState.Closed ? "closed" : "open";
        if (request.Labels != null)
        {
            var labelIds = await GetLabelIdsByNames(request.Labels, cancellationToken);
            body["labels"] = labelIds;
        }
        if (request.Assignees != null)
            body["assignees"] = request.Assignees;
        if (request.Bucket != null && long.TryParse(request.Bucket, out var milestoneId))
            body["milestone"] = milestoneId;
        
        var url = $"repos/{_config.Owner}/{_config.Repo}/issues/{taskId}";
        var content = new StringContent(JsonSerializer.Serialize(body, _jsonOptions), Encoding.UTF8, "application/json");
        
        var httpRequest = new HttpRequestMessage(new HttpMethod("PATCH"), url) { Content = content };
        var response = await _client.SendAsync(httpRequest, cancellationToken);
        
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Gitea: Failed to update issue #{taskId}: {error}");
        }
        
        var issue = await response.Content.ReadFromJsonAsync<GiteaIssue>(_jsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Gitea: Empty response when updating issue");
        
        return MapToUnifiedTask(issue);
    }

    public async Task<bool> DeleteTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        // Gitea supports deleting issues (unlike GitHub)
        try
        {
            var url = $"repos/{_config.Owner}/{_config.Repo}/issues/{taskId}";
            var response = await _client.DeleteAsync(url, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<TaskBucket>> ListBucketsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"repos/{_config.Owner}/{_config.Repo}/milestones?state=open";
            var response = await _client.GetAsync(url, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                return new List<TaskBucket>();
            }
            
            var milestones = await response.Content.ReadFromJsonAsync<List<GiteaMilestone>>(_jsonOptions, cancellationToken)
                ?? new List<GiteaMilestone>();
            
            return milestones.Select((m, i) => new TaskBucket
            {
                Id = m.Id.ToString(),
                Name = m.Title,
                Order = i
            }).ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Gitea: Failed to list milestones");
            return new List<TaskBucket>();
        }
    }

    public async Task<List<TaskLabel>> ListLabelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"repos/{_config.Owner}/{_config.Repo}/labels";
            var response = await _client.GetAsync(url, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                return new List<TaskLabel>();
            }
            
            var labels = await response.Content.ReadFromJsonAsync<List<GiteaLabel>>(_jsonOptions, cancellationToken)
                ?? new List<GiteaLabel>();
            
            return labels.Select(l => new TaskLabel
            {
                Name = l.Name,
                Color = l.Color?.StartsWith("#") == true ? l.Color : $"#{l.Color}",
                Description = l.Description
            }).ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Gitea: Failed to list labels");
            return new List<TaskLabel>();
        }
    }

    private async Task<List<long>> GetLabelIdsByNames(List<string> names, CancellationToken cancellationToken)
    {
        var allLabels = await ListLabelsAsync(cancellationToken);
        var labelMap = new Dictionary<string, long>();
        
        // We need the actual API response to get IDs
        try
        {
            var url = $"repos/{_config.Owner}/{_config.Repo}/labels";
            var response = await _client.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var labels = await response.Content.ReadFromJsonAsync<List<GiteaLabel>>(_jsonOptions, cancellationToken);
                if (labels != null)
                {
                    foreach (var label in labels)
                    {
                        labelMap[label.Name.ToLowerInvariant()] = label.Id;
                    }
                }
            }
        }
        catch { }
        
        return names
            .Select(n => labelMap.GetValueOrDefault(n.ToLowerInvariant()))
            .Where(id => id > 0)
            .ToList();
    }

    private UnifiedTask MapToUnifiedTask(GiteaIssue issue)
    {
        return new UnifiedTask
        {
            Id = issue.Number.ToString(),
            Provider = ProviderId,
            Title = issue.Title,
            Description = issue.Body,
            State = issue.State == "closed" ? TaskState.Closed : TaskState.Open,
            Assignees = issue.Assignees?.Select(a => a.Login).ToList() ?? new List<string>(),
            Labels = issue.Labels?.Select(l => l.Name).ToList() ?? new List<string>(),
            Bucket = issue.Milestone?.Title,
            DueDate = issue.Milestone?.DueOn,
            CreatedAt = issue.CreatedAt,
            UpdatedAt = issue.UpdatedAt,
            ClosedAt = issue.ClosedAt,
            ExternalUrl = issue.HtmlUrl,
            Priority = null
        };
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}

// Gitea API Models
internal class GiteaIssue
{
    public long Id { get; set; }
    public long Number { get; set; }
    public string Title { get; set; } = "";
    public string? Body { get; set; }
    public string State { get; set; } = "open";
    public List<GiteaUser>? Assignees { get; set; }
    public List<GiteaLabel>? Labels { get; set; }
    public GiteaMilestone? Milestone { get; set; }
    public GiteaPullRequest? PullRequest { get; set; }
    public string? HtmlUrl { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
}

internal class GiteaUser
{
    public long Id { get; set; }
    public string Login { get; set; } = "";
}

internal class GiteaLabel
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Color { get; set; }
    public string? Description { get; set; }
}

internal class GiteaMilestone
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public DateTime? DueOn { get; set; }
}

internal class GiteaPullRequest
{
    public string? Url { get; set; }
}
