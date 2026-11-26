using FleetMate.Core.Models.Projects;

namespace FleetMate.Core.Development;

/// <summary>Which Development sub-tab is open, and which one the Graphs strip describes.</summary>
public enum DevelopmentSegment { Repos, Pulls, Commits, Pipelines, Skills }

/// <summary>Source filter for the Development lists.</summary>
public enum DevelopmentSourceFilter { All, DevOps, GitHub }

/// <summary>
/// Scope filter. <see cref="Everything"/> is the default, because the point of
/// Development is to see every open PR on the operator's projects, not just their own.
/// </summary>
public enum DevelopmentScope { Everything, Mine }

/// <summary>
/// Status chip filter for the Pipelines list. One value at a time, so the
/// Approval chip and the status chips clear each other.
/// </summary>
public enum PipelineStatusFilter { All, Running, Failed, Succeeded, Approval }

/// <summary>The Development list's filtering rules, shared by FleetMate.GUI and FleetMate.WinUI.</summary>
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
                            && new RepositoryFilterScope(pr.Source, pr.Container, pr.Repository).Matches(repository)
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
    /// Whether the toolbar's Inbox button shows. It shows while something is
    /// unread, and stays while its popover is open so marking the last thread
    /// read does not pull the popover out from under the reader.
    /// </summary>
    public static bool ShowInboxButton(int unread, bool inboxOpen) => unread > 0 || inboxOpen;

    /// <summary>"owner/repo" for GitHub, "Project/Repo" for DevOps — the group header and repo-chip key.</summary>
    public static string RepositoryKey(UnifiedPullRequest pr) => $"{pr.Container}/{pr.Repository}";

    /// <summary>
    /// Every recent comment and review across the loaded PRs, newest first,
    /// optionally without the operator's own.
    /// </summary>
    public static List<(UnifiedPullRequest PullRequest, PullRequestComment Comment)> Activity(
        IEnumerable<UnifiedPullRequest> prs, IReadOnlySet<string> viewerNames, bool hideMine, int limit = 200) =>
        prs.SelectMany(pr => pr.RecentComments.Select(c => (pr, c)))
           .Where(e => !hideMine || !viewerNames.Contains(e.c.AuthorName))
           .OrderByDescending(e => e.c.Date ?? DateTime.MinValue)
           .Take(limit)
           .ToList();

    /// <summary>Repositories with their PR counts, busiest first, ties alphabetical.</summary>
    public static List<(string Repository, int Count)> RepositoryCounts(IEnumerable<UnifiedPullRequest> prs) =>
        prs.GroupBy(RepositoryKey)
           .Select(g => (g.Key, g.Count()))
           .OrderByDescending(e => e.Item2)
           .ThenBy(e => e.Item1, StringComparer.OrdinalIgnoreCase)
           .ToList();

    /// <summary>
    /// Counts by key, busiest first, then by name — a repository dropdown's
    /// entries. Empty when there is only one key: no choice to offer.
    /// </summary>
    public static List<(string Repository, int Count)> KeyCounts<T>(IEnumerable<T> items, Func<T, string> key)
    {
        var counts = items
            .GroupBy(key)
            .Select(g => (Repository: g.Key, Count: g.Count()))
            .OrderByDescending(e => e.Count)
            .ThenBy(e => e.Repository, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return counts.Count > 1 ? counts : new();
    }
}

/// <summary>Filtering rules for Commits and Pipelines, shared by FleetMate.GUI and FleetMate.WinUI.</summary>
public static class CommitsAndPipelinesFilter
{
    /// <summary>Repositories whose name, owner, or any commit subject/author/sha matches.</summary>
    public static List<RepositoryCommits> Commits(
        IEnumerable<RepositoryCommits> repos, DevelopmentSourceFilter source, string? search, string? repository = null)
    {
        var needle = search?.Trim() ?? "";

        return repos
            .Where(r => MatchesSource(r.Source, source))
            .Where(r => new RepositoryFilterScope(r.Source, r.Container, r.Repository).Matches(repository))
            .Where(r => needle.Length == 0
                        || Has(r.DisplayName, needle)
                        || r.Commits.Any(c => Has(c.Subject, needle) || Has(c.AuthorName, needle) || c.Id.StartsWith(needle, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(r => r.LatestDate)
            .ToList();
    }

    /// <summary>
    /// Runs by source, status chip and search over name, repo, branch, requester
    /// and run number. Failed means the pipeline's LATEST run failed: an older
    /// red run under a newer green one is history, not something to act on.
    /// Running and Succeeded still match every run. Approval keeps the runs
    /// <paramref name="awaitingApproval"/> says an approval holds.
    /// </summary>
    public static List<PipelineRun> Runs(
        IEnumerable<PipelineRun> runs, DevelopmentSourceFilter source, PipelineStatusFilter status, string? search,
        string? repository = null, Func<PipelineRun, bool>? awaitingApproval = null)
    {
        var needle = search?.Trim() ?? "";
        var all = runs.ToList();
        var latest = LatestRunIds(all);

        return all
            .Where(r => MatchesSource(r.Source, source))
            .Where(r => new RepositoryFilterScope(r.Source, r.Container, r.Repository).Matches(repository))
            .Where(r => status switch
            {
                PipelineStatusFilter.Running => r.Status.IsActive(),
                PipelineStatusFilter.Failed => IsFailing(r, latest),
                PipelineStatusFilter.Succeeded => r.Status == PipelineRunStatus.Succeeded,
                PipelineStatusFilter.Approval => awaitingApproval?.Invoke(r) == true,
                _ => true,
            })
            .Where(r => needle.Length == 0
                        || Has(r.PipelineName, needle) || Has(r.Repository, needle) || Has(r.Container, needle)
                        || Has(r.Branch, needle) || Has(r.TriggeredBy, needle) || Has(r.RunNumber, needle))
            .OrderByDescending(r => r.SortDate)
            .ToList();
    }

    /// <summary>Runs an approval holds: the Approval chip's count.</summary>
    public static int ApprovalRunCount(IEnumerable<PipelineRun> runs, Func<PipelineRun, bool>? awaitingApproval) =>
        awaitingApproval == null ? 0 : runs.Count(awaitingApproval);

    /// <summary>
    /// The status filter to keep after the approval count changes. Approving or
    /// rejecting the last waiting run turns the Approval filter off, so the list
    /// returns to every run instead of "Nothing matches these filters".
    /// </summary>
    public static PipelineStatusFilter StatusAfterApprovalCount(PipelineStatusFilter status, int count) =>
        status == PipelineStatusFilter.Approval && count == 0 ? PipelineStatusFilter.All : status;

    /// <summary>The Approval chip shows while any run waits, or while it is the active filter.</summary>
    public static bool ShowApprovalChip(int count, PipelineStatusFilter status) =>
        count > 0 || status == PipelineStatusFilter.Approval;

    /// <summary>Repository dropdown entries for Commits, within the current source.</summary>
    public static List<(string Repository, int Count)> CommitRepositoryCounts(
        IEnumerable<RepositoryCommits> repos, DevelopmentSourceFilter source) =>
        DevelopmentFilter.KeyCounts(
            repos.Where(r => MatchesSource(r.Source, source)).SelectMany(r => r.Commits.Select(_ => r)),
            r => r.DisplayName);

    /// <summary>Repository dropdown entries for Pipelines, within the current source.</summary>
    public static List<(string Repository, int Count)> RunRepositoryCounts(
        IEnumerable<PipelineRun> runs, DevelopmentSourceFilter source) =>
        DevelopmentFilter.KeyCounts(runs.Where(r => MatchesSource(r.Source, source)), RunRepositoryKey);

    /// <summary>"owner/repo" or "Project/Repo"; just the project for a build with no repository.</summary>
    public static string RunRepositoryKey(PipelineRun run) =>
        string.IsNullOrEmpty(run.Repository) ? run.Container : $"{run.Container}/{run.Repository}";

    public static bool MatchesSource(PullRequestSource value, DevelopmentSourceFilter source) => source switch
    {
        DevelopmentSourceFilter.DevOps => value == PullRequestSource.AzureDevOps,
        DevelopmentSourceFilter.GitHub => value == PullRequestSource.GitHub,
        _ => true,
    };

    /// <summary>
    /// One pipeline = source + container + pipeline id (name when there is no
    /// id); its latest run is the one that started last.
    /// </summary>
    public static HashSet<string> LatestRunIds(IEnumerable<PipelineRun> runs) =>
        runs.GroupBy(r => $"{r.Source}|{r.Container}|{(r.PipelineId?.ToString() ?? r.PipelineName)}")
            .Select(g => g.OrderByDescending(r => r.SortDate).First().Id)
            .ToHashSet();

    public static bool IsFailing(PipelineRun run, HashSet<string> latestIds) =>
        run.Status is PipelineRunStatus.Failed or PipelineRunStatus.Partial && latestIds.Contains(run.Id);

    /// <summary>Pipelines whose latest run failed — the Failed chip's count.</summary>
    public static int FailingCount(IReadOnlyCollection<PipelineRun> runs)
    {
        var latest = LatestRunIds(runs);
        return runs.Count(r => IsFailing(r, latest));
    }

    private static bool Has(string? hay, string needle) =>
        hay?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;
}
