using System.Globalization;
using FleetMate.Core.Knowledge;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Shared;

namespace FleetMate.Core.Development;

/// <summary>
/// The figures behind Development's Graphs strip — counts, buckets, top-N
/// lists and KPI values — computed from what the tab has already loaded, with
/// no requests of their own. FleetMate.GUI and FleetMate.WinUI both draw from
/// these (through <see cref="DevelopmentGraphCards"/>), so the two apps always
/// count the same way.
/// </summary>
public static class DevelopmentFigures
{
    /// <summary>The most bars or rows a ranked card shows.</summary>
    public const int TopCount = 8;

    // MARK: - Pulls

    /// <summary>Open pull requests per repository, most first, top eight.</summary>
    public static List<ChartSlice> RepositorySlices(IEnumerable<UnifiedPullRequest> prs) => TopSlices(prs, DevelopmentFilter.RepositoryKey);

    /// <summary>Age buckets on creation date, every bucket shown so the shape reads at a glance.</summary>
    public static readonly string[] AgeBuckets = { "Today", "1–7 days", "8–30 days", "Over 30 days" };

    public static string AgeBucket(DateTime? created, DateTime now)
    {
        if (created is not { } at) return AgeBuckets[3];
        var days = (now.Date - Local(at).Date).TotalDays;
        return days < 1 ? AgeBuckets[0] : days <= 7 ? AgeBuckets[1] : days <= 30 ? AgeBuckets[2] : AgeBuckets[3];
    }

    public static List<ChartSlice> AgeSlices(IEnumerable<UnifiedPullRequest> prs, DateTime now)
    {
        var counts = prs.GroupBy(p => AgeBucket(p.CreatedAt, now)).ToDictionary(g => g.Key, g => g.Count());
        return AgeBuckets.Select(b => new ChartSlice(b, counts.GetValueOrDefault(b))).ToList();
    }

    /// <summary>Review states in precedence order: each pull request counts once, under the first that applies.</summary>
    public static readonly string[] ReviewStates = { "Draft", "Conflicts", "Changes Requested", "Approved", "Awaiting Review" };

    public static string ReviewState(UnifiedPullRequest pr)
    {
        if (pr.State == PullRequestState.Draft) return ReviewStates[0];
        if (pr.HasConflicts) return ReviewStates[1];
        if (pr.Reviewers.Any(r => r.Vote is PullRequestReviewVote.Rejected or PullRequestReviewVote.WaitingForAuthor)) return ReviewStates[2];
        if (pr.Reviewers.Any(r => r.Vote is PullRequestReviewVote.Approved or PullRequestReviewVote.ApprovedWithSuggestions)) return ReviewStates[3];
        return ReviewStates[4];
    }

    public static List<ChartSlice> ReviewSlices(IEnumerable<UnifiedPullRequest> prs)
    {
        var counts = prs.GroupBy(ReviewState).ToDictionary(g => g.Key, g => g.Count());
        return ReviewStates.Where(counts.ContainsKey).Select(s => new ChartSlice(s, counts[s])).ToList();
    }

    /// <summary>Pull requests per author, most first, top eight; a missing author reads "Unknown".</summary>
    public static List<ChartSlice> PullAuthorSlices(IEnumerable<UnifiedPullRequest> prs) =>
        TopSlices(prs, p => string.IsNullOrWhiteSpace(p.AuthorName) ? "Unknown" : p.AuthorName);

    /// <summary>The eight pull requests with the most comments, busiest first, newest activity breaking ties.</summary>
    public static List<UnifiedPullRequest> MostDiscussed(IEnumerable<UnifiedPullRequest> prs) => prs
        .Where(p => p.CommentCount > 0)
        .OrderByDescending(p => p.CommentCount).ThenByDescending(p => p.UpdatedAt ?? p.CreatedAt ?? DateTime.MinValue)
        .Take(TopCount).ToList();

    /// <summary>
    /// Whether a provider failed this load (rate-limited, unreachable). Its
    /// count is unknown, so the tile shows "--": a 0 would read as nothing open.
    /// Being signed out of a provider on purpose is not a failure.
    /// </summary>
    public static bool Failed(PullRequestQueue? queue, PullRequestSource? source = null) =>
        queue?.Errors.Any(e => (source == null || e.Source == source) && !DevelopmentText.IsExpectedSignedOut(e)) == true;

    /// <summary>A count, or "--" when its provider failed.</summary>
    public static string CountOrUnknown(int value, bool failed) => failed ? "--" : value.ToString("N0");

    // MARK: - Time

    /// <summary>The first day of the week <paramref name="date"/> falls in, in the user's calendar.</summary>
    public static DateTime WeekStart(DateTime date)
    {
        var first = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var offset = ((int)date.DayOfWeek - (int)first + 7) % 7;
        return date.Date.AddDays(-offset);
    }

    /// <summary>One entry per week for the last <paramref name="weeks"/> weeks, oldest first, empty weeks included.</summary>
    public static List<(DateTime Start, int Count)> PerWeek(IEnumerable<DateTime?> dates, int weeks, DateTime today)
    {
        var thisWeek = WeekStart(today);
        var counts = dates.OfType<DateTime>().GroupBy(d => WeekStart(Local(d))).ToDictionary(g => g.Key, g => g.Count());
        return Enumerable.Range(0, weeks).Select(i => thisWeek.AddDays(-7 * (weeks - 1 - i)))
            .Select(w => (w, counts.GetValueOrDefault(w))).ToList();
    }

    /// <summary>Counts by weekday in the user's week order, every day present.</summary>
    public static List<ChartSlice> Weekdays(IEnumerable<DateTime?> dates)
    {
        var format = CultureInfo.CurrentCulture.DateTimeFormat;
        var counts = dates.OfType<DateTime>().GroupBy(d => Local(d).DayOfWeek).ToDictionary(g => g.Key, g => g.Count());
        return Enumerable.Range(0, 7).Select(i => (DayOfWeek)(((int)format.FirstDayOfWeek + i) % 7))
            .Select(d => new ChartSlice(format.GetAbbreviatedDayName(d), counts.GetValueOrDefault(d))).ToList();
    }

    /// <summary>"just now", "5 min ago", "3 h ago", "12 d ago", then the date: how Repos and the Graphs say when.</summary>
    public static string Relative(DateTimeOffset? date, DateTimeOffset now)
    {
        if (date is not { } d) return "—";
        var span = now - d;
        if (span.TotalSeconds < 60) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} h ago";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays} d ago";
        return d.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }

    // MARK: - Commits

    /// <summary>How many days the Commits segment loads.</summary>
    public const int CommitDays = 14;

    /// <summary>Commits per local day, oldest first, ending today.</summary>
    public static List<(DateTime Day, int Count)> CommitsPerDay(IEnumerable<RepositoryCommits> repos, DateTime today, int days = CommitDays)
    {
        var start = today.Date.AddDays(-(days - 1));
        var counts = repos.SelectMany(r => r.Commits)
            .Where(c => c.Date != null)
            .GroupBy(c => Local(c.Date!.Value).Date)
            .ToDictionary(g => g.Key, g => g.Count());
        return Enumerable.Range(0, days).Select(i => start.AddDays(i)).Select(d => (d, counts.GetValueOrDefault(d))).ToList();
    }

    public static List<ChartSlice> CommitRepositorySlices(IEnumerable<RepositoryCommits> repos) => repos
        .Select(r => new ChartSlice(r.DisplayName, r.Commits.Count))
        .Where(s => s.Value > 0)
        .OrderByDescending(s => s.Value).ThenBy(s => s.Label, StringComparer.Ordinal)
        .Take(TopCount).ToList();

    public static List<ChartSlice> AuthorSlices(IEnumerable<RepositoryCommits> repos) =>
        TopSlices(repos.SelectMany(r => r.Commits), c => string.IsNullOrWhiteSpace(c.AuthorName) ? "Unknown" : c.AuthorName!);

    /// <summary>Distinct commit authors, ignoring case and commits with no author.</summary>
    public static int AuthorCount(IEnumerable<RepositoryCommits> repos) => repos.SelectMany(r => r.Commits)
        .Select(c => c.AuthorName).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    /// <summary>Commits per provider, Azure DevOps first, providers with none left out.</summary>
    public static List<(PullRequestSource Source, int Count)> CommitSources(IEnumerable<RepositoryCommits> repos)
    {
        var list = repos as IReadOnlyCollection<RepositoryCommits> ?? repos.ToList();
        return new[] { PullRequestSource.AzureDevOps, PullRequestSource.GitHub }
            .Select(src => (Source: src, Count: list.Where(r => r.Source == src).Sum(r => r.Commits.Count)))
            .Where(x => x.Count > 0).ToList();
    }

    /// <summary>Repositories by their newest commit, most recent first, top eight.</summary>
    public static List<(string DisplayName, DateTimeOffset? Latest)> RecentlyActive(IEnumerable<RepositoryCommits> repos) => repos
        .Select(r => (r.DisplayName, Latest: r.Commits.Where(c => c.Date != null).Select(c => (DateTime?)c.Date).Max()))
        .Where(r => r.Latest != null)
        .OrderByDescending(r => r.Latest)
        .Take(TopCount)
        .Select(r => (r.DisplayName, (DateTimeOffset?)new DateTimeOffset(Local(r.Latest!.Value))))
        .ToList();

    // MARK: - Pipelines

    /// <summary>How many days the Pipelines segment loads.</summary>
    public const int RunDays = 7;

    /// <summary>Outcome groups in legend order. Skipped and unknown runs fold into Other.</summary>
    public static readonly string[] Outcomes = { "Succeeded", "Failed", "Partially succeeded", "Cancelled", "Running", "Other" };

    public static string Outcome(PipelineRunStatus status) => status switch
    {
        PipelineRunStatus.Succeeded => Outcomes[0],
        PipelineRunStatus.Failed => Outcomes[1],
        PipelineRunStatus.Partial => Outcomes[2],
        PipelineRunStatus.Cancelled => Outcomes[3],
        PipelineRunStatus.Running or PipelineRunStatus.Queued => Outcomes[4],
        _ => Outcomes[5],
    };

    /// <summary>The Pipelines status filter an outcome opens, or null for one with no filter of its own.</summary>
    public static PipelineStatusFilter? OutcomeFilter(string outcome) => outcome switch
    {
        "Succeeded" => PipelineStatusFilter.Succeeded,
        "Failed" => PipelineStatusFilter.Failed,
        "Running" => PipelineStatusFilter.Running,
        _ => null,
    };

    public static List<ChartSlice> OutcomeSlices(IEnumerable<PipelineRun> runs)
    {
        var counts = runs.GroupBy(r => Outcome(r.Status)).ToDictionary(g => g.Key, g => g.Count());
        return Outcomes.Where(counts.ContainsKey).Select(o => new ChartSlice(o, counts[o])).ToList();
    }

    /// <summary>Succeeded over every run that reached a verdict (succeeded, failed or partial); null with none.</summary>
    public static int? SuccessRate(IEnumerable<PipelineRun> runs)
    {
        var judged = runs.Where(r => r.Status is PipelineRunStatus.Succeeded or PipelineRunStatus.Failed or PipelineRunStatus.Partial).ToList();
        if (judged.Count == 0) return null;
        return (int)Math.Round(100.0 * judged.Count(r => r.Status == PipelineRunStatus.Succeeded) / judged.Count);
    }

    /// <summary>Runs per local day and outcome, oldest first, ending today. Outcomes with no runs are left out.</summary>
    public static (List<DateTime> Days, List<(string Outcome, List<int> Counts)> Series) RunsPerDay(IEnumerable<PipelineRun> runs, DateTime today, int days = RunDays)
    {
        var start = today.Date.AddDays(-(days - 1));
        var dayList = Enumerable.Range(0, days).Select(i => start.AddDays(i)).ToList();
        var dated = runs.Where(r => (r.StartedAt ?? r.FinishedAt) != null).ToList();
        var series = new List<(string, List<int>)>();
        foreach (var outcome in Outcomes)
        {
            var counts = dayList.Select(d => dated.Count(r => Outcome(r.Status) == outcome && Local((r.StartedAt ?? r.FinishedAt)!.Value).Date == d)).ToList();
            if (counts.Any(c => c > 0)) series.Add((outcome, counts));
        }
        return (dayList, series);
    }

    /// <summary>Runs per repository, most first, top eight.</summary>
    public static List<ChartSlice> RunRepositorySlices(IReadOnlyCollection<PipelineRun> runs) =>
        CommitsAndPipelinesFilter.RunRepositoryCounts(runs, DevelopmentSourceFilter.All)
            .OrderByDescending(c => c.Count).ThenBy(c => c.Repository, StringComparer.Ordinal)
            .Take(TopCount).Select(c => new ChartSlice(c.Repository, c.Count)).ToList();

    /// <summary>Median length in whole minutes of each pipeline's finished runs, slowest first, top eight.</summary>
    public static List<ChartSlice> SlowestPipelines(IEnumerable<PipelineRun> runs) => runs
        .Where(r => r is { StartedAt: not null, FinishedAt: not null } && !r.Status.IsActive())
        .GroupBy(r => r.PipelineName)
        .Select(g =>
        {
            var minutes = g.Select(r => (r.FinishedAt!.Value - r.StartedAt!.Value).TotalMinutes).Where(m => m >= 0).OrderBy(m => m).ToList();
            if (minutes.Count == 0) return null;
            var mid = minutes.Count / 2;
            var median = minutes.Count % 2 == 1 ? minutes[mid] : (minutes[mid - 1] + minutes[mid]) / 2;
            return new ChartSlice(g.Key, (int)Math.Max(1, Math.Round(median)));
        })
        .OfType<ChartSlice>()
        .OrderByDescending(s => s.Value).ThenBy(s => s.Label, StringComparer.Ordinal)
        .Take(TopCount).ToList();

    /// <summary>Failed and partially succeeded runs per pipeline, most first, top eight.</summary>
    public static List<ChartSlice> FailuresByPipeline(IEnumerable<PipelineRun> runs) =>
        TopSlices(runs.Where(r => r.Status is PipelineRunStatus.Failed or PipelineRunStatus.Partial), r => r.PipelineName);

    /// <summary>Runs per person or trigger that started them, most first, top eight.</summary>
    public static List<ChartSlice> TriggerSlices(IEnumerable<PipelineRun> runs) =>
        TopSlices(runs.Where(r => !string.IsNullOrWhiteSpace(r.TriggeredBy)), r => r.TriggeredBy!);

    // MARK: - Skills

    public static List<ChartSlice> SkillGroupSlices(IEnumerable<SkillEntry> entries) => entries
        .GroupBy(e => (e.Kind, e.Origin))
        .OrderBy(g => g.Key.Origin).ThenBy(g => g.Key.Kind)
        .Select(g => new ChartSlice(g.First().Group, g.Count()))
        .ToList();

    public static List<ChartSlice> MostFiles(IEnumerable<SkillEntry> entries) => entries
        .Where(e => e.Kind == SkillKind.Skill && e.Files.Count > 0)
        .Select(e => new ChartSlice(e.Name, e.Files.Count))
        .OrderByDescending(s => s.Value).ThenBy(s => s.Label, StringComparer.Ordinal)
        .Take(TopCount).ToList();

    public static List<ChartSlice> Longest(IEnumerable<SkillEntry> entries) => entries
        .Select(e => new ChartSlice(e.Name, e.Body.Length == 0 ? 0 : e.Body.Count(c => c == '\n') + 1))
        .Where(s => s.Value > 0)
        .OrderByDescending(s => s.Value).ThenBy(s => s.Label, StringComparer.Ordinal)
        .Take(TopCount).ToList();

    /// <summary>Files shipped beside skills and hooks, by extension, most first, top eight.</summary>
    public static List<ChartSlice> ScriptExtensions(IEnumerable<SkillEntry> entries) => TopSlices(
        entries.SelectMany(e => e.Files),
        f => Path.GetExtension(f) is { Length: > 0 } ext ? ext.ToLowerInvariant() : "no extension");

    // MARK: - Pieces

    /// <summary>Items grouped by <paramref name="key"/>, most first, ties by name, top eight.</summary>
    public static List<ChartSlice> TopSlices<T>(IEnumerable<T> items, Func<T, string> key) => items
        .GroupBy(key)
        .Select(g => new ChartSlice(g.Key, g.Count()))
        .OrderByDescending(s => s.Value)
        .ThenBy(s => s.Label, StringComparer.Ordinal)
        .Take(TopCount)
        .ToList();

    private static DateTime Local(DateTime value) => value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;
}
