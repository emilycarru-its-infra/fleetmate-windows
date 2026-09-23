namespace FleetMate.Core.Models.Projects;

/// <summary>How a GitHub pull request is merged. Azure DevOps ignores it and completes with the PR's own policy.</summary>
public enum PullRequestMergeMethod
{
    Merge,
    Squash,
    Rebase,
}

public static class PullRequestMergeMethodExtensions
{
    /// <summary>The <c>merge_method</c> value GitHub's REST merge endpoint expects.</summary>
    public static string GitHubValue(this PullRequestMergeMethod method) => method switch
    {
        PullRequestMergeMethod.Squash => "squash",
        PullRequestMergeMethod.Rebase => "rebase",
        _ => "merge",
    };
}

/// <summary>Outcome of one check, normalized across GitHub check runs, commit statuses and Azure DevOps policies.</summary>
public enum PullRequestCheckState
{
    Pending,
    Success,
    Failure,
    Neutral,
    Skipped,
}

/// <summary>
/// One row of the checks strip: a GitHub check run or commit status, or an
/// Azure DevOps policy evaluation.
/// </summary>
public sealed class PullRequestCheck
{
    public string Name { get; init; } = string.Empty;
    public PullRequestCheckState State { get; init; }
    public string? DetailsUrl { get; init; }

    /// <summary>Required by branch protection (GitHub) or blocking (Azure DevOps).</summary>
    public bool IsRequired { get; init; }
}

/// <summary>One GitHub notification thread — the Code inbox row.</summary>
public sealed class GitHubNotification
{
    /// <summary>Thread id, used by mark-read and unsubscribe.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Why it arrived: review_requested, mention, author, comment, ci_activity…</summary>
    public string Reason { get; init; } = string.Empty;

    public bool Unread { get; set; }
    public DateTime? UpdatedAt { get; init; }
    public string SubjectTitle { get; init; } = string.Empty;

    /// <summary>PullRequest, Issue, Release, Commit, CheckSuite, Discussion…</summary>
    public string SubjectType { get; init; } = string.Empty;

    /// <summary>API URL of the subject, e.g. https://api.github.com/repos/o/r/pulls/12. Null for some types.</summary>
    public string? SubjectApiUrl { get; init; }

    /// <summary>Browser URL derived from the subject, falling back to the repository page.</summary>
    public string WebUrl { get; init; } = string.Empty;

    /// <summary>owner/name.</summary>
    public string Repository { get; init; } = string.Empty;

    public bool IsPullRequest => SubjectType == "PullRequest";
    public bool IsIssue => SubjectType == "Issue";

    /// <summary>
    /// The subject's number, parsed from the API URL — present for pull
    /// requests and issues, null for everything else.
    /// </summary>
    public int? SubjectNumber
    {
        get
        {
            if (string.IsNullOrEmpty(SubjectApiUrl)) return null;
            var last = SubjectApiUrl.TrimEnd('/').Split('/').LastOrDefault();
            return int.TryParse(last, out var n) ? n : null;
        }
    }

    public string Owner => Repository.Split('/').FirstOrDefault() ?? string.Empty;
    public string RepositoryName => Repository.Contains('/') ? Repository[(Repository.IndexOf('/') + 1)..] : Repository;
}
