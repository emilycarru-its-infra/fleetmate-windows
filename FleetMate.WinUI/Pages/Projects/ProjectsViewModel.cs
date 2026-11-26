using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FleetMate.Core.Models.Projects;
using FleetMate.WinUI.Shell;
using Microsoft.UI.Xaml;

namespace FleetMate.WinUI.Pages.Projects;

/// <summary>Mine | Queries | Recent | Board, matching the macOS Projects views.</summary>
public enum ProjectsView { Mine, Queries, Recent, Board }

/// <summary>
/// The Projects tab's state: which view shows, its filters, and the columns
/// or rows it draws from <see cref="ShellServices"/>' caches. The rules are
/// FleetMate.Core's (<see cref="TaskBoard"/>, <see cref="GitHubProjectBoard"/>,
/// <see cref="QueryRuns"/>); this holds no WinUI control.
/// </summary>
public sealed partial class ProjectsViewModel : ObservableObject
{
    private readonly ShellServices _services;
    private HashSet<int> _tileIds = new();

    public ProjectsViewModel(ShellServices services)
    {
        _services = services;
        Buckets = new ObservableCollection<string>(services.ProjectBuckets);
    }

    public IReadOnlyList<string> GroupByOptions => TaskBoard.GroupByOptions;

    public IReadOnlyList<string> ProviderOptions { get; } = new[] { "All Providers", "Azure DevOps", "GitHub", "Gitea" };

    public IReadOnlyList<string> BoardSources { get; } = new[] { "Azure DevOps", "GitHub Project" };

    public ObservableCollection<string> Buckets { get; }

    public ObservableCollection<BoardColumnItem> Columns { get; } = new();

    public ObservableCollection<TaskCardItem> FlatRows { get; } = new();

    public ObservableCollection<GitHubColumnItem> GitHubColumns { get; } = new();

    public ObservableCollection<WorkItemRowItem> WorkItemRows { get; } = new();

    [ObservableProperty] public partial ProjectsView View { get; set; } = ProjectsView.Queries;
    [ObservableProperty] public partial string GroupBy { get; set; } = "State";
    [ObservableProperty] public partial bool ShowClosed { get; set; }
    [ObservableProperty] public partial string Bucket { get; set; } = TaskBoard.AllBuckets;
    [ObservableProperty] public partial string Provider { get; set; } = "All Providers";
    [ObservableProperty] public partial string BoardSource { get; set; } = "Azure DevOps";
    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial string CountLabel { get; set; } = "";
    [ObservableProperty] public partial string EmptyText { get; set; } = "";
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string? TileFilterLabel { get; set; }

    public bool ShowsGitHubBoard => View == ProjectsView.Board && BoardSource == "GitHub Project";
    /// <summary>The GitHub Project board has no provider, bucket or closed filter.</summary>
    public bool ShowsFilters => !ShowsGitHubBoard;
    public bool ShowsTaskBoard => View == ProjectsView.Board && !ShowsGitHubBoard;
    public bool ShowsFlat => View is ProjectsView.Mine or ProjectsView.Recent;
    public bool ShowsQueries => View == ProjectsView.Queries && _services.UsesStoredQueries;
    public bool ShowsWorkItemList => View == ProjectsView.Queries && !_services.UsesStoredQueries;

    /// <summary>The provider filter as the registry names it, or null for every provider.</summary>
    public string? ProviderId => Provider switch
    {
        "Azure DevOps" => "azdevops",
        "GitHub" => "github",
        "Gitea" => "gitea",
        _ => null,
    };

    partial void OnViewChanged(ProjectsView value) => RaiseViews();
    partial void OnBoardSourceChanged(string value) => RaiseViews();
    partial void OnGroupByChanged(string value) => Rebuild();
    partial void OnShowClosedChanged(bool value) => Rebuild();
    partial void OnBucketChanged(string value) => Rebuild();
    partial void OnSearchChanged(string value) => Rebuild();

    private void RaiseViews()
    {
        OnPropertyChanged(nameof(ShowsGitHubBoard));
        OnPropertyChanged(nameof(ShowsTaskBoard));
        OnPropertyChanged(nameof(ShowsFilters));
        OnPropertyChanged(nameof(ShowsFlat));
        OnPropertyChanged(nameof(ShowsQueries));
        OnPropertyChanged(nameof(ShowsWorkItemList));
        Rebuild();
    }

    /// <summary>Whether a task is in the set the picked strip tile counts.</summary>
    public bool TilePasses(UnifiedTask task) =>
        ProjectsTileFilter.Passes(task, ProjectsTileFilter.Shared.Selected, _tileIds);

    /// <summary>The strip tile changed, or the user's items it counts reloaded.</summary>
    public void RefreshTile()
    {
        var tile = ProjectsTileFilter.Shared.Selected;
        _tileIds = tile is { } t
            ? ProjectsTileFilter.Ids(_services.MyWorkItems ?? new List<WorkItem>(), t, DateTime.UtcNow)
            : new HashSet<int>();
        TileFilterLabel = tile is { } picked ? $"Showing {ProjectsTileFilter.Title(picked)}" : null;
        Rebuild();
    }

    public void SyncBuckets()
    {
        if (Buckets.SequenceEqual(_services.ProjectBuckets)) return;
        var keep = Bucket;
        Buckets.Clear();
        foreach (var bucket in _services.ProjectBuckets) Buckets.Add(bucket);
        Bucket = Buckets.Contains(keep) ? keep : TaskBoard.AllBuckets;
    }

    /// <summary>Redraw whichever view shows from the caches as they stand.</summary>
    public void Rebuild()
    {
        var flat = ShowsFlat;
        var filtered = TaskBoard.Filter(_services.ProjectTasks, Search, Bucket, ShowClosed, GroupBy, flat, TilePasses);

        if (ShowsTaskBoard)
        {
            Replace(Columns, TaskBoard.Columns(filtered, GroupBy, ShowClosed).Select(g => new BoardColumnItem(g)));
            CountLabel = $"{filtered.Count} {(filtered.Count == 1 ? "task" : "tasks")}";
            EmptyText = _services.ProjectTasksLoaded ? "No tasks match. Clear the filters to see everything." : "Loading tasks…";
        }
        else if (flat) RebuildFlat(filtered);
        else if (ShowsGitHubBoard)
        {
            var columns = GitHubProjectBoard.Columns(_services.GitHubProjectItems, _services.GitHubProjectStatus, Search);
            Replace(GitHubColumns, columns.Select(c => new GitHubColumnItem(c)));
            CountLabel = _services.GitHubProjectMessage ?? GitHubProjectBoard.Summary(columns);
            EmptyText = _services.GitHubProjectMessage ?? "Loading the GitHub Project…";
        }
        else if (ShowsWorkItemList)
        {
            var term = Search.Trim();
            var rows = _services.RecentWorkItems.Where(w => term.Length == 0
                || w.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
                || w.Id.ToString().Contains(term)
                || (w.AssignedTo?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));
            if (!ShowClosed) rows = rows.Where(w => !ProjectsGraphCards.IsClosed(w.State));
            if (ProjectsTileFilter.Shared.Selected is { } tile)
                rows = tile == ProjectsTile.GitHubIssues ? Enumerable.Empty<WorkItem>() : rows.Where(w => _tileIds.Contains(w.Id));
            Replace(WorkItemRows, rows.Select(w => new WorkItemRowItem(w)));
            CountLabel = $"{WorkItemRows.Count} work items";
        }
        else
        {
            var visible = _services.QueryRuns.Sum(r => r.Rows.Count(row => QueryRuns.RowVisible(row, Search.Trim(), ShowClosed,
                ProjectsTileFilter.Shared.IsActive ? TilePasses : null)));
            CountLabel = _services.QueryRuns.Count == 0 ? "" : $"{_services.QueryRuns.Count} queries · {visible} rows";
        }
    }

    private void RebuildFlat(List<UnifiedTask> filtered)
    {
        List<UnifiedTask> rows;
        var tile = ProjectsTileFilter.Shared.Selected;
        if (View == ProjectsView.Mine && tile == ProjectsTile.GitHubIssues)
        {
            // The GitHub Issues tile counts the strip's open issues, which are not work items: list those.
            rows = ProjectsTileFilter.IssueRows(_services.GitHubIssues ?? new(), Search);
        }
        else if (View == ProjectsView.Mine)
        {
            var keys = filtered.Select(t => (t.Provider, t.Id)).ToHashSet();
            var extra = TaskBoard.Filter(_services.MineExtra, Search, Bucket, ShowClosed, GroupBy, flat: true, TilePasses);
            rows = TaskBoard.MineTasks(_services.ProjectTasks.Where(t => keys.Contains((t.Provider, t.Id))), extra,
                _services.MyWorkItemIds ?? new HashSet<int>(), _services.GitHubViewerLogin);
        }
        else rows = filtered.OrderByDescending(t => t.UpdatedAt).ToList();

        Replace(FlatRows, rows.Select(t => new TaskCardItem(t)));
        CountLabel = $"{rows.Count} {(rows.Count == 1 ? "item" : "items")}";
        EmptyText = View == ProjectsView.Mine && tile is { } t
            ? $"Nothing in {ProjectsTileFilter.Title(t)}. None of your items match this tile; clear the filter to see them all."
            : View == ProjectsView.Mine
            ? (_services.MyWorkItemIds == null ? "Loading…" : "Nothing assigned to you")
            : _services.ProjectTasksLoaded ? "Nothing matches" : "Nothing loaded yet";
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    public static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
}
