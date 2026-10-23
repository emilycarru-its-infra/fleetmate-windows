using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FleetMate.Core.Knowledge;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Repos;
using FleetMate.GUI.Views.Development;
using FleetMate.GUI.Views.Development.Repos;
using P = FleetMate.GUI.Views.Shared.Widgets.WidgetPalette;

namespace FleetMate.GUI.Views.Shared.Widgets;

/// <summary>Which Development sub-tab the Graphs strip describes.</summary>
public enum DevelopmentSegment { Repos, Pulls, Commits, Pipelines, Skills }

/// <summary>
/// Development's Graphs strip, one set per sub-tab, matching the macOS app.
/// Every figure comes from what the tab has already loaded: no requests of
/// its own. Repos carries the repository Insights instead, built by the page.
/// </summary>
public static partial class WidgetCatalog
{
    private static readonly Color[] RepositoryColors = { P.Blue, P.Purple, P.Orange, P.Teal, P.Green, P.Indigo, P.Brown, P.Pink };

    /// <summary>The strip for <paramref name="segment"/>. Repos has no catalog cards: the page hosts Insights there.</summary>
    public static List<UIElement> Development(App app, DevelopmentSegment segment, Action<string, string> filter) => segment switch
    {
        DevelopmentSegment.Pulls => PullsWidgets(app, filter),
        DevelopmentSegment.Commits => CommitsWidgets(app, filter),
        DevelopmentSegment.Pipelines => PipelinesWidgets(app, filter),
        DevelopmentSegment.Skills => SkillsWidgets(app.Skills?.Entries ?? Array.Empty<SkillEntry>(), app.Skills?.IsSyncing == true),
        _ => new(),
    };

    // MARK: - Pulls

    /// <summary>Open pull requests per repository, most first, top eight.</summary>
    internal static List<ChartSlice> RepositorySlices(IEnumerable<UnifiedPullRequest> prs) => TopSlices(prs, DevelopmentFilter.RepositoryKey);

    /// <summary>Age buckets on creation date, every bucket shown so the shape reads at a glance.</summary>
    internal static readonly string[] AgeBuckets = { "Today", "1–7 days", "8–30 days", "Over 30 days" };

    internal static string AgeBucket(DateTime? created, DateTime now)
    {
        if (created is not { } at) return AgeBuckets[3];
        var days = (now.Date - Local(at).Date).TotalDays;
        return days < 1 ? AgeBuckets[0] : days <= 7 ? AgeBuckets[1] : days <= 30 ? AgeBuckets[2] : AgeBuckets[3];
    }

    internal static List<ChartSlice> AgeSlices(IEnumerable<UnifiedPullRequest> prs, DateTime now)
    {
        var counts = prs.GroupBy(p => AgeBucket(p.CreatedAt, now)).ToDictionary(g => g.Key, g => g.Count());
        return AgeBuckets.Select(b => new ChartSlice(b, counts.GetValueOrDefault(b))).ToList();
    }

    private static Color AgeColor(string bucket) => Array.IndexOf(AgeBuckets, bucket) switch
    {
        0 => P.Green, 1 => P.Blue, 2 => P.Orange, _ => P.Purple,
    };

    /// <summary>Review states in precedence order: each pull request counts once, under the first that applies.</summary>
    internal static readonly string[] ReviewStates = { "Draft", "Conflicts", "Changes Requested", "Approved", "Awaiting Review" };

    internal static string ReviewState(UnifiedPullRequest pr)
    {
        if (pr.State == PullRequestState.Draft) return ReviewStates[0];
        if (pr.HasConflicts) return ReviewStates[1];
        if (pr.Reviewers.Any(r => r.Vote is PullRequestReviewVote.Rejected or PullRequestReviewVote.WaitingForAuthor)) return ReviewStates[2];
        if (pr.Reviewers.Any(r => r.Vote is PullRequestReviewVote.Approved or PullRequestReviewVote.ApprovedWithSuggestions)) return ReviewStates[3];
        return ReviewStates[4];
    }

    internal static List<ChartSlice> ReviewSlices(IEnumerable<UnifiedPullRequest> prs)
    {
        var counts = prs.GroupBy(ReviewState).ToDictionary(g => g.Key, g => g.Count());
        return ReviewStates.Where(counts.ContainsKey).Select(s => new ChartSlice(s, counts[s])).ToList();
    }

    private static Color ReviewColor(string state) => Array.IndexOf(ReviewStates, state) switch
    {
        0 => P.Gray, 1 => P.Orange, 2 => P.Yellow, 3 => P.Green, _ => P.Blue,
    };

    private static List<UIElement> PullsWidgets(App app, Action<string, string> filter)
    {
        var queue = app.DevelopmentPullRequests;
        var prs = queue?.PullRequests ?? new List<UnifiedPullRequest>();
        var loading = app.IsCacheLoading("PullRequests");
        var waiting = loading && prs.Count == 0;
        void Pulls(DevelopmentSourceFilter source) => filter(Category.Source, source.ToString());

        var cards = new List<UIElement>
        {
            WidgetCards.KpiStack(new[]
            {
                new KpiTile("Review Requested",
                    CountOrUnknown(prs.Count(p => p.Relations.Contains(PullRequestRelation.AssignedToMe)), Failed(queue)),
                    "", P.Hex(P.Purple), waiting, () => Pulls(DevelopmentSourceFilter.All)),
                new KpiTile("Created by Me",
                    CountOrUnknown(prs.Count(p => p.Relations.Contains(PullRequestRelation.CreatedByMe)), Failed(queue)),
                    "", P.Hex(P.Teal), waiting, () => Pulls(DevelopmentSourceFilter.All)),
            }),
            WidgetCards.KpiStack(new[]
            {
                new KpiTile($"{PullRequestSource.AzureDevOps.ShortName()} Pull Requests",
                    CountOrUnknown(prs.Count(p => p.Source == PullRequestSource.AzureDevOps), Failed(queue, PullRequestSource.AzureDevOps)),
                    "", P.Hex(P.Blue), waiting, () => Pulls(DevelopmentSourceFilter.DevOps)),
                new KpiTile($"{PullRequestSource.GitHub.ShortName()} Pull Requests",
                    CountOrUnknown(prs.Count(p => p.Source == PullRequestSource.GitHub), Failed(queue, PullRequestSource.GitHub)),
                    "", P.Hex(P.Indigo), waiting, () => Pulls(DevelopmentSourceFilter.GitHub)),
            }),
        };

        var weeks = PerWeek(prs.Select(p => p.CreatedAt), 12, DateTime.Now);
        cards.Add(WidgetCards.Card("Opened per Week",
            WidgetCards.ChartOr(prs.Count > 0, loading, "No open pull requests",
                () => DayColumns(weeks.Select(w => w.Start).ToList(),
                    new[] { ("Pull requests", (Brush)RepoBrushes.Accent, (IReadOnlyList<int>)weeks.Select(w => w.Count).ToList()) },
                    legend: false, bucket: RepoStatsBucket.Week)),
            units: 2, loading: loading));

        var review = ReviewSlices(prs);
        cards.Add(WidgetCards.Card("Review Status",
            WidgetCards.ChartOr(review.Count > 0, loading, "No open pull requests",
                () => WidgetCards.Donut(review, colors: review.Select(s => P.Sk(ReviewColor(s.Label))).ToList(), adaptive: true)),
            loading: loading));

        var ages = AgeSlices(prs, DateTime.Now);
        cards.Add(WidgetCards.Card("By Age",
            WidgetCards.ChartOr(prs.Count > 0, loading, "No open pull requests",
                () => WidgetCards.HorizontalBars(ages, AgeColor)),
            loading: loading));

        var byRepo = RepositorySlices(prs);
        cards.Add(WidgetCards.Card("By Repository",
            WidgetCards.ChartOr(byRepo.Count > 0, loading, "No open pull requests",
                () => WidgetCards.HorizontalBars(byRepo, WidgetCards.ByIndex(byRepo, RepositoryColors),
                    repo => filter(Category.Repository, repo))),
            loading: loading));

        var byAuthor = TopSlices(prs, p => string.IsNullOrWhiteSpace(p.AuthorName) ? "Unknown" : p.AuthorName);
        cards.Add(WidgetCards.Card("By Author",
            WidgetCards.ChartOr(byAuthor.Count > 0, loading, "No open pull requests",
                () => WidgetCards.HorizontalBars(byAuthor, _ => P.Indigo, author => filter(Category.Search, author))),
            loading: loading));

        var discussed = MostDiscussed(prs);
        cards.Add(WidgetCards.Card("Most Discussed",
            WidgetCards.ChartOr(discussed.Count > 0, loading, "No comments yet",
                () => WidgetCards.HorizontalBars(discussed.Select(p => new ChartSlice(p.Title, p.CommentCount)).ToList(), _ => P.Teal,
                    title => { if (discussed.FirstOrDefault(p => p.Title == title) is { } pr) filter(Category.PullRequest, pr.Id); })),
            loading: loading));

        return cards;
    }

    /// <summary>The eight pull requests with the most comments, busiest first, newest activity breaking ties.</summary>
    internal static List<UnifiedPullRequest> MostDiscussed(IEnumerable<UnifiedPullRequest> prs) => prs
        .Where(p => p.CommentCount > 0)
        .OrderByDescending(p => p.CommentCount).ThenByDescending(p => p.UpdatedAt ?? p.CreatedAt ?? DateTime.MinValue)
        .Take(8).ToList();

    /// <summary>The first day of the week <paramref name="date"/> falls in, in the user's calendar.</summary>
    private static DateTime WeekStart(DateTime date)
    {
        var first = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var offset = ((int)date.DayOfWeek - (int)first + 7) % 7;
        return date.Date.AddDays(-offset);
    }

    /// <summary>One entry per week for the last <paramref name="weeks"/> weeks, oldest first, empty weeks included.</summary>
    internal static List<(DateTime Start, int Count)> PerWeek(IEnumerable<DateTime?> dates, int weeks, DateTime today)
    {
        var thisWeek = WeekStart(today);
        var counts = dates.OfType<DateTime>().GroupBy(d => WeekStart(Local(d))).ToDictionary(g => g.Key, g => g.Count());
        return Enumerable.Range(0, weeks).Select(i => thisWeek.AddDays(-7 * (weeks - 1 - i)))
            .Select(w => (w, counts.GetValueOrDefault(w))).ToList();
    }

    /// <summary>Counts by weekday in the user's week order, every day present.</summary>
    internal static List<ChartSlice> Weekdays(IEnumerable<DateTime?> dates)
    {
        var format = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat;
        var counts = dates.OfType<DateTime>().GroupBy(d => Local(d).DayOfWeek).ToDictionary(g => g.Key, g => g.Count());
        return Enumerable.Range(0, 7).Select(i => (DayOfWeek)(((int)format.FirstDayOfWeek + i) % 7))
            .Select(d => new ChartSlice(format.GetAbbreviatedDayName(d), counts.GetValueOrDefault(d))).ToList();
    }

    // MARK: - Commits

    /// <summary>How many days the Commits segment loads.</summary>
    internal const int CommitDays = 14;

    /// <summary>Commits per local day, oldest first, ending today.</summary>
    internal static List<(DateTime Day, int Count)> CommitsPerDay(IEnumerable<RepositoryCommits> repos, DateTime today, int days = CommitDays)
    {
        var start = today.Date.AddDays(-(days - 1));
        var counts = repos.SelectMany(r => r.Commits)
            .Where(c => c.Date != null)
            .GroupBy(c => Local(c.Date!.Value).Date)
            .ToDictionary(g => g.Key, g => g.Count());
        return Enumerable.Range(0, days).Select(i => start.AddDays(i)).Select(d => (d, counts.GetValueOrDefault(d))).ToList();
    }

    internal static List<ChartSlice> CommitRepositorySlices(IEnumerable<RepositoryCommits> repos) => repos
        .Select(r => new ChartSlice(r.DisplayName, r.Commits.Count))
        .Where(s => s.Value > 0)
        .OrderByDescending(s => s.Value).ThenBy(s => s.Label, StringComparer.Ordinal)
        .Take(8).ToList();

    internal static List<ChartSlice> AuthorSlices(IEnumerable<RepositoryCommits> repos) =>
        TopSlices(repos.SelectMany(r => r.Commits), c => string.IsNullOrWhiteSpace(c.AuthorName) ? "Unknown" : c.AuthorName!);

    private static List<UIElement> CommitsWidgets(App app, Action<string, string> filter)
    {
        var repos = app.DevelopmentCommits ?? new List<RepositoryCommits>();
        var loading = app.IsCacheLoading("Commits");
        var waiting = loading && repos.Count == 0;
        var commits = repos.SelectMany(r => r.Commits).ToList();
        var authors = commits.Select(c => c.AuthorName).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        var cards = new List<UIElement>
        {
            WidgetCards.KpiStack(new[]
            {
                new KpiTile("Commits", commits.Count.ToString("N0"), "", P.Hex(P.Blue), waiting),
                new KpiTile("Repositories", repos.Count(r => r.Commits.Count > 0).ToString("N0"), "", P.Hex(P.Indigo), waiting),
                new KpiTile("Authors", authors.ToString("N0"), "", P.Hex(P.Teal), waiting),
            }),
        };

        var perDay = CommitsPerDay(repos, DateTime.Now);
        cards.Add(WidgetCards.Card("Commits per Day",
            WidgetCards.ChartOr(commits.Count > 0, loading, "No commits in the last 14 days",
                () => DayColumns(perDay.Select(d => d.Day).ToList(),
                    new[] { ("Commits", (Brush)RepoBrushes.Accent, (IReadOnlyList<int>)perDay.Select(d => d.Count).ToList()) }, legend: false)),
            units: 2, loading: loading));

        var byRepo = CommitRepositorySlices(repos);
        cards.Add(WidgetCards.Card("By Repository",
            WidgetCards.ChartOr(byRepo.Count > 0, loading, "No commits in the last 14 days",
                () => WidgetCards.HorizontalBars(byRepo, WidgetCards.ByIndex(byRepo, RepositoryColors),
                    repo => filter(Category.CommitRepository, repo))),
            loading: loading));

        var byAuthor = AuthorSlices(repos);
        cards.Add(WidgetCards.Card("By Author",
            WidgetCards.ChartOr(byAuthor.Count > 0, loading, "No commits in the last 14 days",
                () => WidgetCards.HorizontalBars(byAuthor, _ => P.Blue, author => filter(Category.Search, author))),
            loading: loading));

        var weekdays = Weekdays(commits.Select(c => c.Date));
        cards.Add(WidgetCards.Card("By Weekday",
            WidgetCards.ChartOr(commits.Count > 0, loading, "No commits in the last 14 days",
                () => WidgetCards.HorizontalBars(weekdays, _ => P.Indigo)),
            loading: loading));

        var sources = new[] { PullRequestSource.AzureDevOps, PullRequestSource.GitHub }
            .Select(src => (Source: src, Count: repos.Where(r => r.Source == src).Sum(r => r.Commits.Count)))
            .Where(x => x.Count > 0).ToList();
        cards.Add(WidgetCards.Card("By Source",
            WidgetCards.ChartOr(sources.Count > 0, loading, "No commits in the last 14 days",
                () => WidgetCards.Donut(sources.Select(x => new ChartSlice(x.Source.DisplayName(), x.Count)).ToList(), null,
                    sources.Select(x => P.Sk(x.Source == PullRequestSource.GitHub ? P.Indigo : P.Blue)).ToList(), adaptive: true)),
            loading: loading));

        var recent = RecentlyActive(repos);
        cards.Add(WidgetCards.Card("Recently Active",
            WidgetCards.ChartOr(recent.Count > 0, loading, "No commits in the last 14 days",
                () => WidgetCards.List("Recently Active",
                    recent.Select(r => new ListRow(r.DisplayName, RepoShell.Relative(r.Latest),
                        () => filter(Category.CommitRepository, r.DisplayName))).ToList(), visible: 8)),
            loading: loading));

        return cards;
    }

    /// <summary>Repositories by their newest commit, most recent first, top eight.</summary>
    internal static List<(string DisplayName, DateTimeOffset? Latest)> RecentlyActive(IEnumerable<RepositoryCommits> repos) => repos
        .Select(r => (r.DisplayName, Latest: r.Commits.Where(c => c.Date != null).Select(c => (DateTime?)c.Date).Max()))
        .Where(r => r.Latest != null)
        .OrderByDescending(r => r.Latest)
        .Take(8)
        .Select(r => (r.DisplayName, (DateTimeOffset?)new DateTimeOffset(Local(r.Latest!.Value))))
        .ToList();

    // MARK: - Pipelines

    internal const int RunDays = 7;

    /// <summary>Outcome groups in legend order. Skipped and unknown runs fold into Other.</summary>
    internal static readonly string[] Outcomes = { "Succeeded", "Failed", "Partially succeeded", "Cancelled", "Running", "Other" };

    internal static string Outcome(PipelineRunStatus status) => status switch
    {
        PipelineRunStatus.Succeeded => Outcomes[0],
        PipelineRunStatus.Failed => Outcomes[1],
        PipelineRunStatus.Partial => Outcomes[2],
        PipelineRunStatus.Cancelled => Outcomes[3],
        PipelineRunStatus.Running or PipelineRunStatus.Queued => Outcomes[4],
        _ => Outcomes[5],
    };

    private static Color OutcomeColor(string outcome) => Array.IndexOf(Outcomes, outcome) switch
    {
        0 => P.Green, 1 => P.Orange, 2 => P.Yellow, 4 => P.Teal, _ => P.Gray,
    };

    internal static List<ChartSlice> OutcomeSlices(IEnumerable<PipelineRun> runs)
    {
        var counts = runs.GroupBy(r => Outcome(r.Status)).ToDictionary(g => g.Key, g => g.Count());
        return Outcomes.Where(counts.ContainsKey).Select(o => new ChartSlice(o, counts[o])).ToList();
    }

    /// <summary>Succeeded over every run that reached a verdict (succeeded, failed or partial); null with none.</summary>
    internal static int? SuccessRate(IEnumerable<PipelineRun> runs)
    {
        var judged = runs.Where(r => r.Status is PipelineRunStatus.Succeeded or PipelineRunStatus.Failed or PipelineRunStatus.Partial).ToList();
        if (judged.Count == 0) return null;
        return (int)Math.Round(100.0 * judged.Count(r => r.Status == PipelineRunStatus.Succeeded) / judged.Count);
    }

    /// <summary>Runs per local day and outcome, oldest first, ending today.</summary>
    internal static (List<DateTime> Days, List<(string Outcome, List<int> Counts)> Series) RunsPerDay(IEnumerable<PipelineRun> runs, DateTime today, int days = RunDays)
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

    /// <summary>Median length in whole minutes of each pipeline's finished runs, slowest first, top eight.</summary>
    internal static List<ChartSlice> SlowestPipelines(IEnumerable<PipelineRun> runs) => runs
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
        .Take(8).ToList();

    private static List<UIElement> PipelinesWidgets(App app, Action<string, string> filter)
    {
        var runs = app.DevelopmentRuns ?? new List<PipelineRun>();
        var loading = app.IsCacheLoading("Runs");
        var waiting = loading && runs.Count == 0;
        void Status(PipelineStatusFilter status) => filter(Category.PipelineStatus, status.ToString());
        var rate = SuccessRate(runs);

        var cards = new List<UIElement>
        {
            WidgetCards.KpiStack(new[]
            {
                new KpiTile("Failing", CommitsAndPipelinesFilter.FailingCount(runs).ToString("N0"), "",
                    P.Hex(P.Orange), waiting, () => Status(PipelineStatusFilter.Failed)),
                new KpiTile("Running", runs.Count(r => r.Status.IsActive()).ToString("N0"), "",
                    P.Hex(P.Teal), waiting, () => Status(PipelineStatusFilter.Running)),
                new KpiTile("Success Rate", rate is { } r ? $"{r}%" : "--", "", P.Hex(P.Green), waiting,
                    () => Status(PipelineStatusFilter.Succeeded)),
            }),
        };

        var outcomes = OutcomeSlices(runs);
        cards.Add(WidgetCards.Card("Outcomes",
            WidgetCards.ChartOr(outcomes.Count > 0, loading, "No runs in the last 7 days",
                () => WidgetCards.Donut(outcomes, label =>
                {
                    var status = label switch
                    {
                        "Succeeded" => PipelineStatusFilter.Succeeded,
                        "Failed" => PipelineStatusFilter.Failed,
                        "Running" => PipelineStatusFilter.Running,
                        _ => (PipelineStatusFilter?)null,
                    };
                    if (status is { } s) Status(s);
                }, outcomes.Select(o => P.Sk(OutcomeColor(o.Label))).ToList(), adaptive: true)),
            loading: loading));

        var (days, series) = RunsPerDay(runs, DateTime.Now);
        cards.Add(WidgetCards.Card("Runs per Day",
            WidgetCards.ChartOr(series.Count > 0, loading, "No runs in the last 7 days",
                () => DayColumns(days, series.Select(s => (s.Outcome, (Brush)Frozen(OutcomeColor(s.Outcome)), (IReadOnlyList<int>)s.Counts)).ToList(), legend: true)),
            units: 2, loading: loading));

        var byRepo = CommitsAndPipelinesFilter.RunRepositoryCounts(runs, DevelopmentSourceFilter.All)
            .OrderByDescending(c => c.Count).ThenBy(c => c.Repository, StringComparer.Ordinal)
            .Take(8).Select(c => new ChartSlice(c.Repository, c.Count)).ToList();
        cards.Add(WidgetCards.Card("By Repository",
            WidgetCards.ChartOr(byRepo.Count > 0, loading, "No runs in the last 7 days",
                () => WidgetCards.HorizontalBars(byRepo, WidgetCards.ByIndex(byRepo, RepositoryColors),
                    repo => filter(Category.PipelineRepository, repo))),
            loading: loading));

        var failures = FailuresByPipeline(runs);
        cards.Add(WidgetCards.Card("Failures by Pipeline",
            failures.Count > 0
                ? WidgetCards.HorizontalBars(failures, _ => P.Orange, name => filter(Category.Search, name))
                : waiting ? WidgetCards.Skeleton() : WidgetCards.AllClear("No failed runs", $"{runs.Count:N0} runs in {RunDays} days"),
            loading: loading));

        var slowest = SlowestPipelines(runs);
        cards.Add(WidgetCards.Card("Slowest · median minutes",
            WidgetCards.ChartOr(slowest.Count > 0, loading, "No finished runs in the last 7 days",
                () => WidgetCards.HorizontalBars(slowest, _ => P.Indigo, name => filter(Category.Search, name))),
            loading: loading));

        var triggers = TopSlices(runs.Where(r => !string.IsNullOrWhiteSpace(r.TriggeredBy)), r => r.TriggeredBy!);
        cards.Add(WidgetCards.Card("Triggered By",
            WidgetCards.ChartOr(triggers.Count > 0, loading, "No runs in the last 7 days",
                () => WidgetCards.HorizontalBars(triggers, _ => P.Teal, who => filter(Category.Search, who))),
            loading: loading));

        return cards;
    }

    /// <summary>Failed and partially succeeded runs per pipeline, most first, top eight.</summary>
    internal static List<ChartSlice> FailuresByPipeline(IEnumerable<PipelineRun> runs) =>
        TopSlices(runs.Where(r => r.Status is PipelineRunStatus.Failed or PipelineRunStatus.Partial), r => r.PipelineName);

    // MARK: - Skills

    internal static List<ChartSlice> SkillGroupSlices(IEnumerable<SkillEntry> entries) => entries
        .GroupBy(e => (e.Kind, e.Origin))
        .OrderBy(g => g.Key.Origin).ThenBy(g => g.Key.Kind)
        .Select(g => new ChartSlice(g.First().Group, g.Count()))
        .ToList();

    internal static List<ChartSlice> MostFiles(IEnumerable<SkillEntry> entries) => entries
        .Where(e => e.Kind == SkillKind.Skill && e.Files.Count > 0)
        .Select(e => new ChartSlice(e.Name, e.Files.Count))
        .OrderByDescending(s => s.Value).ThenBy(s => s.Label, StringComparer.Ordinal)
        .Take(8).ToList();

    internal static List<ChartSlice> Longest(IEnumerable<SkillEntry> entries) => entries
        .Select(e => new ChartSlice(e.Name, e.Body.Length == 0 ? 0 : e.Body.Count(c => c == '\n') + 1))
        .Where(s => s.Value > 0)
        .OrderByDescending(s => s.Value).ThenBy(s => s.Label, StringComparer.Ordinal)
        .Take(8).ToList();

    private static List<UIElement> SkillsWidgets(IReadOnlyList<SkillEntry> entries, bool syncing)
    {
        var waiting = syncing && entries.Count == 0;
        int Count(SkillKind kind) => entries.Count(e => e.Kind == kind);
        var cards = new List<UIElement>
        {
            WidgetCards.KpiStack(new[]
            {
                new KpiTile("Skills", Count(SkillKind.Skill).ToString("N0"), "", P.Hex(P.Blue), waiting),
                new KpiTile("Hooks", Count(SkillKind.Hook).ToString("N0"), "", P.Hex(P.Purple), waiting),
                new KpiTile("Standards", Count(SkillKind.Standard).ToString("N0"), "", P.Hex(P.Teal), waiting),
            }),
        };

        var groups = SkillGroupSlices(entries);
        cards.Add(WidgetCards.Card("By Origin",
            WidgetCards.ChartOr(groups.Count > 0, syncing, "No skills yet",
                () => WidgetCards.HorizontalBars(groups, WidgetCards.ByIndex(groups, RepositoryColors))),
            loading: syncing));

        var scripts = ScriptExtensions(entries);
        cards.Add(WidgetCards.Card("Bundled Scripts",
            WidgetCards.ChartOr(scripts.Count > 0, syncing, "No bundled scripts",
                () => WidgetCards.HorizontalBars(scripts, WidgetCards.ByIndex(scripts, RepositoryColors))),
            loading: syncing));

        var files = MostFiles(entries);
        cards.Add(WidgetCards.Card("Most Bundled Files",
            WidgetCards.ChartOr(files.Count > 0, syncing, "No skill ships extra files",
                () => WidgetCards.HorizontalBars(files, _ => P.Indigo)),
            loading: syncing));

        var longest = Longest(entries);
        cards.Add(WidgetCards.Card("Longest · lines",
            WidgetCards.ChartOr(longest.Count > 0, syncing, "No skills yet",
                () => WidgetCards.HorizontalBars(longest, _ => P.Teal)),
            loading: syncing));

        var hooks = Longest(entries.Where(e => e.Kind == SkillKind.Hook));
        cards.Add(WidgetCards.Card("Hooks · lines",
            WidgetCards.ChartOr(hooks.Count > 0, syncing, "No hooks",
                () => WidgetCards.HorizontalBars(hooks, _ => P.Purple)),
            loading: syncing));

        return cards;
    }

    /// <summary>Files shipped beside skills and hooks, by extension, most first, top eight.</summary>
    internal static List<ChartSlice> ScriptExtensions(IEnumerable<SkillEntry> entries) => TopSlices(
        entries.SelectMany(e => e.Files),
        f => System.IO.Path.GetExtension(f) is { Length: > 0 } ext ? ext.ToLowerInvariant() : "no extension");

    // MARK: - Pieces

    private static List<ChartSlice> TopSlices<T>(IEnumerable<T> items, Func<T, string> key) => items
        .GroupBy(key)
        .Select(g => new ChartSlice(g.Key, g.Count()))
        .OrderByDescending(s => s.Value)
        .ThenBy(s => s.Label, StringComparer.Ordinal)
        .Take(8)
        .ToList();

    private static DateTime Local(DateTime value) => value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>Columns per day (or week), stacked when there is more than one series; hover names the period and its values. The chart takes whatever height its card has.</summary>
    private static UIElement DayColumns(IReadOnlyList<DateTime> days, IReadOnlyList<(string Name, Brush Brush, IReadOnlyList<int> Values)> series,
        bool legend, RepoStatsBucket bucket = RepoStatsBucket.Day)
    {
        var chart = new TimelineChart { MinHeight = 130 };
        chart.ShowStacked(days.Select(d => new DateTimeOffset(d)).ToList(), series, bucket);
        if (!legend) return chart;
        var key = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (name, brush, _) in series)
        {
            key.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = brush, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            key.Children.Add(new TextBlock { Text = name, FontSize = WidgetCards.CaptionSize, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
        }
        var panel = new DockPanel();
        DockPanel.SetDock(key, Dock.Bottom);
        panel.Children.Add(key);
        panel.Children.Add(chart);
        return panel;
    }
}
