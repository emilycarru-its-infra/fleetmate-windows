using FleetMate.Core.Development;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Shared;

namespace FleetMate.Core.Models.Projects;

/// <summary>Everything the Projects strip draws from: the signed-in user's open items and what surrounds them.</summary>
public sealed record ProjectsFigures(
    IReadOnlyList<WorkItem> Mine,
    bool MineLoading,
    IReadOnlyList<WorkItem> InitiativeAncestors,
    IReadOnlyList<Sprint> Sprints,
    IReadOnlyList<GitHubIssueSummary>? Issues,
    bool IssuesLoading,
    ProjectsTile? Selected,
    string? OrganizationUrl);

/// <summary>
/// The Projects widgets as the shared strip's <see cref="GraphCard"/>s,
/// matching the WPF strip and the Mac: the eight figures (each a filter on
/// the page's lists), By State with the current sprint, By Type, By
/// Initiative, and the Work Items and GitHub Issues lists.
/// </summary>
public static class ProjectsGraphCards
{
    /// <summary>A figure's click: its value is the <see cref="ProjectsTile"/> name.</summary>
    public const string Tile = "ProjectsTile";

    /// <summary>A list row's click: its value is the web address to open.</summary>
    public const string Url = "ProjectsUrl";

    /// <summary>States that take a work item off the open list; Resolved counts, since only closing is left.</summary>
    private static readonly HashSet<string> FinishedStates = new(AzureDevOpsService.FinishedStates, StringComparer.OrdinalIgnoreCase);

    public static bool IsClosed(string? state) => state is not null && FinishedStates.Contains(state);

    /// <summary>The user's open items by state, most first, each labelled "State (count)".</summary>
    public static List<ChartSlice> WorkItemSlices(IEnumerable<WorkItem> items) => items
        .GroupBy(w => string.IsNullOrEmpty(w.State) ? "Unknown" : w.State)
        .Select(g => (State: g.Key, Count: g.Count()))
        .OrderByDescending(s => s.Count)
        .Select(s => new ChartSlice($"{s.State} ({s.Count})", s.Count))
        .ToList();

    /// <summary>"Sprint: Name · N open" — the user's open items in the current sprint.</summary>
    public static string SprintCaption(string sprintName, IEnumerable<WorkItem> items) =>
        $"Sprint: {sprintName} · {items.Count(w => w.IterationPath?.EndsWith(sprintName, StringComparison.Ordinal) == true)} open";

    private static readonly ChartTone[] StateTones = { ChartTone.Blue, ChartTone.Green, ChartTone.Orange, ChartTone.Purple, ChartTone.Gray, ChartTone.Brown };

    /// <summary>The figures with their tone and help, in the strip's order.</summary>
    public static readonly IReadOnlyList<(ProjectsTile Tile, ChartTone Tone, string Help)> Figures = new[]
    {
        (ProjectsTile.AssignedToMe, ChartTone.Indigo, "Your open work items"),
        (ProjectsTile.InProgress, ChartTone.Blue, "Open and past New"),
        (ProjectsTile.NewThisWeek, ChartTone.Green, "Created in the last 7 days"),
        (ProjectsTile.UpdatedThisWeek, ChartTone.Teal, "Changed in the last 7 days"),
        (ProjectsTile.Blocked, ChartTone.Orange, "Tagged Blocked, or on a Blocked column or state"),
        (ProjectsTile.Overdue, ChartTone.Purple, "Past their due date"),
        (ProjectsTile.Stale, ChartTone.Gray, $"Not changed in {WorkItemStats.StaleDays} days"),
        (ProjectsTile.GitHubIssues, ChartTone.Indigo, "Open GitHub issues in your list"),
    };

    public static List<GraphCard> For(ProjectsFigures f, DateTime nowUtc)
    {
        var stats = WorkItemStats.From(f.Mine, nowUtc);
        var waiting = f.MineLoading && f.Mine.Count == 0;
        int Value(ProjectsTile tile) => tile switch
        {
            ProjectsTile.AssignedToMe => stats.Open,
            ProjectsTile.InProgress => stats.InProgress,
            ProjectsTile.NewThisWeek => stats.NewThisWeek,
            ProjectsTile.UpdatedThisWeek => stats.UpdatedThisWeek,
            ProjectsTile.Blocked => stats.Blocked,
            ProjectsTile.Overdue => stats.Overdue,
            ProjectsTile.Stale => stats.Stale,
            _ => f.Issues?.Count ?? 0,
        };
        GraphKpi Kpi((ProjectsTile Tile, ChartTone Tone, string Help) figure)
        {
            var title = ProjectsTileFilter.Title(figure.Tile);
            var loading = figure.Tile == ProjectsTile.GitHubIssues ? f.Issues == null && f.IssuesLoading : waiting;
            return new GraphKpi(f.Selected == figure.Tile ? $"{title} (on)" : title, Value(figure.Tile).ToString("N0"),
                figure.Tone, loading, new GraphFilter(Tile, figure.Tile.ToString()));
        }

        // Eight figures read best as two columns of four beside the charts.
        var cards = new List<GraphCard>
        {
            new(GraphCardKind.Kpis, "Your Items") { Loading = waiting, Kpis = Figures.Take(4).Select(Kpi).ToList() },
            new(GraphCardKind.Kpis, "Needs Attention") { Loading = waiting, Kpis = Figures.Skip(4).Select(Kpi).ToList() },
        };

        GraphCardBody Body(int count) => count > 0 ? GraphCardBody.Chart : f.MineLoading ? GraphCardBody.Loading : GraphCardBody.Empty;

        var slices = WorkItemSlices(f.Mine);
        var sprint = f.Sprints.FirstOrDefault(s => s.IsCurrent);
        cards.Add(new GraphCard(GraphCardKind.Donut, "By State")
        {
            Loading = f.MineLoading,
            Body = Body(slices.Count),
            Message = "No work item data",
            Caption = sprint == null ? "" : SprintCaption(string.IsNullOrEmpty(sprint.Name) ? "Current" : sprint.Name, f.Mine),
            Bars = slices.Select((s, i) => new GraphBar(s.Label, (int)s.Value, StateTones[i % StateTones.Length], null,
                $"By State, {s.Label}")).ToList(),
        });

        var byType = WorkItemStats.Ranked(f.Mine, w => w.WorkItemType).Take(8).ToList();
        cards.Add(new GraphCard(GraphCardKind.Bars, "By Type")
        {
            Loading = f.MineLoading,
            Body = Body(byType.Count),
            Message = "No work item data",
            Bars = byType.Select(r => new GraphBar(r.Label, r.Count, ChartTone.Indigo, null, $"By Type, {r.Label}: {r.Count}")).ToList(),
        });

        var byInitiative = WorkItemStats.Initiatives(f.Mine, f.InitiativeAncestors);
        cards.Add(new GraphCard(GraphCardKind.Bars, "By Initiative")
        {
            Loading = f.MineLoading,
            Body = Body(byInitiative.Count),
            Message = "No work item data",
            Bars = byInitiative.Select(r => new GraphBar(r.Label, r.Count, ChartTone.Teal, null, $"By Initiative, {r.Label}: {r.Count}")).ToList(),
        });

        var root = f.OrganizationUrl?.TrimEnd('/');
        cards.Add(new GraphCard(GraphCardKind.List, "Work Items List")
        {
            Units = 2,
            Loading = f.MineLoading,
            Body = f.Mine.Count > 0 ? GraphCardBody.Chart : f.MineLoading ? GraphCardBody.Loading : GraphCardBody.Empty,
            Message = "No open work items",
            Rows = f.Mine.Select(w => new GraphListRow($"#{w.Id}  {w.Title}", $"{w.State} · {w.WorkItemType}",
                root == null ? null : new GraphFilter(Url, $"{root}/_workitems/edit/{w.Id}"))).ToList(),
        });

        var issues = f.Issues ?? Array.Empty<GitHubIssueSummary>();
        cards.Add(new GraphCard(GraphCardKind.List, "GitHub Issues")
        {
            Units = 2,
            Loading = f.IssuesLoading,
            Body = issues.Count > 0 ? GraphCardBody.Chart : f.IssuesLoading ? GraphCardBody.Loading : GraphCardBody.Empty,
            Message = "No open GitHub issues",
            Rows = issues.Select(i => new GraphListRow($"#{i.Number}  {i.Title}", i.Repository,
                string.IsNullOrEmpty(i.WebUrl) ? null : new GraphFilter(Url, i.WebUrl))).ToList(),
        });
        return cards;
    }

    /// <summary>
    /// Act on a click: a figure toggles its tile on <paramref name="tiles"/>;
    /// a list row returns its address for the caller to open. Null when there
    /// is nothing to open.
    /// </summary>
    public static string? Apply(GraphFilter filter, ProjectsTileFilter tiles)
    {
        if (filter.Category == Tile && Enum.TryParse<ProjectsTile>(filter.Value, out var tile))
        {
            tiles.Toggle(tile);
            return null;
        }
        return filter.Category == Url ? filter.Value : null;
    }
}
