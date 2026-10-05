using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Services.Projects.Tasks;
using Serilog;

namespace FleetMate.GUI.Views.Projects;

/// <summary>
/// Mine | Queries | Recent | Board, matching the macOS Projects views. Mine is
/// the signed-in user's open work items (the Azure DevOps @Me set) plus GitHub
/// items assigned to them; Recent is everything loaded, newest change first.
/// </summary>
public partial class BoardsPage
{
    private HashSet<int>? _myIds;
    private readonly List<UnifiedTask> _mineExtra = new();
    private string? _viewerLogin;
    private bool _loadingMine;

    private enum ProjectsView { Mine, Queries, Recent, Board }

    private ProjectsView CurrentView =>
        MineModeRadio.IsChecked == true ? ProjectsView.Mine
        : RecentModeRadio.IsChecked == true ? ProjectsView.Recent
        : BoardModeRadio.IsChecked == true ? ProjectsView.Board
        : ProjectsView.Queries;

    private bool BoardShowsGitHubProject =>
        (BoardSourceCombo.SelectedItem as ComboBoxItem)?.Tag as string == "github";

    private async void OnBoardSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        await ApplyViewAsync();
    }

    /// <summary>Show the panels for the current view and load what it needs.</summary>
    private async Task ApplyViewAsync()
    {
        var view = CurrentView;
        var flat = view is ProjectsView.Mine or ProjectsView.Recent;
        var board = view == ProjectsView.Board;
        var githubBoard = board && BoardShowsGitHubProject;

        // Queries renders every Shared Query when Azure DevOps is configured;
        // without it the legacy flat work item list stands in.
        var useQueries = _config.AzureDevOps != null && !string.IsNullOrEmpty(_config.AzureDevOps.Organization);
        var queries = view == ProjectsView.Queries;

        static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        BoardFilters.Visibility = Show(flat || (board && !githubBoard));
        GroupByCombo.Visibility = Show(board);
        BoardSourceCombo.Visibility = Show(board);
        ListFilters.Visibility = Show(queries);
        ProjectsFilters.Visibility = Show(githubBoard);
        KanbanBoard.Visibility = Show(board && !githubBoard);
        FlatTasks.Visibility = Show(flat);
        QueriesList.Visibility = Show(queries && useQueries);
        WorkItemsList.Visibility = Show(queries && !useQueries);
        ProjectsBoard.Visibility = Show(githubBoard);

        // The source picker sits in the board filter card; keep it reachable
        // while the GitHub Project board (which has its own filter card) shows.
        if (githubBoard) BoardFilters.Visibility = Visibility.Visible;

        if (queries && useQueries) await LoadQueriesAsync();
        else if (queries && _allWorkItems.Count == 0) await LoadWorkItemsAsync();

        if (githubBoard && _projectItems.Count == 0) await LoadProjectsBoardAsync();
        if (view == ProjectsView.Mine) await EnsureMineAsync();

        UpdateDisplay();
    }

    /// <summary>
    /// The @Me set, the GitHub login, and any @Me work items the loaded list
    /// does not contain (it holds the 100 most recent; mine may be older).
    /// </summary>
    private async Task EnsureMineAsync(bool force = false)
    {
        if (_loadingMine || (_myIds != null && !force)) return;
        _loadingMine = true;
        try
        {
            if (_devOpsService != null)
            {
                _myIds = await _devOpsService.GetMyOpenWorkItemIdsAsync();
                var loaded = _allTasks.Where(t => t.Provider == "azdevops").Select(t => t.Id).ToHashSet();
                var missing = _myIds.Where(id => !loaded.Contains(id.ToString())).Take(200).ToList();
                _mineExtra.Clear();
                if (missing.Count > 0)
                {
                    var items = await _devOpsService.GetWorkItemsByIdsAsync(missing);
                    _mineExtra.AddRange(items.Select(AzureDevOpsTaskProvider.MapToUnifiedTask));
                }
            }
            else
            {
                _myIds = new();
            }

            if (_viewerLogin == null)
            {
                try
                {
                    using var github = new GitHubPullRequestService(_config.GitHubProviderOrDefault());
                    _viewerLogin = (await github.GetViewerAsync()).Login ?? "";
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "[projects] GitHub viewer unavailable for Mine");
                    _viewerLogin = "";
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[projects] Could not load Mine");
            _myIds ??= new();
        }
        finally
        {
            _loadingMine = false;
        }
    }

    /// <summary>The Mine rows: @Me work items plus GitHub items assigned to the viewer.</summary>
    internal static List<UnifiedTask> MineTasks(
        IEnumerable<UnifiedTask> loaded, IEnumerable<UnifiedTask> extra, IReadOnlySet<int> myIds, string? viewerLogin) =>
        loaded.Concat(extra)
            .GroupBy(t => (t.Provider, t.Id)).Select(g => g.First())
            .Where(t => t.Provider == "azdevops"
                ? int.TryParse(t.Id, out var id) && myIds.Contains(id)
                : !string.IsNullOrEmpty(viewerLogin)
                  && t.Assignees.Any(a => string.Equals(a, viewerLogin, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(t => t.UpdatedAt)
            .ToList();

    /// <summary>Render Mine or Recent into the flat list, through the same search, bucket and closed filters.</summary>
    private void RenderFlatTasks(IEnumerable<UnifiedTask> filtered)
    {
        var view = CurrentView;
        if (view is not (ProjectsView.Mine or ProjectsView.Recent)) return;

        var filteredSet = filtered.Select(t => (t.Provider, t.Id)).ToHashSet();
        List<UnifiedTask> rows;

        if (view == ProjectsView.Mine)
        {
            // Mine's extra items were not in the board list, so they pass the
            // same filters here rather than through `filtered`.
            var extra = FilterTasks(_mineExtra);
            rows = MineTasks(_allTasks.Where(t => filteredSet.Contains((t.Provider, t.Id))), extra, _myIds ?? new HashSet<int>(), _viewerLogin);
        }
        else
        {
            rows = filtered.OrderByDescending(t => t.UpdatedAt).ToList();
        }

        FlatTaskList.ItemsSource = rows.Select(t => new TaskCardVm { Task = t }).ToList();
        FlatTasksEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FlatTasksEmpty.Text = view == ProjectsView.Mine
            ? (_myIds == null ? "Loading…" : "Nothing assigned to you")
            : "Nothing loaded yet";
        TaskCountLabel.Text = $"{rows.Count} items";
    }

    private void OnFlatTaskSelected(object sender, SelectionChangedEventArgs e)
    {
        if (FlatTaskList.SelectedItem is not TaskCardVm vm) return;
        var provider = _registry?.GetProvider(vm.Task.Provider);
        DetailPanel.ShowTask(vm.Task, provider);
        DetailPanel.Visibility = Visibility.Visible;
        DetailColumn.Width = new GridLength(2, GridUnitType.Star);
    }
}
