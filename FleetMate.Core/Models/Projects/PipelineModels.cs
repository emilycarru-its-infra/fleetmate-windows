using FleetMate.Core.Shared;

namespace FleetMate.Core.Models.Projects;

// MARK: - Recent commits per repository (Development › Commits)

/// <summary>
/// A repository with its most recent default-branch commits, from either
/// provider, flattened into one shape for the Commits list.
/// </summary>
public sealed class RepositoryCommits
{
    public PullRequestSource Source { get; init; }

    /// <summary>Azure DevOps: project name. GitHub: owner login.</summary>
    public string Container { get; init; } = string.Empty;

    public string Repository { get; init; } = string.Empty;

    /// <summary>Id the provider wants back on detail calls (Azure DevOps GUID; GitHub uses owner/name).</summary>
    public string? RepositoryId { get; init; }

    public string WebUrl { get; init; } = string.Empty;
    public string? DefaultBranch { get; init; }

    /// <summary>Newest first.</summary>
    public List<PullRequestCommit> Commits { get; init; } = new();

    public string Id => $"{Source}:{Container}/{Repository}";
    public string DisplayName => $"{Container}/{Repository}";
    public DateTime LatestDate => Commits.FirstOrDefault()?.Date ?? DateTime.MinValue;
}

/// <summary>
/// One commit opened in the viewer: full message, and either per-file diffs
/// (GitHub) or a bare change list (Azure DevOps, whose commit API returns paths
/// and change types but no patch).
/// </summary>
public sealed class CommitDetail
{
    public string Message { get; init; } = string.Empty;
    public List<DiffFile> Files { get; init; } = new();
    public List<CommitChange> Changes { get; init; } = new();
    public int Additions { get; init; }
    public int Deletions { get; init; }
    public bool Truncated { get; init; }
}

public sealed class CommitChange
{
    public string Path { get; init; } = string.Empty;

    /// <summary>add, edit, delete, rename — the provider's own word, lowercased.</summary>
    public string ChangeType { get; init; } = string.Empty;
}

// MARK: - Pipeline runs (Development › Pipelines)

/// <summary>Lifecycle of a run, normalized across Azure Pipelines and GitHub Actions.</summary>
public enum PipelineRunStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Partial,
    Skipped,
    Unknown,
}

public static class PipelineRunStatusExtensions
{
    public static string DisplayName(this PipelineRunStatus status) => status switch
    {
        PipelineRunStatus.Partial => "Partially succeeded",
        _ => status.ToString(),
    };

    public static bool IsActive(this PipelineRunStatus status) =>
        status is PipelineRunStatus.Queued or PipelineRunStatus.Running;

    /// <summary>GitHub Actions <c>status</c> + <c>conclusion</c>.</summary>
    public static PipelineRunStatus FromGitHub(string? status, string? conclusion)
    {
        switch (status?.ToLowerInvariant())
        {
            case "queued" or "waiting" or "requested" or "pending": return PipelineRunStatus.Queued;
            case "in_progress": return PipelineRunStatus.Running;
            case "completed": break;
            default: return PipelineRunStatus.Unknown;
        }

        return conclusion?.ToLowerInvariant() switch
        {
            "success" or "neutral" => PipelineRunStatus.Succeeded,
            "failure" or "timed_out" or "startup_failure" => PipelineRunStatus.Failed,
            "cancelled" => PipelineRunStatus.Cancelled,
            "skipped" => PipelineRunStatus.Skipped,
            "action_required" => PipelineRunStatus.Queued,
            _ => PipelineRunStatus.Unknown,
        };
    }

    /// <summary>Azure Pipelines build <c>status</c> + <c>result</c>; timeline records (state/result) fit too.</summary>
    public static PipelineRunStatus FromAzureDevOps(string? status, string? result)
    {
        switch (status?.ToLowerInvariant())
        {
            case "notstarted" or "postponed" or "pending": return PipelineRunStatus.Queued;
            case "inprogress" or "cancelling": return PipelineRunStatus.Running;
            case "completed": break;
            default: return PipelineRunStatus.Unknown;
        }

        return result?.ToLowerInvariant() switch
        {
            "succeeded" => PipelineRunStatus.Succeeded,
            "partiallysucceeded" or "succeededwithissues" => PipelineRunStatus.Partial,
            "failed" => PipelineRunStatus.Failed,
            "canceled" or "cancelled" or "abandoned" => PipelineRunStatus.Cancelled,
            "skipped" => PipelineRunStatus.Skipped,
            _ => PipelineRunStatus.Unknown,
        };
    }
}

/// <summary>
/// One pipeline run from either provider. There is no cross-project run list
/// in Azure DevOps and no cross-repository one in GitHub; this is it.
/// </summary>
public sealed class PipelineRun
{
    public PullRequestSource Source { get; init; }

    /// <summary>Azure DevOps: project name. GitHub: owner login.</summary>
    public string Container { get; init; } = string.Empty;

    public string? Repository { get; init; }

    /// <summary>Azure DevOps: build definition name. GitHub: workflow name.</summary>
    public string PipelineName { get; init; } = string.Empty;

    /// <summary>Azure DevOps: definition id. GitHub: workflow id. Needed to queue a rerun.</summary>
    public int? PipelineId { get; init; }

    public long RunId { get; init; }

    /// <summary>Azure DevOps buildNumber (e.g. "20260922.3"), GitHub run_number as text.</summary>
    public string RunNumber { get; init; } = string.Empty;

    public PipelineRunStatus Status { get; init; }
    public string? Branch { get; init; }
    public string? CommitSha { get; init; }
    public string? TriggeredBy { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public string WebUrl { get; init; } = string.Empty;

    public string Id => $"{Source}:{Container}/{PipelineName}#{RunId}";

    /// <summary>Wall-clock length once finished, or so far while running.</summary>
    public TimeSpan? Duration => StartedAt is { } start ? (FinishedAt ?? DateTime.UtcNow) - start : null;

    public DateTime SortDate => StartedAt ?? FinishedAt ?? DateTime.MinValue;
}

/// <summary>
/// The log of one run, split into the provider's natural sections: jobs on
/// GitHub, task records on Azure DevOps.
/// </summary>
public sealed class PipelineRunLog
{
    /// <summary>Longest tail kept per section.</summary>
    public const int SectionCap = 400 * 1024;

    public sealed class Section
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public PipelineRunStatus Status { get; init; }
        public string Text { get; init; } = string.Empty;
    }

    public long RunId { get; init; }
    public List<Section> Sections { get; init; } = new();

    /// <summary>True when a section's text was cut to keep the view responsive.</summary>
    public bool Truncated { get; init; }

    /// <summary>Keep the tail — the end of a log is where a failure is.</summary>
    public static (string Text, bool Truncated) Tail(string text) =>
        text.Length <= SectionCap ? (text, false) : ("…\n" + text[^SectionCap..], true);

    public string Text => string.Join("\n\n", Sections.Select(s => $"── {s.Name} ──\n{s.Text}"));
}

// MARK: - Approvals

/// <summary>
/// A pending approval that holds an Azure Pipelines run: a ManualValidation
/// task or an environment approval check. Both use the same Approvals API.
/// </summary>
public sealed record PipelineApproval
{
    /// <summary>The approval id; also the <c>identifier</c> of the run's timeline record.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Azure DevOps project name.</summary>
    public string Project { get; init; } = string.Empty;

    /// <summary>
    /// The build the approval holds, when the API says so. ManualValidation
    /// approvals leave it out, so it is matched through the build's timeline.
    /// </summary>
    public long? BuildId { get; init; }

    public string Instructions { get; init; } = string.Empty;
    public DateTime? CreatedOn { get; init; }

    /// <summary>Display names of the people or groups asked to approve.</summary>
    public IReadOnlyList<string> Approvers { get; init; } = Array.Empty<string>();

    public PipelineApproval AssignedTo(long buildId) => this with { BuildId = buildId };
}
