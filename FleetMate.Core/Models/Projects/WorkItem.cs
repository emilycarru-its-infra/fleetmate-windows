using System.Text.Json.Serialization;

namespace FleetMate.Core.Models.Projects;

/// <summary>
/// Azure DevOps work item
/// </summary>
public class WorkItem
{
    public int Id { get; set; }
    public int Rev { get; set; }
    public WorkItemFields Fields { get; set; } = new();
    public string Url { get; set; } = string.Empty;

    // Convenience properties
    [JsonIgnore]
    public string Title => Fields.Title ?? string.Empty;
    [JsonIgnore]
    public string State => Fields.State ?? string.Empty;
    [JsonIgnore]
    public string WorkItemType => Fields.WorkItemType ?? string.Empty;
    [JsonIgnore]
    public string? AssignedTo => Fields.AssignedTo?.DisplayName;
    [JsonIgnore]
    public string? IterationPath => Fields.IterationPath;
    [JsonIgnore]
    public string? AreaPath => Fields.AreaPath;
    [JsonIgnore]
    public int? Priority => Fields.Priority;
    [JsonIgnore]
    public string? Description => Fields.Description;
    [JsonIgnore]
    public DateTime? CreatedDate => Fields.CreatedDate;
    [JsonIgnore]
    public DateTime? ChangedDate => Fields.ChangedDate;
    [JsonIgnore]
    public List<string> Tags => string.IsNullOrEmpty(Fields.Tags)
        ? new List<string>()
        : Fields.Tags.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList();
}

/// <summary>
/// Work item fields from Azure DevOps API
/// </summary>
public class WorkItemFields
{
    [JsonPropertyName("System.Title")]
    public string? Title { get; set; }

    [JsonPropertyName("System.State")]
    public string? State { get; set; }

    [JsonPropertyName("System.WorkItemType")]
    public string? WorkItemType { get; set; }

    [JsonPropertyName("System.AssignedTo")]
    public IdentityRef? AssignedTo { get; set; }

    [JsonPropertyName("System.IterationPath")]
    public string? IterationPath { get; set; }

    [JsonPropertyName("System.AreaPath")]
    public string? AreaPath { get; set; }

    [JsonPropertyName("Microsoft.VSTS.Common.Priority")]
    public int? Priority { get; set; }

    [JsonPropertyName("System.Description")]
    public string? Description { get; set; }

    [JsonPropertyName("System.CreatedDate")]
    public DateTime? CreatedDate { get; set; }

    [JsonPropertyName("System.ChangedDate")]
    public DateTime? ChangedDate { get; set; }

    [JsonPropertyName("System.Tags")]
    public string? Tags { get; set; }

    [JsonPropertyName("System.Reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("System.CreatedBy")]
    public IdentityRef? CreatedBy { get; set; }

    [JsonPropertyName("System.ChangedBy")]
    public IdentityRef? ChangedBy { get; set; }
}

/// <summary>
/// Identity reference (user) in Azure DevOps
/// </summary>
public class IdentityRef
{
    public string DisplayName { get; set; } = string.Empty;
    public string UniqueName { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
}

/// <summary>
/// Response from work item query
/// </summary>
public class WorkItemQueryResult
{
    public string QueryType { get; set; } = string.Empty;
    public List<WorkItemReference> WorkItems { get; set; } = new();
    public List<ColumnReference>? Columns { get; set; }
}

/// <summary>
/// Work item reference (ID only)
/// </summary>
public class WorkItemReference
{
    public int Id { get; set; }
    public string Url { get; set; } = string.Empty;
}

/// <summary>
/// Column reference in query result
/// </summary>
public class ColumnReference
{
    public string ReferenceName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

/// <summary>
/// Response from work items batch endpoint
/// </summary>
public class WorkItemBatchResponse
{
    public int Count { get; set; }
    public List<WorkItem> Value { get; set; } = new();
}

/// <summary>
/// Request to create a work item
/// </summary>
public class CreateWorkItemRequest
{
    public string Title { get; set; } = string.Empty;
    public string Type { get; set; } = "Bug";
    public string? Description { get; set; }
    public string? AssignedTo { get; set; }
    public int? Priority { get; set; }
    public string? IterationPath { get; set; }
    public string? AreaPath { get; set; }
    public List<string>? Tags { get; set; }
}

/// <summary>
/// Request to update a work item
/// </summary>
public class UpdateWorkItemRequest
{
    public string? Title { get; set; }
    public string? State { get; set; }
    public string? AssignedTo { get; set; }
    public int? Priority { get; set; }
    public string? IterationPath { get; set; }
    public string? Comment { get; set; }
}

/// <summary>
/// JSON Patch operation for work item updates
/// </summary>
public class JsonPatchOperation
{
    [JsonPropertyName("op")]
    public string Op { get; set; } = "add";

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public object? Value { get; set; }
}
