using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;

namespace FleetMate.Core.Development;

/// <summary>
/// The words and icons on Development's rows, shared by FleetMate.GUI and
/// FleetMate.WinUI so both say the same thing. Colours stay with each UI.
/// </summary>
public static class DevelopmentText
{
    /// <summary>Relative age in the lists' own units — days, not "last month".</summary>
    public static string Age(DateTime? when)
    {
        if (when is not { } value) return "recently";
        var elapsed = DateTime.UtcNow - value;
        if (elapsed.TotalMinutes < 1) return "just now";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours}h ago";
        return $"{(int)elapsed.TotalDays}d ago";
    }

    // MARK: - Pull requests

    /// <summary>"Ada Lovelace · !4821 · Updated 3d ago".</summary>
    public static string PullRequestByline(UnifiedPullRequest pr)
    {
        var when = pr.WasUpdatedAfterCreation ? $"Updated {Age(pr.UpdatedAt)}" : $"Created {Age(pr.CreatedAt)}";
        return $"{pr.AuthorName} · {pr.Reference} · {when}";
    }

    public static string StateLabel(UnifiedPullRequest pr) => pr.State switch
    {
        PullRequestState.Draft => "Draft",
        PullRequestState.Merged => "Merged",
        PullRequestState.Closed => "Closed",
        _ => pr.HasConflicts ? "Conflicts" : "Open",
    };

    /// <summary>
    /// Why this PR is on the operator's plate, strongest reason first — "Review"
    /// beats "Mine" beats "Involved". Empty for organization-only rows.
    /// </summary>
    public static string RelationLabel(UnifiedPullRequest pr) =>
        pr.Relations.Contains(PullRequestRelation.AssignedToMe) ? "Review"
        : pr.Relations.Contains(PullRequestRelation.CreatedByMe) ? "Mine"
        : pr.Relations.Contains(PullRequestRelation.Involved) ? "Involved"
        : "";

    /// <summary>"owner/repo · ada · #42 · Updated 3h ago" ungrouped; the plain byline under a section header.</summary>
    public static string PullRequestSubtitle(UnifiedPullRequest pr, bool showRepository) =>
        showRepository ? $"{DevelopmentFilter.RepositoryKey(pr)} · {PullRequestByline(pr)}" : PullRequestByline(pr);

    /// <summary>The count line under the Pulls filters.</summary>
    public static string PullsSummary(int rows, int repositories, IEnumerable<PullRequestQueueError> errors)
    {
        var errorText = string.Join(" · ", errors
            .Where(e => !IsExpectedSignedOut(e))
            .Select(e => $"{e.Source.ShortName()} unavailable"));
        return $"{rows} open across {PipelineAvailability.Count(repositories, "repository", "repositories")}"
               + (errorText.Length > 0 ? $" · {errorText}" : "");
    }

    /// <summary>
    /// True when a provider failure just means "you have not signed in here",
    /// which is a configuration state rather than something going wrong.
    /// </summary>
    public static bool IsExpectedSignedOut(PullRequestQueueError error)
    {
        if (error.Source != PullRequestSource.GitHub) return false;

        var message = error.Message ?? "";

        // Matched against the messages the stack actually produces rather than
        // plausible-looking ones: "No authentication token available" is what
        // the client throws when the token source comes back empty, and "Bad
        // credentials" is GitHub's own reply to a stale token.
        return message.Contains("authentication token", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not authenticated", StringComparison.OrdinalIgnoreCase)
            || message.Contains("bad credentials", StringComparison.OrdinalIgnoreCase)
            || message.Contains("gh auth login", StringComparison.OrdinalIgnoreCase);
    }

    // MARK: - Inbox

    /// <summary>GitHub's reason codes, in the words its own inbox uses.</summary>
    public static string ReasonLabel(string reason) => reason switch
    {
        "review_requested" => "Review requested",
        "mention" => "Mentioned",
        "team_mention" => "Team mentioned",
        "author" => "Author",
        "comment" => "Commented",
        "assign" => "Assigned",
        "state_change" => "State changed",
        "ci_activity" => "CI activity",
        "subscribed" => "Watching",
        "manual" => "Subscribed",
        "approval_requested" => "Approval requested",
        "security_alert" => "Security alert",
        "invitation" => "Invitation",
        "member_feature_requested" => "Feature requested",
        "" => "Notification",
        _ => reason.Replace('_', ' '),
    };

    public static string NotificationGlyph(string subjectType) => subjectType switch
    {
        "PullRequest" => "",
        "Issue" => "",
        "Release" => "",
        "Commit" => "",
        "CheckSuite" => "",
        "Discussion" => "",
        _ => "",
    };

    public static string NotificationTypeLabel(string subjectType) => subjectType switch
    {
        "PullRequest" => "Pull request",
        "CheckSuite" => "Checks",
        "" => "Notification",
        var other => other,
    };

    /// <summary>
    /// The author of the pull request a thread is about, from the pull requests
    /// already loaded. GitHub's notification does not say who acted, so this is
    /// labelled as the PR's author, never as the sender.
    /// </summary>
    public static string? NotificationPullRequestAuthor(GitHubNotification notification, IEnumerable<UnifiedPullRequest> pullRequests) =>
        notification.IsPullRequest && notification.SubjectNumber is { } number
            ? pullRequests.FirstOrDefault(pr => pr.Source == PullRequestSource.GitHub
                && pr.Number == number
                && $"{pr.Container}/{pr.Repository}".Equals(notification.Repository, StringComparison.OrdinalIgnoreCase))?.AuthorName
            : null;

    public static string AuthorLabel(string? pullRequestAuthor) => string.IsNullOrWhiteSpace(pullRequestAuthor)
        ? "PR author unavailable" : $"PR author: {pullRequestAuthor}";

    /// <summary>"Review requested · owner/repo · 3h ago".</summary>
    public static string NotificationByline(GitHubNotification notification, string? pullRequestAuthor) =>
        $"{ReasonLabel(notification.Reason)} · {notification.Repository} · {Age(notification.UpdatedAt)}"
        + (notification.IsPullRequest ? $" · {AuthorLabel(pullRequestAuthor)}" : "");

    /// <summary>The status line at the top of the Inbox popover.</summary>
    public static string InboxStatus(DevelopmentInbox inbox) =>
        inbox.LastError is { } error && inbox.Notifications.Count == 0
            ? $"GitHub inbox unavailable — {error}"
            : (inbox.LastRefreshed is { } at ? $"Checked {at:HH:mm}" : "")
              + (inbox.LastError != null ? $" · showing saved notifications; refresh failed: {inbox.LastError}" : "");

    public static string InboxEmpty(DevelopmentInbox inbox, bool showRead) =>
        inbox.LastError != null ? "Could not reach GitHub notifications."
        : showRead ? "No notifications." : "Inbox zero.";

    // MARK: - Commits

    public const int CollapsedCommits = 3;

    public static string CommitByline(PullRequestCommit commit) =>
        $"{commit.AuthorName ?? "unknown"} · {commit.ShortSha} · {Age(commit.Date)}";

    public static string RepositoryCommitsSubtitle(RepositoryCommits repository) =>
        $"{repository.DefaultBranch ?? "default branch"} · {Age(repository.LatestDate)}";

    public static string ShowAllLabel(RepositoryCommits repository, bool showingAll) =>
        showingAll ? "Show fewer" : $"Show all {repository.Commits.Count}";

    public static string CommitsSummary(IReadOnlyCollection<RepositoryCommits> repos) =>
        $"{repos.Sum(r => r.Commits.Count)} commits across {repos.Count} repositories · last 14 days";

    // MARK: - Pipelines

    public const string WaitingForApproval = "Waiting for approval";

    public static string RunTitle(PipelineRun run) => $"{run.PipelineName} #{run.RunNumber}";

    public static string RunSubtitle(PipelineRun run)
    {
        var where = string.IsNullOrEmpty(run.Repository) || run.Repository == run.Container
            ? run.Container
            : $"{run.Container}/{run.Repository}";
        return string.IsNullOrEmpty(run.Branch) ? where : $"{where} · {run.Branch}";
    }

    public static string RunByline(PipelineRun run) =>
        string.Join(" · ", new[]
        {
            run.TriggeredBy,
            Age(run.StartedAt ?? run.FinishedAt),
            FormatDuration(run.Duration),
        }.Where(s => !string.IsNullOrEmpty(s)));

    public static string RunStatusLabel(PipelineRun run, bool awaitingApproval) =>
        awaitingApproval ? WaitingForApproval : run.Status.DisplayName();

    /// <summary>"42s", "3m 05s", "1h 12m".</summary>
    public static string FormatDuration(TimeSpan? duration)
    {
        if (duration is not { } d || d < TimeSpan.Zero) return "";
        if (d.TotalMinutes < 1) return $"{(int)d.TotalSeconds}s";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m {d.Seconds:00}s";
        return $"{(int)d.TotalHours}h {d.Minutes:00}m";
    }

    public static string StatusGlyph(PipelineRunStatus status) => status switch
    {
        PipelineRunStatus.Succeeded => "",
        PipelineRunStatus.Failed => "",
        PipelineRunStatus.Partial => "",
        PipelineRunStatus.Running => "",
        PipelineRunStatus.Queued => "",
        PipelineRunStatus.Cancelled => "",
        _ => "",
    };

    /// <summary>The count line under the Pipelines filters.</summary>
    public static string PipelinesSummary(int rows, IReadOnlyCollection<PipelineRun> all) =>
        $"{PipelineAvailability.Count(rows, "run")} · {all.Count(r => r.Status.IsActive())} running · "
        + $"{CommitsAndPipelinesFilter.FailingCount(all)} failed · last 7 days";
}
