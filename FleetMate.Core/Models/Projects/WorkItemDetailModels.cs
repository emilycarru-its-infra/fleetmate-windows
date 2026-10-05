namespace FleetMate.Core.Models.Projects;

/// <summary>
/// Everything the work item sidebar shows: the fields the macOS sidebar
/// reads, plus relations (parent, children, links and code artifacts).
/// </summary>
public sealed class WorkItemDetail
{
    public int Id { get; init; }
    public string Title { get; init; } = "";
    public string State { get; init; } = "";
    public string? Reason { get; init; }
    public string Type { get; init; } = "";
    public string Project { get; init; } = "";
    public string? AssignedTo { get; init; }
    public string? AssignedToUniqueName { get; init; }
    public string? AreaPath { get; init; }
    public string? IterationPath { get; init; }
    public string? Tags { get; init; }
    public int? Priority { get; init; }
    public DateTime? DueDate { get; init; }
    public string? Description { get; init; }
    public string? ReproSteps { get; init; }
    public string? AcceptanceCriteria { get; init; }
    public double? OriginalEstimate { get; init; }
    public double? RemainingWork { get; init; }
    public double? CompletedWork { get; init; }
    public string? BoardColumn { get; init; }

    public DateTime? Created { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? Changed { get; init; }
    public string? ChangedBy { get; init; }
    public DateTime? StateChanged { get; init; }
    public DateTime? Resolved { get; init; }
    public string? ResolvedBy { get; init; }
    public DateTime? Closed { get; init; }
    public string? ClosedBy { get; init; }

    public string WebUrl { get; init; } = "";
    public List<WorkItemRelationInfo> Relations { get; init; } = new();

    public int? ParentId => Relations.FirstOrDefault(r => r.Kind == WorkItemRelationKind.Parent)?.LinkedWorkItemId;
    public bool HasEffort => OriginalEstimate != null || RemainingWork != null || CompletedWork != null;

    public List<string> TagList => string.IsNullOrWhiteSpace(Tags)
        ? new()
        : Tags.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

public enum WorkItemRelationKind { Parent, Child, Related, Predecessor, Successor, Artifact, Other }

/// <summary>One relation. <see cref="Name"/> is the link type ("Fixed in Commit"), not the artifact's name.</summary>
public sealed record WorkItemRelationInfo(WorkItemRelationKind Kind, string Rel, string Url, string? Name, string? Comment)
{
    /// <summary>The other work item's id, read from the end of its URL.</summary>
    public int? LinkedWorkItemId =>
        Kind is WorkItemRelationKind.Artifact ? null
        : int.TryParse(Url.TrimEnd('/').Split('/').LastOrDefault(), out var id) ? id : null;
}

/// <summary>A discussion comment with its reactions. Text is the HTML Azure DevOps stores.</summary>
public sealed class WorkItemDiscussionComment
{
    public int Id { get; init; }
    public string Text { get; set; } = "";
    public string? Author { get; init; }
    public string? AuthorUniqueName { get; init; }
    public string? AuthorId { get; init; }
    public DateTime? Created { get; init; }
    public List<CommentReaction> Reactions { get; set; } = new();
}

public sealed record CommentReaction(string Type, int Count, bool Engaged);

/// <summary>A branch, commit or pull request offered by Link to Code.</summary>
public sealed record CodeLinkCandidate(string Kind, string Key, string Title, string? Detail);

/// <summary>
/// The sidebar's edit form. Only fields that differ from the loaded item are
/// sent, so an untouched field never overwrites someone else's change.
/// </summary>
public sealed class WorkItemEdit
{
    public string? Title { get; set; }
    public string? State { get; set; }
    public string? Type { get; set; }
    public string? AssignedToUniqueName { get; set; }
    public int? Priority { get; set; }
    public string? AreaPath { get; set; }
    public string? IterationPath { get; set; }
    public string? Tags { get; set; }
    public DateTime? DueDate { get; set; }
    public string? Description { get; set; }
    public string? ReproSteps { get; set; }
    public string? AcceptanceCriteria { get; set; }
    public double? OriginalEstimate { get; set; }
    public double? RemainingWork { get; set; }
    public double? CompletedWork { get; set; }
}
