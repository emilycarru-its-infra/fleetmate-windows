using FleetMate.Core.Models.Projects;

namespace FleetMate.Core.Models.Projects;

// Stored-query models for the Azure DevOps Shared Queries feature: the
// Projects tab's List view renders every Shared Query with its results
// expanded, the way the Azure DevOps query grid does. Mirrors the macOS
// QueryModels so both ports speak the same shapes.

/// <summary>A saved query (or query folder) from the Azure DevOps Queries API.</summary>
public class AdoQuery
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Path { get; set; }
    public bool? IsFolder { get; set; }
    public bool? IsPublic { get; set; }
    public bool? HasChildren { get; set; }
    public string? QueryType { get; set; }
    public List<AdoQuery>? Children { get; set; }

    public bool IsLeafQuery => !(IsFolder ?? false);

    /// <summary>"tree", "oneHop" or "flat" — defaults to flat when the API omits it.</summary>
    public string ResolvedQueryType => QueryType ?? "flat";
}

/// <summary>
/// A leaf query flattened out of the Shared Queries folder tree. FolderPath is
/// the human path between "Shared Queries" and the query itself ("" for
/// queries sitting directly in Shared Queries).
/// </summary>
public class AdoSharedQuery
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string FolderPath { get; init; } = "";
    public string QueryType { get; init; } = "flat";
}

/// <summary>
/// One row of a stored query run, pre-order flattened for display. Depth is 0
/// for roots; tree queries indent children below their parent exactly as the
/// Azure DevOps results grid does.
/// </summary>
public class StoredQueryRow
{
    public WorkItem Item { get; init; } = new();
    public int Depth { get; init; }
    public bool HasChildren { get; init; }
}

/// <summary>The materialized result of running one stored query.</summary>
public class StoredQueryRun
{
    public string QueryId { get; init; } = "";
    public string QueryType { get; init; } = "flat";
    public List<StoredQueryRow> Rows { get; init; } = new();
    /// <summary>True when the run was capped and more rows exist server-side.</summary>
    public bool Truncated { get; init; }
}

public static class WorkItemUnifiedTaskExtensions
{
    /// <summary>
    /// The same mapping AzureDevOpsTaskProvider applies, exposed so views can
    /// promote raw query results into the unified task shape the detail
    /// sidebar, lightbox and context menus already understand.
    /// </summary>
    public static UnifiedTask AsUnifiedTask(this WorkItem workItem)
    {
        var fields = workItem.Fields;

        var state = (fields?.State ?? "New").ToLowerInvariant() switch
        {
            "new" or "to do" or "proposed" => TaskState.Open,
            "active" or "in progress" or "doing" or "committed" => TaskState.InProgress,
            "closed" or "done" or "resolved" or "completed" or "removed" => TaskState.Closed,
            _ => TaskState.Open
        };

        var labels = (fields?.Tags ?? "")
            .Split(';', ',')
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToList();

        return new UnifiedTask
        {
            Id = workItem.Id.ToString(),
            Provider = "azdevops",
            Title = fields?.Title ?? "",
            Description = fields?.Description,
            State = state,
            Assignees = fields?.AssignedTo?.DisplayName is { Length: > 0 } name
                ? new List<string> { name }
                : new List<string>(),
            Labels = labels,
            Bucket = fields?.IterationPath,
            CreatedAt = fields?.CreatedDate ?? DateTime.MinValue,
            UpdatedAt = fields?.ChangedDate ?? DateTime.MinValue,
            ExternalUrl = workItem.Url?.Replace("_apis/wit/workItems", "_workitems/edit"),
            Priority = fields?.Priority,
            Metadata = new Dictionary<string, string>
            {
                ["state"] = fields?.State ?? "New",
                ["areaPath"] = fields?.AreaPath ?? "",
                ["iterationPath"] = fields?.IterationPath ?? "",
                ["workItemType"] = fields?.WorkItemType ?? "",
            }
        };
    }
}

/// <summary>One discussion comment on a work item.</summary>
public class WorkItemComment
{
    public int Id { get; set; }
    public string? Text { get; set; }
    public IdentityRef? CreatedBy { get; set; }
    public DateTime? CreatedDate { get; set; }
}

public class WorkItemCommentsResponse
{
    public int TotalCount { get; set; }
    public List<WorkItemComment>? Comments { get; set; }
}
