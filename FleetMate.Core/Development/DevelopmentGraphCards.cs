using System.Globalization;
using FleetMate.Core.Knowledge;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Services.Repos;
using FleetMate.Core.Shared;

namespace FleetMate.Core.Development;

/// <summary>
/// A named chart colour, matching the macOS app's palette (no red). Each app
/// maps it to its own theme brush, so Light, Dark and High Contrast each get theirs.
/// </summary>
public enum ChartTone { Blue, Green, Orange, Purple, Teal, Brown, Indigo, Pink, Gray, Yellow }

/// <summary>The filter categories a Development graph click hands the tab, as the macOS module filters name them.</summary>
public static class GraphCategory
{
    /// <summary>Open a segment (<see cref="InboxSegment"/>).</summary>
    public const string Segment = "Segment";
    /// <summary>Open Pulls filtered to a repository.</summary>
    public const string Repository = "Repository";
    /// <summary>Open Pulls filtered to a source (a <see cref="DevelopmentSourceFilter"/> name).</summary>
    public const string Source = "Source";
    /// <summary>Open Pipelines filtered to a status (a <see cref="PipelineStatusFilter"/> name).</summary>
    public const string PipelineStatus = "PipelineStatus";
    /// <summary>Open Commits filtered to a repository.</summary>
    public const string CommitRepository = "CommitRepository";
    /// <summary>Open Pipelines filtered to a repository.</summary>
    public const string PipelineRepository = "PipelineRepository";
    /// <summary>Type a value into the open sub-tab's search field (an author, a pipeline).</summary>
    public const string Search = "Search";
    /// <summary>Open one pull request in Pulls (its id).</summary>
    public const string PullRequest = "PullRequest";

    public const string InboxSegment = "Inbox";
}

/// <summary>What a click on a figure opens: a filter category and its value.</summary>
public sealed record GraphFilter(string Category, string Value)
{
    /// <summary>A filter on <paramref name="label"/>, cut at any trailing "(count)" (<see cref="ChartSlice.FilterValue"/>).</summary>
    public static GraphFilter For(string category, string label) => new(category, ChartSlice.FilterValue(label));
}

/// <summary>One KPI tile: a big number with a label. <see cref="Loading"/> shows a placeholder; <see cref="Open"/> makes it a button.</summary>
public sealed record GraphKpi(string Title, string Value, ChartTone Tone, bool Loading, GraphFilter? Open)
{
    public string Description => $"{Title}: {Value}";
}

/// <summary>
/// One bar or donut slice. A null <see cref="Tone"/> takes the app's
/// categorical colour for the bar's position. <see cref="Description"/> is
/// its accessible name: "owner/repo, 4 open pull requests".
/// </summary>
public sealed record GraphBar(string Label, int Value, ChartTone? Tone, GraphFilter? Open, string Description);

/// <summary>One stacked series of a column chart.</summary>
public sealed record GraphSeries(string Name, ChartTone? Tone, IReadOnlyList<int> Values);

/// <summary>One column of a column chart: its period, a short axis label and the accessible description.</summary>
public sealed record GraphColumn(DateTime Start, string Axis, string Description);

/// <summary>One row of a list card.</summary>
public sealed record GraphListRow(string Title, string Detail, GraphFilter? Open);

public enum GraphCardKind { Kpis, Donut, Bars, Columns, List }

/// <summary>What a card's body shows: its chart, a loading placeholder, an empty message or an all-clear.</summary>
public enum GraphCardBody { Chart, Loading, Empty, AllClear }

/// <summary>
/// One card of the strip, ready for either app to draw: the kind of chart,
/// its figures, what each click opens and its loading and empty states.
/// </summary>
public sealed record GraphCard(GraphCardKind Kind, string Title)
{
    /// <summary>Units of the row it spans (<see cref="WidgetRowPacking"/>).</summary>
    public int Units { get; init; } = 1;
    /// <summary>True while its data is loading: a spinner beside the title.</summary>
    public bool Loading { get; init; }
    public GraphCardBody Body { get; init; } = GraphCardBody.Chart;
    /// <summary>The empty message, or the all-clear title.</summary>
    public string Message { get; init; } = "";
    /// <summary>The all-clear's second line.</summary>
    public string Detail { get; init; } = "";
    public IReadOnlyList<GraphKpi> Kpis { get; init; } = Array.Empty<GraphKpi>();
    public IReadOnlyList<GraphBar> Bars { get; init; } = Array.Empty<GraphBar>();
    public IReadOnlyList<GraphColumn> Columns { get; init; } = Array.Empty<GraphColumn>();
    public IReadOnlyList<GraphSeries> Series { get; init; } = Array.Empty<GraphSeries>();
    public RepoStatsBucket Bucket { get; init; } = RepoStatsBucket.Day;
    /// <summary>Whether a column chart names its series under it.</summary>
    public bool Legend { get; init; }
    public IReadOnlyList<GraphListRow> Rows { get; init; } = Array.Empty<GraphListRow>();
    /// <summary>A line under the chart, such as Projects' current sprint; empty for none.</summary>
    public string Caption { get; init; } = "";
    /// <summary>How many list rows show before "Show all".</summary>
    public int VisibleRows { get; init; } = DevelopmentFigures.TopCount;

    /// <summary>The card's accessible name: its title and what it says.</summary>
    public string Description => Body switch
    {
        GraphCardBody.Loading => $"{Title}: loading",
        GraphCardBody.Empty => $"{Title}: {Message}",
        GraphCardBody.AllClear => $"{Title}: {Message}, {Detail}",
        _ => Title,
    };
}

/// <summary>
/// Development's Graphs strip, one set of cards per sub-tab, matching the
/// macOS app. Every figure comes from what the tab has already loaded
/// (<see cref="DevelopmentFigures"/>). Repos has no cards here: each app hosts
/// the repository Insights there.
/// </summary>
public static class DevelopmentGraphCards
{
    /// <summary>The cache keys whose change redraws <paramref name="segment"/>'s cards.</summary>
    public static bool Follows(DevelopmentSegment segment, string cacheKey) => segment switch
    {
        DevelopmentSegment.Pulls => cacheKey == "PullRequests",
        DevelopmentSegment.Commits => cacheKey == "Commits",
        DevelopmentSegment.Pipelines => cacheKey == "Runs",
        _ => false,
    };

    private static readonly ChartTone[] AgeTones = { ChartTone.Green, ChartTone.Blue, ChartTone.Orange, ChartTone.Purple };
    private static readonly ChartTone[] ReviewTones = { ChartTone.Gray, ChartTone.Orange, ChartTone.Yellow, ChartTone.Green, ChartTone.Blue };

    public static ChartTone OutcomeTone(string outcome) => Array.IndexOf(DevelopmentFigures.Outcomes, outcome) switch
    {
        0 => ChartTone.Green, 1 => ChartTone.Orange, 2 => ChartTone.Yellow, 4 => ChartTone.Teal, _ => ChartTone.Gray,
    };

    // MARK: - Pulls

    public static List<GraphCard> Pulls(PullRequestQueue? queue, bool loading, DateTime now)
    {
        var prs = queue?.PullRequests ?? new List<UnifiedPullRequest>();
        var waiting = loading && prs.Count == 0;
        const string none = "No open pull requests";
        static GraphFilter Source(DevelopmentSourceFilter source) => new(GraphCategory.Source, source.ToString());
        string Count(Func<UnifiedPullRequest, bool> match, PullRequestSource? source = null) =>
            DevelopmentFigures.CountOrUnknown(prs.Count(match), DevelopmentFigures.Failed(queue, source));

        var cards = new List<GraphCard>
        {
            Kpis(
                new GraphKpi("Review Requested", Count(p => p.Relations.Contains(PullRequestRelation.AssignedToMe)),
                    ChartTone.Purple, waiting, Source(DevelopmentSourceFilter.All)),
                new GraphKpi("Created by Me", Count(p => p.Relations.Contains(PullRequestRelation.CreatedByMe)),
                    ChartTone.Teal, waiting, Source(DevelopmentSourceFilter.All))),
            Kpis(
                new GraphKpi($"{PullRequestSource.AzureDevOps.ShortName()} Pull Requests",
                    Count(p => p.Source == PullRequestSource.AzureDevOps, PullRequestSource.AzureDevOps),
                    ChartTone.Blue, waiting, Source(DevelopmentSourceFilter.DevOps)),
                new GraphKpi($"{PullRequestSource.GitHub.ShortName()} Pull Requests",
                    Count(p => p.Source == PullRequestSource.GitHub, PullRequestSource.GitHub),
                    ChartTone.Indigo, waiting, Source(DevelopmentSourceFilter.GitHub))),
        };

        var weeks = DevelopmentFigures.PerWeek(prs.Select(p => p.CreatedAt), 12, now);
        cards.Add(Columns("Opened per Week", prs.Count > 0, loading, none,
            weeks.Select(w => w.Start).ToList(), RepoStatsBucket.Week,
            new[] { new GraphSeries("Pull requests", ChartTone.Blue, weeks.Select(w => w.Count).ToList()) },
            legend: false, "pull request opened", "pull requests opened"));

        var review = DevelopmentFigures.ReviewSlices(prs);
        cards.Add(Chart(GraphCardKind.Donut, "Review Status", review.Count > 0, loading, none,
            Bars(review, s => ReviewTones[Math.Max(0, Array.IndexOf(DevelopmentFigures.ReviewStates, s.Label))], null, "pull request")));

        var ages = DevelopmentFigures.AgeSlices(prs, now);
        cards.Add(Chart(GraphCardKind.Bars, "By Age", prs.Count > 0, loading, none,
            Bars(ages, s => AgeTones[Math.Max(0, Array.IndexOf(DevelopmentFigures.AgeBuckets, s.Label))], null, "open pull request")));

        var byRepo = DevelopmentFigures.RepositorySlices(prs);
        cards.Add(Chart(GraphCardKind.Bars, "By Repository", byRepo.Count > 0, loading, none,
            Bars(byRepo, _ => null, s => GraphFilter.For(GraphCategory.Repository, s.Label), "open pull request")));

        var byAuthor = DevelopmentFigures.PullAuthorSlices(prs);
        cards.Add(Chart(GraphCardKind.Bars, "By Author", byAuthor.Count > 0, loading, none,
            Bars(byAuthor, _ => ChartTone.Indigo, s => GraphFilter.For(GraphCategory.Search, s.Label), "open pull request")));

        var discussed = DevelopmentFigures.MostDiscussed(prs);
        cards.Add(Chart(GraphCardKind.Bars, "Most Discussed", discussed.Count > 0, loading, "No comments yet",
            discussed.Select(p => new GraphBar(p.Title, p.CommentCount, ChartTone.Teal,
                new GraphFilter(GraphCategory.PullRequest, p.Id), Describe(p.Title, p.CommentCount, "comment"))).ToList()));

        return cards;
    }

    // MARK: - Commits

    public static List<GraphCard> Commits(IReadOnlyList<RepositoryCommits>? repos, bool loading, DateTime now)
    {
        repos ??= Array.Empty<RepositoryCommits>();
        var waiting = loading && repos.Count == 0;
        var total = repos.Sum(r => r.Commits.Count);
        var none = $"No commits in the last {DevelopmentFigures.CommitDays} days";

        var cards = new List<GraphCard>
        {
            Kpis(
                new GraphKpi("Commits", total.ToString("N0"), ChartTone.Blue, waiting, null),
                new GraphKpi("Repositories", repos.Count(r => r.Commits.Count > 0).ToString("N0"), ChartTone.Indigo, waiting, null),
                new GraphKpi("Authors", DevelopmentFigures.AuthorCount(repos).ToString("N0"), ChartTone.Teal, waiting, null)),
        };

        var perDay = DevelopmentFigures.CommitsPerDay(repos, now);
        cards.Add(Columns("Commits per Day", total > 0, loading, none,
            perDay.Select(d => d.Day).ToList(), RepoStatsBucket.Day,
            new[] { new GraphSeries("Commits", ChartTone.Blue, perDay.Select(d => d.Count).ToList()) },
            legend: false, "commit", "commits"));

        var byRepo = DevelopmentFigures.CommitRepositorySlices(repos);
        cards.Add(Chart(GraphCardKind.Bars, "By Repository", byRepo.Count > 0, loading, none,
            Bars(byRepo, _ => null, s => GraphFilter.For(GraphCategory.CommitRepository, s.Label), "commit")));

        var byAuthor = DevelopmentFigures.AuthorSlices(repos);
        cards.Add(Chart(GraphCardKind.Bars, "By Author", byAuthor.Count > 0, loading, none,
            Bars(byAuthor, _ => ChartTone.Blue, s => GraphFilter.For(GraphCategory.Search, s.Label), "commit")));

        var weekdays = DevelopmentFigures.Weekdays(repos.SelectMany(r => r.Commits).Select(c => c.Date));
        cards.Add(Chart(GraphCardKind.Bars, "By Weekday", total > 0, loading, none,
            Bars(weekdays, _ => ChartTone.Indigo, null, "commit")));

        var sources = DevelopmentFigures.CommitSources(repos);
        cards.Add(Chart(GraphCardKind.Donut, "By Source", sources.Count > 0, loading, none,
            sources.Select(x => new GraphBar(x.Source.DisplayName(), x.Count,
                x.Source == PullRequestSource.GitHub ? ChartTone.Indigo : ChartTone.Blue, null,
                Describe(x.Source.DisplayName(), x.Count, "commit"))).ToList()));

        var recent = DevelopmentFigures.RecentlyActive(repos);
        var nowOffset = new DateTimeOffset(now);
        cards.Add(Chart(GraphCardKind.List, "Recently Active", recent.Count > 0, loading, none, Array.Empty<GraphBar>()) with
        {
            Rows = recent.Select(r => new GraphListRow(r.DisplayName, DevelopmentFigures.Relative(r.Latest, nowOffset),
                GraphFilter.For(GraphCategory.CommitRepository, r.DisplayName))).ToList(),
        });

        return cards;
    }

    // MARK: - Pipelines

    public static List<GraphCard> Pipelines(IReadOnlyList<PipelineRun>? runs, bool loading, DateTime now)
    {
        runs ??= Array.Empty<PipelineRun>();
        var waiting = loading && runs.Count == 0;
        var none = $"No runs in the last {DevelopmentFigures.RunDays} days";
        static GraphFilter Status(PipelineStatusFilter status) => new(GraphCategory.PipelineStatus, status.ToString());
        var rate = DevelopmentFigures.SuccessRate(runs);

        var cards = new List<GraphCard>
        {
            Kpis(
                new GraphKpi("Failing", CommitsAndPipelinesFilter.FailingCount(runs).ToString("N0"), ChartTone.Orange, waiting,
                    Status(PipelineStatusFilter.Failed)),
                new GraphKpi("Running", runs.Count(r => r.Status.IsActive()).ToString("N0"), ChartTone.Teal, waiting,
                    Status(PipelineStatusFilter.Running)),
                new GraphKpi("Success Rate", rate is { } r ? $"{r}%" : "--", ChartTone.Green, waiting,
                    Status(PipelineStatusFilter.Succeeded))),
        };

        var outcomes = DevelopmentFigures.OutcomeSlices(runs);
        cards.Add(Chart(GraphCardKind.Donut, "Outcomes", outcomes.Count > 0, loading, none,
            Bars(outcomes, s => OutcomeTone(s.Label),
                s => DevelopmentFigures.OutcomeFilter(s.Label) is { } f ? Status(f) : null, "run")));

        var (days, series) = DevelopmentFigures.RunsPerDay(runs, now);
        cards.Add(Columns("Runs per Day", series.Count > 0, loading, none, days, RepoStatsBucket.Day,
            series.Select(s => new GraphSeries(s.Outcome, OutcomeTone(s.Outcome), s.Counts)).ToList(),
            legend: true, "run", "runs"));

        var byRepo = DevelopmentFigures.RunRepositorySlices(runs);
        cards.Add(Chart(GraphCardKind.Bars, "By Repository", byRepo.Count > 0, loading, none,
            Bars(byRepo, _ => null, s => GraphFilter.For(GraphCategory.PipelineRepository, s.Label), "run")));

        var failures = DevelopmentFigures.FailuresByPipeline(runs);
        var failureCard = new GraphCard(GraphCardKind.Bars, "Failures by Pipeline")
        {
            Loading = loading,
            Bars = Bars(failures, _ => ChartTone.Orange, s => GraphFilter.For(GraphCategory.Search, s.Label), "failed run"),
        };
        cards.Add(failures.Count > 0 ? failureCard
            : waiting ? failureCard with { Body = GraphCardBody.Loading }
            : failureCard with
            {
                Body = GraphCardBody.AllClear,
                Message = "No failed runs",
                Detail = $"{runs.Count:N0} runs in {DevelopmentFigures.RunDays} days",
            });

        var slowest = DevelopmentFigures.SlowestPipelines(runs);
        cards.Add(Chart(GraphCardKind.Bars, "Slowest · median minutes", slowest.Count > 0, loading,
            $"No finished runs in the last {DevelopmentFigures.RunDays} days",
            Bars(slowest, _ => ChartTone.Indigo, s => GraphFilter.For(GraphCategory.Search, s.Label), "minute median")));

        var triggers = DevelopmentFigures.TriggerSlices(runs);
        cards.Add(Chart(GraphCardKind.Bars, "Triggered By", triggers.Count > 0, loading, none,
            Bars(triggers, _ => ChartTone.Teal, s => GraphFilter.For(GraphCategory.Search, s.Label), "run")));

        return cards;
    }

    // MARK: - Skills

    public static List<GraphCard> Skills(IReadOnlyList<SkillEntry>? entries, bool syncing)
    {
        entries ??= Array.Empty<SkillEntry>();
        var waiting = syncing && entries.Count == 0;
        string Count(SkillKind kind) => entries.Count(e => e.Kind == kind).ToString("N0");

        var cards = new List<GraphCard>
        {
            Kpis(
                new GraphKpi("Skills", Count(SkillKind.Skill), ChartTone.Blue, waiting, null),
                new GraphKpi("Hooks", Count(SkillKind.Hook), ChartTone.Purple, waiting, null),
                new GraphKpi("Standards", Count(SkillKind.Standard), ChartTone.Teal, waiting, null)),
        };

        var groups = DevelopmentFigures.SkillGroupSlices(entries);
        cards.Add(Chart(GraphCardKind.Bars, "By Origin", groups.Count > 0, syncing, "No skills yet",
            Bars(groups, _ => null, null, "entry", "entries")));

        var scripts = DevelopmentFigures.ScriptExtensions(entries);
        cards.Add(Chart(GraphCardKind.Bars, "Bundled Scripts", scripts.Count > 0, syncing, "No bundled scripts",
            Bars(scripts, _ => null, null, "file")));

        var files = DevelopmentFigures.MostFiles(entries);
        cards.Add(Chart(GraphCardKind.Bars, "Most Bundled Files", files.Count > 0, syncing, "No skill ships extra files",
            Bars(files, _ => ChartTone.Indigo, null, "file")));

        var longest = DevelopmentFigures.Longest(entries);
        cards.Add(Chart(GraphCardKind.Bars, "Longest · lines", longest.Count > 0, syncing, "No skills yet",
            Bars(longest, _ => ChartTone.Teal, null, "line")));

        var hooks = DevelopmentFigures.Longest(entries.Where(e => e.Kind == SkillKind.Hook));
        cards.Add(Chart(GraphCardKind.Bars, "Hooks · lines", hooks.Count > 0, syncing, "No hooks",
            Bars(hooks, _ => ChartTone.Purple, null, "line")));

        return cards;
    }

    // MARK: - Pieces

    /// <summary>"owner/repo, 4 open pull requests".</summary>
    public static string Describe(string label, int value, string singular, string? plural = null) =>
        $"{label}, {PipelineAvailability.Count(value, singular, plural)}";

    private static GraphCard Kpis(params GraphKpi[] kpis) => new(GraphCardKind.Kpis, "") { Kpis = kpis };

    private static List<GraphBar> Bars(IEnumerable<ChartSlice> slices, Func<ChartSlice, ChartTone?> tone,
        Func<ChartSlice, GraphFilter?>? open, string singular, string? plural = null) =>
        slices.Select(s => new GraphBar(s.Label, s.Value, tone(s), open?.Invoke(s), Describe(s.Label, s.Value, singular, plural))).ToList();

    /// <summary>A chart card's body: the chart when there is data, a placeholder while it loads, the empty message otherwise.</summary>
    private static GraphCard Chart(GraphCardKind kind, string title, bool hasData, bool loading, string empty, IReadOnlyList<GraphBar> bars) =>
        new(kind, title)
        {
            Loading = loading,
            Body = hasData ? GraphCardBody.Chart : loading ? GraphCardBody.Loading : GraphCardBody.Empty,
            Message = hasData ? "" : empty,
            Bars = bars,
        };

    private static GraphCard Columns(string title, bool hasData, bool loading, string empty, IReadOnlyList<DateTime> starts,
        RepoStatsBucket bucket, IReadOnlyList<GraphSeries> series, bool legend, string singular, string plural)
    {
        var columns = starts.Select((start, i) =>
        {
            var period = bucket == RepoStatsBucket.Week
                ? $"Week of {start.ToString("d MMMM", CultureInfo.CurrentCulture)}"
                : start.ToString("dddd d MMMM", CultureInfo.CurrentCulture);
            var values = series.Count == 1
                ? PipelineAvailability.Count(series[0].Values[i], singular, plural)
                : string.Join(", ", series.Select(s => $"{s.Name} {s.Values[i]:N0}"));
            return new GraphColumn(start, start.ToString("d MMM", CultureInfo.CurrentCulture), $"{period}: {values}");
        }).ToList();
        return Chart(GraphCardKind.Columns, title, hasData, loading, empty, Array.Empty<GraphBar>()) with
        {
            Units = 2,
            Columns = columns,
            Series = series,
            Bucket = bucket,
            Legend = legend,
        };
    }
}
