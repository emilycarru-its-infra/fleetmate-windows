using System.Globalization;

namespace FleetMate.Core.Models.Projects;

/// <summary>Which system a pull request came from.</summary>
public enum PullRequestSource
{
    AzureDevOps,
    GitHub,
}

/// <summary>
/// How the signed-in user relates to a pull request. Mirrors the two sections of
/// the Azure DevOps "My pull requests" page.
/// </summary>
public enum PullRequestRelation
{
    /// <summary>The user opened the PR.</summary>
    CreatedByMe,

    /// <summary>
    /// The user is a reviewer (Azure DevOps), or a review is requested of them
    /// or they are an assignee (GitHub).
    /// </summary>
    AssignedToMe,
}

/// <summary>Lifecycle state, normalized across both providers.</summary>
public enum PullRequestState
{
    Open,
    Draft,
    Merged,
    Closed,
}

/// <summary>A reviewer's standing on a pull request.</summary>
public enum PullRequestReviewVote
{
    Rejected = -10,
    WaitingForAuthor = -5,
    NoVote = 0,
    ApprovedWithSuggestions = 5,
    Approved = 10,
}

public static class PullRequestSourceExtensions
{
    public static string DisplayName(this PullRequestSource source) => source switch
    {
        PullRequestSource.AzureDevOps => "Azure DevOps",
        _ => "GitHub",
    };

    /// <summary>Compact label for filter chips, where "Azure DevOps" is too wide.</summary>
    public static string ShortName(this PullRequestSource source) => source switch
    {
        PullRequestSource.AzureDevOps => "DevOps",
        _ => "GitHub",
    };
}

public static class PullRequestRelationExtensions
{
    public static string SectionTitle(this PullRequestRelation relation) => relation switch
    {
        PullRequestRelation.CreatedByMe => "Created by me",
        _ => "Assigned to me",
    };
}

public static class PullRequestReviewVoteExtensions
{
    /// <summary>Map an Azure DevOps <c>vote</c> integer onto the enum, tolerating unknowns.</summary>
    public static PullRequestReviewVote FromAzureDevOps(int? raw)
    {
        if (raw == null) return PullRequestReviewVote.NoVote;
        return Enum.IsDefined(typeof(PullRequestReviewVote), raw.Value)
            ? (PullRequestReviewVote)raw.Value
            : PullRequestReviewVote.NoVote;
    }

    /// <summary>Map a GitHub review state string onto the enum.</summary>
    public static PullRequestReviewVote FromGitHub(string? raw) => raw?.ToUpperInvariant() switch
    {
        "APPROVED" => PullRequestReviewVote.Approved,
        "CHANGES_REQUESTED" => PullRequestReviewVote.Rejected,
        "COMMENTED" => PullRequestReviewVote.ApprovedWithSuggestions,
        _ => PullRequestReviewVote.NoVote,
    };
}

/// <summary>One reviewer on a pull request.</summary>
public sealed class PullRequestReviewer
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public PullRequestReviewVote Vote { get; init; } = PullRequestReviewVote.NoVote;
    public bool IsRequired { get; init; }

    /// <summary>Up to two letters for the avatar bubble, e.g. "Ada Lovelace" → "AL".</summary>
    public string Initials
    {
        get
        {
            var letters = DisplayName
                .Split(new[] { ' ', '.', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                .Take(2)
                .Select(part => part[0])
                .ToArray();

            return letters.Length == 0 ? "?" : new string(letters).ToUpperInvariant();
        }
    }
}

/// <summary>
/// A pull request from either Azure DevOps or GitHub, flattened into one shape so
/// the dashboard queue can render both in a single list.
/// </summary>
public sealed class UnifiedPullRequest : IEquatable<UnifiedPullRequest>
{
    public PullRequestSource Source { get; init; }

    /// <summary>PR number (Azure DevOps <c>pullRequestId</c>, GitHub <c>number</c>).</summary>
    public int Number { get; init; }

    public string Title { get; init; } = string.Empty;
    public string AuthorName { get; init; } = string.Empty;

    /// <summary>Azure DevOps: the project name. GitHub: the owner/org login.</summary>
    public string Container { get; init; } = string.Empty;

    public string Repository { get; init; } = string.Empty;
    public string SourceBranch { get; init; } = string.Empty;
    public string TargetBranch { get; init; } = string.Empty;
    public DateTime? CreatedAt { get; init; }

    /// <summary>
    /// Last comment/review activity. Azure DevOps does not return this on the PR
    /// list endpoint, so it is filled in during thread enrichment.
    /// </summary>
    public DateTime? UpdatedAt { get; set; }

    public PullRequestState State { get; init; }
    public bool HasConflicts { get; init; }

    /// <summary>Human comment threads. Enriched separately for Azure DevOps.</summary>
    public int CommentCount { get; set; }

    public IReadOnlyList<PullRequestReviewer> Reviewers { get; init; } = Array.Empty<PullRequestReviewer>();
    public string WebUrl { get; init; } = string.Empty;

    /// <summary>
    /// A PR can be both created by and assigned to the same user; the queue shows
    /// it under every section it belongs to.
    /// </summary>
    public HashSet<PullRequestRelation> Relations { get; init; } = new();

    public string Id => $"{Source}:{Container}/{Repository}#{Number}";

    /// <summary>The <c>!10716</c> / <c>#42</c> reference shown next to the author.</summary>
    public string Reference => Source == PullRequestSource.AzureDevOps ? $"!{Number}" : $"#{Number}";

    /// <summary>Most recent meaningful timestamp, used for sorting.</summary>
    public DateTime LastActivity
    {
        get
        {
            var updated = UpdatedAt ?? DateTime.MinValue;
            var created = CreatedAt ?? DateTime.MinValue;
            return updated > created ? updated : created;
        }
    }

    /// <summary>
    /// True when the PR has been touched since it was opened — drives the
    /// "Updated …" vs "Created …" wording, matching the Azure DevOps queue.
    ///
    /// The one-minute floor is deliberate: both providers stamp created and
    /// updated within the same second when a PR is opened, and without it every
    /// brand-new PR would claim to have been updated already.
    /// </summary>
    public bool WasUpdatedAfterCreation =>
        CreatedAt is { } created && UpdatedAt is { } updated
        && (updated - created).TotalSeconds > 60;

    public bool Equals(UnifiedPullRequest? other) => other != null && Id == other.Id;
    public override bool Equals(object? obj) => Equals(obj as UnifiedPullRequest);
    public override int GetHashCode() => Id.GetHashCode();
}

/// <summary>A provider that failed while building the queue.</summary>
public sealed class PullRequestQueueError
{
    public PullRequestSource Source { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>The signed-in user's pull request queue across every configured provider.</summary>
public sealed class PullRequestQueue
{
    public List<UnifiedPullRequest> PullRequests { get; init; } = new();

    /// <summary>
    /// Soft failures, one per provider that could not be reached. The queue still
    /// renders whatever the other providers returned — one dead provider must not
    /// blank a queue the rest of which is fine.
    /// </summary>
    public List<PullRequestQueueError> Errors { get; init; } = new();

    public bool IsEmpty => PullRequests.Count == 0;

    public IReadOnlyList<UnifiedPullRequest> Section(PullRequestRelation relation) =>
        PullRequests
            .Where(pr => pr.Relations.Contains(relation))
            .OrderByDescending(pr => pr.LastActivity)
            .ToList();

    /// <summary>
    /// Merge another provider's results in, unioning relations for PRs that
    /// appear in more than one query — a PR you opened and were also asked to
    /// review comes back from both and must not appear twice.
    /// </summary>
    public void Merge(PullRequestQueue other)
    {
        foreach (var pr in other.PullRequests) Insert(pr);
        Errors.AddRange(other.Errors);
    }

    public void Insert(UnifiedPullRequest pr)
    {
        var existing = PullRequests.FirstOrDefault(p => p.Id == pr.Id);
        if (existing != null)
        {
            foreach (var relation in pr.Relations) existing.Relations.Add(relation);
        }
        else
        {
            PullRequests.Add(pr);
        }
    }
}

/// <summary>
/// Lenient ISO-8601 parsing. Azure DevOps and GitHub both emit timestamps with
/// and without fractional seconds depending on the endpoint, so a single strict
/// format drops half of them.
/// </summary>
public static class PullRequestDateParser
{
    public static DateTime? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        return DateTime.TryParse(
            value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
    }
}
