using FleetMate.Core.Config;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Devices;
using FleetMate.Core.Services.Inventory;
using FleetMate.Core.Services.Tickets;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Services.Reporting;
using Serilog;

namespace FleetMate.Core.Services.Projects.Tasks;

/// <summary>
/// Azure DevOps task provider that wraps AzureDevOpsService.
/// Implements ITaskProvider for unified task management.
/// </summary>
public class AzureDevOpsTaskProvider : ITaskProvider, IDisposable
{
    private readonly AzureDevOpsService _service;
    private readonly AzureDevOpsProviderConfig? _providerConfig;
    private readonly AzureDevOpsConfig _devOpsConfig;
    private bool _isAuthenticated;

    public string ProviderId => "azdevops";
    public string ProviderName => "Azure DevOps";
    
    public bool IsEnabled => _providerConfig?.Enabled ?? _devOpsConfig.Organization != null;

    public AzureDevOpsTaskProvider(FleetMateConfig config)
    {
        _providerConfig = config.Tasks?.Providers?.AzDevOps;
        
        // Build effective config - provider config overrides main config
        _devOpsConfig = new AzureDevOpsConfig
        {
            Organization = _providerConfig?.Organization ?? config.AzureDevOps?.Organization,
            Project = _providerConfig?.Project ?? config.AzureDevOps?.Project,
            DefaultWorkItemType = _providerConfig?.DefaultWorkItemType ?? config.AzureDevOps?.DefaultWorkItemType ?? "Bug",
            DefaultIterationPath = _providerConfig?.IterationPath ?? config.AzureDevOps?.DefaultIterationPath,
            CacheMinutes = config.AzureDevOps?.CacheMinutes ?? 30
        };
        
        _service = new AzureDevOpsService(_devOpsConfig);
    }

    public async Task<bool> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        // Try a simple query to verify auth works
        try
        {
            var sprints = await _service.GetSprintsAsync();
            _isAuthenticated = true;
            Log.Information("Authenticated with Azure DevOps: {Org}/{Project}", 
                _devOpsConfig.Organization, _devOpsConfig.Project);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to authenticate with Azure DevOps");
            _isAuthenticated = false;
            return false;
        }
    }

    /// <summary>Escape a value for safe WIQL string interpolation</summary>
    private static string EscapeWiql(string value) => value.Replace("'", "''");

    public async Task<List<UnifiedTask>> ListTasksAsync(TaskFilter? filter = null, CancellationToken cancellationToken = default)
    {
        // Org-level query — no [System.TeamProject] = @project filter
        var conditions = new List<string>();
        
        // Build filter conditions
        if (filter != null)
        {
            if (filter.States?.Count > 0)
            {
                var stateConditions = filter.States.Select(s => $"[System.State] = '{EscapeWiql(MapStateToAdo(s))}'");
                conditions.Add($"({string.Join(" OR ", stateConditions)})");
            }
            else if (!filter.IncludeClosed)
            {
                conditions.Add("[System.State] <> 'Closed'");
                conditions.Add("[System.State] <> 'Done'");
                conditions.Add("[System.State] <> 'Removed'");
            }
            
            if (filter.Assignees?.Count > 0)
            {
                var assigneeConditions = filter.Assignees.Select(a => $"[System.AssignedTo] = '{EscapeWiql(a)}'");
                conditions.Add($"({string.Join(" OR ", assigneeConditions)})");
            }
            
            if (filter.Labels?.Count > 0)
            {
                foreach (var label in filter.Labels)
                {
                    conditions.Add($"[System.Tags] CONTAINS '{EscapeWiql(label)}'");
                }
            }
            
            if (!string.IsNullOrEmpty(filter.Bucket))
            {
                conditions.Add($"[System.IterationPath] UNDER '{EscapeWiql(filter.Bucket)}'");
            }
            
            if (!string.IsNullOrEmpty(filter.SearchText))
            {
                conditions.Add($"[System.Title] CONTAINS '{EscapeWiql(filter.SearchText)}'");
            }
        }
        else
        {
            // Default: exclude closed
            conditions.Add("[System.State] <> 'Closed'");
            conditions.Add("[System.State] <> 'Done'");
            conditions.Add("[System.State] <> 'Removed'");
        }
        
        var whereClause = conditions.Count > 0
            ? $" WHERE {string.Join(" AND ", conditions)}"
            : "";
        var wiql = $"SELECT [System.Id] FROM WorkItems{whereClause} ORDER BY [System.ChangedDate] DESC";
        
        var workItems = await _service.QueryWorkItemsAsync(wiql, orgLevel: true);
        
        if (filter?.Limit > 0)
        {
            workItems = workItems.Take(filter.Limit.Value).ToList();
        }
        
        return workItems.Select(MapToUnifiedTask).ToList();
    }

    public async Task<UnifiedTask?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(taskId, out var id))
        {
            Log.Warning("Invalid Azure DevOps work item ID: {Id}", taskId);
            return null;
        }
        
        var workItem = await _service.GetWorkItemAsync(id);
        return workItem != null ? MapToUnifiedTask(workItem) : null;
    }

    public async Task<UnifiedTask> CreateTaskAsync(CreateTaskRequest request, CancellationToken cancellationToken = default)
    {
        var adoRequest = new CreateWorkItemRequest
        {
            Title = request.Title,
            Type = _devOpsConfig.DefaultWorkItemType,
            Description = request.Description,
            AssignedTo = request.Assignees?.FirstOrDefault(),
            Priority = request.Priority,
            IterationPath = request.Bucket ?? _devOpsConfig.DefaultIterationPath,
            AreaPath = _providerConfig?.AreaPath,
            Tags = request.Labels
        };
        
        var workItem = await _service.CreateWorkItemAsync(adoRequest)
            ?? throw new InvalidOperationException("Failed to create work item in Azure DevOps");
        
        return MapToUnifiedTask(workItem);
    }

    public async Task<UnifiedTask> UpdateTaskAsync(string taskId, UpdateTaskRequest request, CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(taskId, out var id))
        {
            throw new ArgumentException($"Invalid Azure DevOps work item ID: {taskId}");
        }
        
        var adoRequest = new UpdateWorkItemRequest
        {
            Title = request.Title,
            State = request.State.HasValue ? MapStateToAdo(request.State.Value) : null,
            AssignedTo = request.Assignees?.FirstOrDefault(),
            Priority = request.Priority,
            IterationPath = request.Bucket
        };
        
        var workItem = await _service.UpdateWorkItemAsync(id, adoRequest)
            ?? throw new InvalidOperationException($"Failed to update work item {taskId} in Azure DevOps");
        
        return MapToUnifiedTask(workItem);
    }

    public async Task<bool> DeleteTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        // Azure DevOps doesn't typically support direct deletion via API
        // Instead, we set the state to "Removed"
        if (!int.TryParse(taskId, out var id))
        {
            return false;
        }
        
        var result = await _service.UpdateWorkItemAsync(id, new UpdateWorkItemRequest
        {
            State = "Removed"
        });
        
        return result != null;
    }

    public async Task<List<TaskBucket>> ListBucketsAsync(CancellationToken cancellationToken = default)
    {
        var sprints = await _service.GetSprintsAsync();
        
        return sprints.Select((s, i) => new TaskBucket
        {
            Id = s.Path ?? s.Name,
            Name = s.Name,
            Order = i
        }).ToList();
    }

    public async Task<List<TaskLabel>> ListLabelsAsync(CancellationToken cancellationToken = default)
    {
        // Azure DevOps doesn't have a dedicated labels API
        // Tags are free-form text on work items
        // We could query work items and extract unique tags, but that's expensive
        // Return empty for now - tags can still be applied
        await Task.CompletedTask;
        return new List<TaskLabel>();
    }

    // MARK: - Mapping Helpers

    private UnifiedTask MapToUnifiedTask(WorkItem workItem)
    {
        var fields = workItem.Fields;
        
        return new UnifiedTask
        {
            Id = workItem.Id.ToString(),
            Provider = ProviderId,
            Title = fields?.Title ?? "",
            Description = fields?.Description,
            State = MapStateFromAdo(fields?.State ?? "New"),
            Assignees = fields?.AssignedTo?.DisplayName != null 
                ? new List<string> { fields.AssignedTo.DisplayName }
                : new List<string>(),
            Labels = ParseTags(fields?.Tags),
            Bucket = fields?.IterationPath,
            DueDate = null, // ADO uses target date differently
            CreatedAt = fields?.CreatedDate ?? DateTime.MinValue,
            UpdatedAt = fields?.ChangedDate ?? DateTime.MinValue,
            ClosedAt = null, // WorkItemFields doesn't have ClosedDate property
            ExternalUrl = workItem.Url?.Replace("_apis/wit/workItems", "_workitems/edit"),
            Priority = fields?.Priority
        };
    }

    private static TaskState MapStateFromAdo(string adoState)
    {
        return adoState.ToLowerInvariant() switch
        {
            "new" or "to do" or "proposed" => TaskState.Open,
            "active" or "in progress" or "doing" or "committed" => TaskState.InProgress,
            "closed" or "done" or "resolved" or "completed" or "removed" => TaskState.Closed,
            _ => TaskState.Open
        };
    }

    private static string MapStateToAdo(TaskState state)
    {
        return state switch
        {
            TaskState.Open => "New",
            TaskState.InProgress => "Active",
            TaskState.Closed => "Closed",
            _ => "New"
        };
    }

    private static List<string> ParseTags(string? tags)
    {
        if (string.IsNullOrEmpty(tags))
            return new List<string>();
        
        return tags.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();
    }

    public void Dispose()
    {
        _service.Dispose();
    }
}
