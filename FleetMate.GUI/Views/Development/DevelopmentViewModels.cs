using System.Windows;
using System.Windows.Media;
using FleetMate.Core.Models.Projects;
using FleetMate.GUI.Views.Shared;

namespace FleetMate.GUI.Views.Development;

/// <summary>Source filter for the Development pull request list.</summary>
public enum DevelopmentSourceFilter { All, DevOps, GitHub }

/// <summary>
/// Scope filter. <see cref="Everything"/> is the default, because the point of
/// Development is to see every open PR on the operator's projects, not just their own.
/// </summary>
public enum DevelopmentScope { Everything, Mine }

/// <summary>The Development list's filtering rules, kept out of the view so they can be tested.</summary>
public static class DevelopmentFilter
{
    public static bool MatchesSource(UnifiedPullRequest pr, DevelopmentSourceFilter source) => source switch
    {
        DevelopmentSourceFilter.DevOps => pr.Source == PullRequestSource.AzureDevOps,
        DevelopmentSourceFilter.GitHub => pr.Source == PullRequestSource.GitHub,
        _ => true,
    };

    /// <summary>Mine = anything with a personal relation; Organization alone does not count.</summary>
    public static bool MatchesScope(UnifiedPullRequest pr, DevelopmentScope scope) =>
        scope == DevelopmentScope.Everything
        || pr.Relations.Any(r => r != PullRequestRelation.Organization);

    /// <summary>Case-insensitive match on title, repository, author, reference and branch.</summary>
    public static bool MatchesSearch(UnifiedPullRequest pr, string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        var needle = search.Trim();

        return Contains(pr.Title) || Contains(pr.Repository) || Contains(pr.Container)
            || Contains(pr.AuthorName) || Contains(pr.Reference) || Contains(pr.SourceBranch);

        bool Contains(string? hay) => hay?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>Every filter together, by last modified — newest first unless <paramref name="oldestFirst"/>.</summary>
    public static List<UnifiedPullRequest> Apply(
        IEnumerable<UnifiedPullRequest> prs, DevelopmentSourceFilter source, DevelopmentScope scope,
        string? repository, string? search, bool oldestFirst = false) =>
        SortByModified(
            prs.Where(pr => MatchesSource(pr, source)
                            && MatchesScope(pr, scope)
                            && (repository == null || RepositoryKey(pr) == repository)
                            && MatchesSearch(pr, search)),
            oldestFirst);

    /// <summary>Last modified, newest first, or oldest first.</summary>
    public static List<UnifiedPullRequest> SortByModified(IEnumerable<UnifiedPullRequest> prs, bool oldestFirst) =>
        (oldestFirst ? prs.OrderBy(pr => pr.LastActivity) : prs.OrderByDescending(pr => pr.LastActivity)).ToList();

    /// <summary>
    /// Sections per repository for Group by Repository, rows in the chosen modified
    /// order. A section ranks by its first row — its newest, or its oldest when
    /// <paramref name="oldestFirst"/> — and ties go alphabetically.
    /// </summary>
    public static List<(string Repository, List<UnifiedPullRequest> Rows)> GroupByRepository(
        IEnumerable<UnifiedPullRequest> prs, bool oldestFirst)
    {
        var groups = SortByModified(prs, oldestFirst)
            .GroupBy(RepositoryKey)
            .Select(g => (Repository: g.Key, Rows: g.ToList()));

        var ordered = oldestFirst
            ? groups.OrderBy(g => g.Rows[0].LastActivity)
            : groups.OrderByDescending(g => g.Rows[0].LastActivity);

        return ordered.ThenBy(g => g.Repository, StringComparer.Ordinal).ToList();
    }

    /// <summary>The inbox list: unread only, or every loaded thread.</summary>
    public static List<GitHubNotification> Inbox(IEnumerable<GitHubNotification> notifications, bool showRead) =>
        notifications.Where(n => showRead || n.Unread).ToList();

    /// <summary>
    /// Whether the Inbox segment shows. It shows while something is unread, and
    /// stays while it is the open segment so marking the last thread read does
    /// not pull the page out from under the reader.
    /// </summary>
    public static bool ShowInboxSegment(int unread, bool inboxOpen) => unread > 0 || inboxOpen;

    /// <summary>
    /// Whether to leave an open Inbox for Pulls when the tab is shown again. The
    /// view outlives the tab, so an Inbox read down to zero would otherwise stay
    /// the open segment on every later visit, hidden from the bar but still open.
    /// </summary>
    public static bool LeaveEmptyInbox(int unread, bool inboxOpen) => inboxOpen && unread == 0;

    /// <summary>"owner/repo" for GitHub, "Project/Repo" for DevOps — the group header and repo-chip key.</summary>
    public static string RepositoryKey(UnifiedPullRequest pr) => $"{pr.Container}/{pr.Repository}";

    /// <summary>
    /// Every recent comment and review across the loaded PRs, newest first,
    /// optionally without the operator's own.
    /// </summary>
    public static List<DevelopmentActivityRowViewModel> Activity(
        IEnumerable<UnifiedPullRequest> prs, IReadOnlySet<string> viewerNames, bool hideMine, int limit = 200) =>
        prs.SelectMany(pr => pr.RecentComments.Select(c => (pr, c)))
           .Where(e => !hideMine || !viewerNames.Contains(e.c.AuthorName))
           .OrderByDescending(e => e.c.Date ?? DateTime.MinValue)
           .Take(limit)
           .Select(e => new DevelopmentActivityRowViewModel { PullRequest = e.pr, Comment = e.c })
           .ToList();

    /// <summary>Repositories with their PR counts, busiest first, ties alphabetical.</summary>
    public static List<(string Repository, int Count)> RepositoryCounts(IEnumerable<UnifiedPullRequest> prs) =>
        prs.GroupBy(RepositoryKey)
           .Select(g => (g.Key, g.Count()))
           .OrderByDescending(e => e.Item2)
           .ThenBy(e => e.Item1, StringComparer.OrdinalIgnoreCase)
           .ToList();
}

/// <summary>One row in the Development pull request list.</summary>
public sealed class DevelopmentPullRequestRowViewModel
{
    public required UnifiedPullRequest PullRequest { get; init; }

    private PullRequestRowViewModel Row => new() { PullRequest = PullRequest };

    public string Title => PullRequest.Title;
    public string Byline => Row.Byline;
    public string StateLabel => Row.StateLabel;
    public Brush StateBrush => Row.StateBrush;
    public string SourceLabel => Row.SourceLabel;
    public string RepositoryKey => DevelopmentFilter.RepositoryKey(PullRequest);

    /// <summary>Names the repository on the row in the ungrouped list, where no section header does.</summary>
    public bool ShowRepository { get; init; }

    /// <summary>"owner/repo · ada · #42 · Updated 3h ago" ungrouped; the plain byline under a section header.</summary>
    public string Subtitle => ShowRepository ? $"{RepositoryKey} · {Byline}" : Byline;

    /// <summary>
    /// Why this PR is on the operator's plate, strongest reason first — "Review"
    /// beats "Mine" beats "Involved". Empty for organization-only rows.
    /// </summary>
    public string RelationLabel =>
        PullRequest.Relations.Contains(PullRequestRelation.AssignedToMe) ? "Review"
        : PullRequest.Relations.Contains(PullRequestRelation.CreatedByMe) ? "Mine"
        : PullRequest.Relations.Contains(PullRequestRelation.Involved) ? "Involved"
        : "";

    public Visibility RelationVisibility => RelationLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string CopyIdLabel => $"Copy ID ({PullRequest.Number})";
}

/// <summary>One comment or review in the activity sidebar.</summary>
public sealed class DevelopmentActivityRowViewModel
{
    public required UnifiedPullRequest PullRequest { get; init; }
    public required PullRequestComment Comment { get; init; }

    public string AuthorName => Comment.AuthorName;
    public string Age => DevelopmentNotificationRowViewModel.Age(Comment.Date);
    public string PullRequestLabel => $"{PullRequest.Repository} {PullRequest.Reference} · {PullRequest.Title}";
    public string PullRequestTitle => PullRequest.Title;

    /// <summary>Review verbs ("approved") read as events, so they render italic.</summary>
    public FontStyle SnippetStyle => Comment.IsSystem ? FontStyles.Italic : FontStyles.Normal;

    /// <summary>First 240 characters, HTML stripped (DevOps), whitespace collapsed.</summary>
    public string Snippet
    {
        get
        {
            var text = PullRequestCommentViewModel.Strip(Comment.Body);
            text = System.Text.RegularExpressions.Regex.Replace(text, "\\s+", " ").Trim();
            return text.Length > 240 ? text[..240] + "…" : text;
        }
    }
}

/// <summary>One row in the Development inbox.</summary>
public sealed class DevelopmentNotificationRowViewModel
{
    public required GitHubNotification Notification { get; init; }

    public string Title => Notification.SubjectTitle;
    public bool Unread => Notification.Unread;
    public FontWeight TitleWeight => Unread ? FontWeights.SemiBold : FontWeights.Normal;
    public Visibility UnreadVisibility => Unread ? Visibility.Visible : Visibility.Hidden;
    public Visibility MarkReadVisibility => Unread ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>"Review requested · owner/repo · 3h ago".</summary>
    public string Byline => $"{ReasonLabel(Notification.Reason)} · {Notification.Repository} · {Age(Notification.UpdatedAt)}";

    public string TypeGlyph => Notification.SubjectType switch
    {
        "PullRequest" => "",
        "Issue" => "",
        "Release" => "",
        "Commit" => "",
        "CheckSuite" => "",
        "Discussion" => "",
        _ => "",
    };

    public string TypeLabel => Notification.SubjectType switch
    {
        "PullRequest" => "Pull request",
        "CheckSuite" => "Checks",
        "" => "Notification",
        var other => other,
    };

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

    public static string Age(DateTime? when)
    {
        if (when is not { } value) return "recently";
        var elapsed = DateTime.UtcNow - value;
        if (elapsed.TotalMinutes < 1) return "just now";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours}h ago";
        return $"{(int)elapsed.TotalDays}d ago";
    }
}
