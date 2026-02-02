using System.Diagnostics;
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
/// GitHub task provider using GitHub Issues API.
/// Maps GitHub Issues to UnifiedTask format.
/// </summary>
public class GitHubTaskProvider : ITaskProvider, IDisposable
{
    private readonly HttpClient _client;
    private readonly GitHubProviderConfig _config;
    private readonly JsonSerializerOptions _jsonOptions;
    private string? _cachedToken;
    private bool _isAuthenticated;

    public string ProviderId => "github";
    public string ProviderName => "GitHub";
    
    public bool IsEnabled => _config.Enabled && 
                             !string.IsNullOrEmpty(_config.Owner) && 
                             !string.IsNullOrEmpty(_config.Repo);

    public GitHubTaskProvider(FleetMateConfig config)
    {
        _config = config.Tasks?.Providers?.GitHub ?? new GitHubProviderConfig();
        
        _client = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        
        _client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        _client.DefaultRequestHeaders.Add("User-Agent", "FleetMate");
        _client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    public async Task<bool> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        var token = await GetTokenAsync();
        if (string.IsNullOrEmpty(token))
        {
            Log.Warning("GitHub: No token available");
            return false;
        }
        
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        
        try
        {
            // Verify token by getting authenticated user
            var response = await _client.GetAsync("user", cancellationToken);
            _isAuthenticated = response.IsSuccessStatusCode;
            
            if (_isAuthenticated)
            {
                Log.Information("Authenticated with GitHub as owner: {Owner}", _config.Owner);
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                Log.Warning("GitHub authentication failed: {Error}", error);
            }
            
            return _isAuthenticated;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to authenticate with GitHub");
            return false;
        }
    }

    private async Task<string?> GetTokenAsync()
    {
        if (!string.IsNullOrEmpty(_cachedToken))
            return _cachedToken;
        
        // Try configured token first
        if (!string.IsNullOrEmpty(_config.Token))
        {
            _cachedToken = _config.Token;
            return _cachedToken;
        }
        
        // Try environment variable
        var envToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN") 
                    ?? Environment.GetEnvironmentVariable("GH_TOKEN");
        if (!string.IsNullOrEmpty(envToken))
        {
            _cachedToken = envToken;
            return _cachedToken;
        }
        
        // Try gh CLI if enabled
        if (_config.UseGhCli)
        {
            _cachedToken = await GetTokenFromGhCli();
        }
        
        return _cachedToken;
    }

    private async Task<string?> GetTokenFromGhCli()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = "auth token",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            var token = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                Log.Debug("Got GitHub token from gh CLI");
                return token.Trim();
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to get token from gh CLI");
        }
        
        return null;
    }

    public async Task<List<UnifiedTask>> ListTasksAsync(TaskFilter? filter = null, CancellationToken cancellationToken = default)
    {
        var queryParams = new List<string>();
        
        // State filter
        if (filter?.States?.Count > 0)
        {
            // GitHub only supports one state at a time, default to 'open' if mixed
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
            queryParams.Add($"labels={string.Join(",", filter.Labels)}");
        }
        
        // Assignee filter
        if (filter?.Assignees?.Count > 0)
        {
            queryParams.Add($"assignee={filter.Assignees.First()}");
        }
        
        // Milestone (bucket) filter
        if (!string.IsNullOrEmpty(filter?.Bucket))
        {
            queryParams.Add($"milestone={filter.Bucket}");
        }
        
        // Limit
        var perPage = Math.Min(filter?.Limit ?? 100, 100);
        queryParams.Add($"per_page={perPage}");
        
        var url = $"repos/{_config.Owner}/{_config.Repo}/issues?{string.Join("&", queryParams)}";
        
        try
        {
            var response = await _client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                Log.Warning("GitHub: Failed to list issues: {Error}", error);
                return new List<UnifiedTask>();
            }
            
            var issues = await response.Content.ReadFromJsonAsync<List<GitHubIssue>>(_jsonOptions, cancellationToken)
                ?? new List<GitHubIssue>();
            
            // Filter out pull requests (they also show up in issues endpoint)
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
            
            return issues.Select(MapToUnifiedTask).ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GitHub: Failed to list issues");
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
            
            var issue = await response.Content.ReadFromJsonAsync<GitHubIssue>(_jsonOptions, cancellationToken);
            return issue != null ? MapToUnifiedTask(issue) : null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GitHub: Failed to get issue {Id}", taskId);
            return null;
        }
    }

    public async Task<UnifiedTask> CreateTaskAsync(CreateTaskRequest request, CancellationToken cancellationToken = default)
    {
        var labels = request.Labels?.ToList() ?? new List<string>();
        labels.AddRange(_config.DefaultLabels);
        
        var body = new
        {
            title = request.Title,
            body = request.Description,
            labels = labels.Distinct().ToList(),
            assignees = request.Assignees,
            milestone = request.Bucket != null ? int.TryParse(request.Bucket, out var m) ? (int?)m : null : null
        };
        
        var url = $"repos/{_config.Owner}/{_config.Repo}/issues";
        var content = new StringContent(JsonSerializer.Serialize(body, _jsonOptions), Encoding.UTF8, "application/json");
        
        var response = await _client.PostAsync(url, content, cancellationToken);
        
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"GitHub: Failed to create issue: {error}");
        }
        
        var issue = await response.Content.ReadFromJsonAsync<GitHubIssue>(_jsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("GitHub: Empty response when creating issue");
        
        Log.Information("GitHub: Created issue #{Number}: {Title}", issue.Number, request.Title);
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
            body["labels"] = request.Labels;
        if (request.Assignees != null)
            body["assignees"] = request.Assignees;
        if (request.Bucket != null && int.TryParse(request.Bucket, out var milestone))
            body["milestone"] = milestone;
        
        var url = $"repos/{_config.Owner}/{_config.Repo}/issues/{taskId}";
        var content = new StringContent(JsonSerializer.Serialize(body, _jsonOptions), Encoding.UTF8, "application/json");
        
        var httpRequest = new HttpRequestMessage(new HttpMethod("PATCH"), url) { Content = content };
        var response = await _client.SendAsync(httpRequest, cancellationToken);
        
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"GitHub: Failed to update issue #{taskId}: {error}");
        }
        
        var issue = await response.Content.ReadFromJsonAsync<GitHubIssue>(_jsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("GitHub: Empty response when updating issue");
        
        return MapToUnifiedTask(issue);
    }

    public async Task<bool> DeleteTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        // GitHub doesn't support deleting issues, only closing them
        try
        {
            await UpdateTaskAsync(taskId, new UpdateTaskRequest { State = TaskState.Closed }, cancellationToken);
            return true;
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
            
            var milestones = await response.Content.ReadFromJsonAsync<List<GitHubMilestone>>(_jsonOptions, cancellationToken)
                ?? new List<GitHubMilestone>();
            
            return milestones.Select((m, i) => new TaskBucket
            {
                Id = m.Number.ToString(),
                Name = m.Title,
                Order = i
            }).ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GitHub: Failed to list milestones");
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
            
            var labels = await response.Content.ReadFromJsonAsync<List<GitHubLabel>>(_jsonOptions, cancellationToken)
                ?? new List<GitHubLabel>();
            
            return labels.Select(l => new TaskLabel
            {
                Name = l.Name,
                Color = $"#{l.Color}",
                Description = l.Description
            }).ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GitHub: Failed to list labels");
            return new List<TaskLabel>();
        }
    }

    private UnifiedTask MapToUnifiedTask(GitHubIssue issue)
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
            Priority = null // GitHub doesn't have priority
        };
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}

// GitHub API Models
internal class GitHubIssue
{
    public int Id { get; set; }
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string? Body { get; set; }
    public string State { get; set; } = "open";
    public List<GitHubUser>? Assignees { get; set; }
    public List<GitHubLabel>? Labels { get; set; }
    public GitHubMilestone? Milestone { get; set; }
    public GitHubPullRequest? PullRequest { get; set; }
    public string? HtmlUrl { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
}

internal class GitHubUser
{
    public string Login { get; set; } = "";
}

internal class GitHubLabel
{
    public string Name { get; set; } = "";
    public string? Color { get; set; }
    public string? Description { get; set; }
}

internal class GitHubMilestone
{
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public DateTime? DueOn { get; set; }
}

internal class GitHubPullRequest
{
    public string? Url { get; set; }
}
