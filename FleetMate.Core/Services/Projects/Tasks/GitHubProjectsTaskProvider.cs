using System.Text.Json;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Projects;
using Serilog;

namespace FleetMate.Core.Services.Projects.Tasks;

/// <summary>
/// Task provider backed by GitHub Projects v2.
/// Replaces the old GitHubTaskProvider (Issues-only REST) with full Projects v2 GraphQL support.
/// Maps project items (Issues, PRs, Draft Issues) to UnifiedTask using the project's Status field
/// for state mapping, and status columns as TaskBuckets.
/// </summary>
public class GitHubProjectsTaskProvider : ITaskProvider
{
    public string ProviderId => "github";
    public string ProviderName => "GitHub Projects";
    public bool IsEnabled => _config.Enabled;

    private readonly GitHubProviderConfig _config;
    private readonly GitHubProjectsService _service;
    private string? _projectId;
    private GitHubProjectField? _statusField;
    private List<GitHubProjectField>? _fields;

    public GitHubProjectsTaskProvider(GitHubProviderConfig config)
    {
        _config = config;
        _service = new GitHubProjectsService(config);
    }

    public async Task<bool> AuthenticateAsync(CancellationToken ct = default)
    {
        if (!await _service.AuthenticateAsync(ct))
            return false;

        // Resolve the project ID and cache the status field
        await ResolveProjectAsync(ct);
        return _projectId != null;
    }

    public async Task<List<UnifiedTask>> ListTasksAsync(TaskFilter? filter = null, CancellationToken ct = default)
    {
        await EnsureProjectAsync(ct);

        var limit = filter?.Limit ?? 100;
        var items = await _service.ListProjectItemsAsync(_projectId!, limit, ct: ct);

        var tasks = items.Select(ItemToUnifiedTask).Where(t => t != null).Select(t => t!).ToList();

        // Apply filters
        if (filter != null)
        {
            if (filter.States?.Any() == true)
                tasks = tasks.Where(t => filter.States.Contains(t.State)).ToList();

            if (!filter.IncludeClosed)
                tasks = tasks.Where(t => t.State != TaskState.Closed).ToList();

            if (filter.Assignees?.Any() == true)
                tasks = tasks.Where(t => t.Assignees.Any(a => 
                    filter.Assignees.Contains(a, StringComparer.OrdinalIgnoreCase))).ToList();

            if (filter.Labels?.Any() == true)
                tasks = tasks.Where(t => t.Labels.Any(l => 
                    filter.Labels.Contains(l, StringComparer.OrdinalIgnoreCase))).ToList();

            if (!string.IsNullOrEmpty(filter.Bucket))
                tasks = tasks.Where(t => string.Equals(t.Bucket, filter.Bucket, StringComparison.OrdinalIgnoreCase)).ToList();

            if (!string.IsNullOrEmpty(filter.SearchText))
                tasks = tasks.Where(t =>
                    (t.Title?.Contains(filter.SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (t.Description?.Contains(filter.SearchText, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        }

        return tasks;
    }

    public async Task<UnifiedTask?> GetTaskAsync(string taskId, CancellationToken ct = default)
    {
        // taskId is the project item node ID
        var items = await ListTasksAsync(new TaskFilter { Limit = 200 }, ct);
        return items.FirstOrDefault(t => t.Id == taskId);
    }

    public async Task<UnifiedTask> CreateTaskAsync(CreateTaskRequest request, CancellationToken ct = default)
    {
        await EnsureProjectAsync(ct);

        // Create as draft issue in the project
        var itemId = await _service.AddDraftItemAsync(_projectId!, request.Title, request.Description, ct);

        // If a bucket (status) was specified, move to that status
        if (!string.IsNullOrEmpty(request.Bucket) && _statusField != null)
        {
            var option = _statusField.Options.FirstOrDefault(o =>
                o.Name.Equals(request.Bucket, StringComparison.OrdinalIgnoreCase));
            if (option != null)
                await _service.MoveItemToStatusAsync(_projectId!, itemId, _statusField.Id, option.Id, ct);
        }

        return new UnifiedTask
        {
            Id = itemId,
            Provider = ProviderId,
            Title = request.Title,
            Description = request.Description,
            State = MapBucketToState(request.Bucket),
            Bucket = request.Bucket,
            Labels = request.Labels ?? new(),
            Assignees = request.Assignees ?? new(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Priority = request.Priority
        };
    }

    public async Task<UnifiedTask> UpdateTaskAsync(string taskId, UpdateTaskRequest request, CancellationToken ct = default)
    {
        await EnsureProjectAsync(ct);

        // Move to new status if bucket changed
        if (!string.IsNullOrEmpty(request.Bucket) && _statusField != null)
        {
            var option = _statusField.Options.FirstOrDefault(o =>
                o.Name.Equals(request.Bucket, StringComparison.OrdinalIgnoreCase));
            if (option != null)
                await _service.MoveItemToStatusAsync(_projectId!, taskId, _statusField.Id, option.Id, ct);
        }

        // Update other fields as needed
        if (_fields != null)
        {
            if (request.Title != null)
            {
                var titleField = _fields.FirstOrDefault(f => f.Name == "Title" && f.DataType == "TEXT");
                if (titleField != null)
                    await _service.UpdateItemFieldValueAsync(_projectId!, taskId, titleField.Id,
                        new Dictionary<string, string> { ["text"] = request.Title }, ct);
            }
        }

        // Fetch and return updated task
        return await GetTaskAsync(taskId, ct) ?? new UnifiedTask
        {
            Id = taskId,
            Provider = ProviderId,
            Title = request.Title ?? "",
            State = request.State ?? TaskState.Open,
            UpdatedAt = DateTime.UtcNow
        };
    }

    public async Task<bool> DeleteTaskAsync(string taskId, CancellationToken ct = default)
    {
        await EnsureProjectAsync(ct);
        try
        {
            await _service.DeleteItemAsync(_projectId!, taskId, ct);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to delete project item {ItemId}", taskId);
            return false;
        }
    }

    public async Task<List<TaskBucket>> ListBucketsAsync(CancellationToken ct = default)
    {
        await EnsureProjectAsync(ct);

        if (_statusField == null) return new List<TaskBucket>();

        return _statusField.Options.Select((o, i) => new TaskBucket
        {
            Id = o.Id,
            Name = o.Name,
            Order = i
        }).ToList();
    }

    public async Task<List<TaskLabel>> ListLabelsAsync(CancellationToken ct = default)
    {
        // Project items that are Issues/PRs inherit their labels from the underlying content.
        // Collect distinct labels across all items.
        await EnsureProjectAsync(ct);
        var items = await _service.ListProjectItemsAsync(_projectId!, 200, ct: ct);

        var labels = items
            .Where(i => i.Content != null)
            .SelectMany(i => i.Content!.Labels)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(l => new TaskLabel { Name = l })
            .ToList();

        return labels;
    }

    // ──────────────────────────── Project Resolution ────────────────────────────

    private async Task EnsureProjectAsync(CancellationToken ct)
    {
        if (_projectId != null) return;
        await ResolveProjectAsync(ct);
        if (_projectId == null)
            throw new InvalidOperationException("No GitHub project configured or found");
    }

    private async Task ResolveProjectAsync(CancellationToken ct)
    {
        var scope = ParseScope(_config.ProjectScope);
        var owner = _config.Organization ?? _config.Owner ?? "";

        if (_config.ProjectNumber.HasValue)
        {
            // Fetch specific project by number
            var project = await _service.GetProjectAsync(scope, owner, _config.ProjectNumber.Value, _config.Repo, ct);
            if (project != null)
            {
                _projectId = project.Id;
                Log.Information("GitHub Projects: Resolved project #{Number} '{Title}'", project.Number, project.Title);
            }
        }
        else
        {
            // Use first non-closed project
            var projects = await _service.ListProjectsAsync(scope, owner, _config.Repo, limit: 1, ct: ct);
            if (projects.Any())
            {
                var project = projects.First();
                _projectId = project.Id;
                Log.Information("GitHub Projects: Using first project #{Number} '{Title}'", project.Number, project.Title);
            }
        }

        // Cache fields
        if (_projectId != null)
        {
            _fields = await _service.ListProjectFieldsAsync(_projectId, ct);
            _statusField = _fields.FirstOrDefault(f =>
                f.Name.Equals("Status", StringComparison.OrdinalIgnoreCase) &&
                f.DataType == "SINGLE_SELECT");
        }
    }

    // ──────────────────────────── Mapping ────────────────────────────

    private UnifiedTask? ItemToUnifiedTask(GitHubProjectItem item)
    {
        string title;
        string? description = null;
        string? externalUrl = null;
        List<string> assignees = new();
        List<string> labels = new();
        DateTime createdAt = item.CreatedAt;
        DateTime updatedAt = item.UpdatedAt;
        DateTime? closedAt = null;

        if (item.Content != null)
        {
            title = item.Content.Title;
            description = item.Content.Body;
            externalUrl = item.Content.Url;
            assignees = item.Content.Assignees;
            labels = item.Content.Labels;
            createdAt = item.Content.CreatedAt;
            updatedAt = item.Content.UpdatedAt;
            closedAt = item.Content.ClosedAt;
        }
        else if (item.DraftContent != null)
        {
            title = item.DraftContent.Title;
            description = item.DraftContent.Body;
        }
        else if (item.Type == "REDACTED")
        {
            return null; // Skip items we can't see
        }
        else
        {
            return null;
        }

        // Get status from field values
        var statusValue = item.FieldValues
            .FirstOrDefault(fv => fv.FieldName.Equals("Status", StringComparison.OrdinalIgnoreCase));

        var bucket = statusValue?.SingleSelectValue;
        var state = MapStatusToState(statusValue?.SingleSelectValue, item.Content?.State);

        // Get priority from field values (if a Priority or P field exists)
        int? priority = null;
        var priorityFv = item.FieldValues
            .FirstOrDefault(fv => fv.FieldName.Equals("Priority", StringComparison.OrdinalIgnoreCase) ||
                                   fv.FieldName.Equals("P", StringComparison.OrdinalIgnoreCase));
        if (priorityFv?.SingleSelectValue != null)
        {
            // Map common priority names: P0/Urgent=1, P1/High=2, P2/Medium=3, P3/Low=4
            priority = priorityFv.SingleSelectValue.ToLowerInvariant() switch
            {
                "p0" or "urgent" or "critical" => 1,
                "p1" or "high" => 2,
                "p2" or "medium" or "normal" => 3,
                "p3" or "low" => 4,
                _ => null
            };
        }
        else if (priorityFv?.NumberValue.HasValue == true)
        {
            priority = (int)priorityFv.NumberValue.Value;
        }

        // Store provider data for round-tripping
        var providerData = JsonSerializer.SerializeToElement(new
        {
            projectItemId = item.Id,
            projectId = _projectId,
            type = item.Type,
            isArchived = item.IsArchived,
            contentId = item.Content?.Id,
            repository = item.Content?.Repository,
            isPullRequest = item.Content?.IsPullRequest ?? false
        });

        return new UnifiedTask
        {
            Id = item.Id,
            Provider = ProviderId,
            Title = title,
            Description = description,
            State = state,
            Assignees = assignees,
            Labels = labels,
            Bucket = bucket,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            ClosedAt = closedAt,
            ExternalUrl = externalUrl,
            Priority = priority,
            ProviderData = providerData
        };
    }

    private static TaskState MapStatusToState(string? statusName, string? contentState)
    {
        if (statusName != null)
        {
            var lower = statusName.ToLowerInvariant();
            if (lower.Contains("done") || lower.Contains("closed") || lower.Contains("complete") || lower.Contains("merged"))
                return TaskState.Closed;
            if (lower.Contains("progress") || lower.Contains("active") || lower.Contains("review") || lower.Contains("doing"))
                return TaskState.InProgress;
            // "Todo", "Backlog", "New", "Triage", etc. -> Open
            return TaskState.Open;
        }

        // Fallback to content state
        return contentState?.ToUpperInvariant() switch
        {
            "CLOSED" or "MERGED" => TaskState.Closed,
            _ => TaskState.Open
        };
    }

    private TaskState MapBucketToState(string? bucket)
    {
        if (bucket == null) return TaskState.Open;
        return MapStatusToState(bucket, null);
    }

    private static ProjectScope ParseScope(string scope)
    {
        return scope.ToLowerInvariant() switch
        {
            "user" => ProjectScope.User,
            "repository" or "repo" => ProjectScope.Repository,
            _ => ProjectScope.Organization
        };
    }
}
