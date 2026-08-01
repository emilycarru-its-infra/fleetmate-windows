using System.Text.Json.Serialization;

namespace FleetMate.Core.Models.Projects;

// Wire models for the Azure DevOps Git pull request endpoints. Everything is
// optional: Azure DevOps omits fields freely depending on the endpoint and the
// caller's permissions, and a strict model turns a partial payload into a total
// failure of the queue.

// IdentityRef is defined in WorkItem.cs and reused here — Azure DevOps returns
// the same identity shape for work item fields and pull request authors.

public sealed class GitPullRequestReviewer
{
    public string? Id { get; set; }
    public string? DisplayName { get; set; }
    public string? UniqueName { get; set; }

    /// <summary>Azure DevOps returns -10…10; see <see cref="PullRequestReviewVote"/>.</summary>
    public int? Vote { get; set; }

    public bool? IsRequired { get; set; }

    /// <summary>True for group reviewers, which are filtered out of the avatar row.</summary>
    public bool? IsContainer { get; set; }
}

public sealed class GitRepositoryRef
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public DevOpsProjectRef? Project { get; set; }
}

public sealed class DevOpsProjectRef
{
    public string? Id { get; set; }
    public string? Name { get; set; }
}

public sealed class GitCommitRef
{
    public string? CommitId { get; set; }
}

public sealed class GitPullRequest
{
    public int PullRequestId { get; set; }
    public string? Title { get; set; }
    public string? Status { get; set; }
    public bool? IsDraft { get; set; }
    public IdentityRef? CreatedBy { get; set; }
    public string? CreationDate { get; set; }
    public string? ClosedDate { get; set; }
    public string? SourceRefName { get; set; }
    public string? TargetRefName { get; set; }
    public string? MergeStatus { get; set; }
    public GitRepositoryRef? Repository { get; set; }
    public List<GitPullRequestReviewer>? Reviewers { get; set; }

    /// <summary>
    /// Azure DevOps requires this to be echoed back when completing a PR, and
    /// rejects the request if the source branch has moved since.
    /// </summary>
    public GitCommitRef? LastMergeSourceCommit { get; set; }

    public GitCommitRef? LastMergeCommit { get; set; }

    /// <summary>Azure DevOps reports conflicts through mergeStatus, not a boolean.</summary>
    [JsonIgnore]
    public bool HasConflicts =>
        string.Equals(MergeStatus, "conflicts", StringComparison.OrdinalIgnoreCase);
}

public sealed class GitPullRequestsResponse
{
    public List<GitPullRequest>? Value { get; set; }
    public int Count { get; set; }
}

public sealed class DevOpsConnectionData
{
    public IdentityRef? AuthorizedUser { get; set; }
    public IdentityRef? AuthenticatedUser { get; set; }
}

/// <summary>
/// The signed-in user's Azure DevOps identity. Pull request search criteria key
/// off the identity GUID rather than the UPN, so resolving this is what lets the
/// queue be a server-side query instead of a client-side scan.
/// </summary>
public sealed class DevOpsIdentitySummary
{
    public string? Id { get; init; }
    public string? DisplayName { get; init; }
    public string? Account { get; init; }

    public bool IsResolved => !string.IsNullOrWhiteSpace(Id);
}

/// <summary>Outcome of a Complete or Abandon action.</summary>
public sealed class PullRequestActionResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }

    public static PullRequestActionResult Ok() => new() { Success = true };
    public static PullRequestActionResult Failed(string error) => new() { Success = false, Error = error };
}
