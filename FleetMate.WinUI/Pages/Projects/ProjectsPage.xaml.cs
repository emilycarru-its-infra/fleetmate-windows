using FleetMate.Core.Config;
using FleetMate.Core.Links;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects.Tasks;
using FleetMate.WinUI.Shell;
using FleetMate.WinUI.Shell.Widgets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Serilog;
using Windows.ApplicationModel.DataTransfer;

namespace FleetMate.WinUI.Pages.Projects;

/// <summary>
/// The Projects tab, after FleetMate.GUI's BoardsPage and the macOS
/// BoardsView: Mine, Queries, Recent and Board, the widget strip, the New
/// menu, Sync and Refresh, and the selected item's detail beside the view.
/// Cards drag between board columns to change the grouped field. The rules
/// are FleetMate.Core's; the data is <see cref="ShellServices"/>'.
/// </summary>
public sealed partial class ProjectsPage : Page, ICaptureViews
{
    public const string Tab = "Projects";

    private readonly ShellServices _services = App.Services;
    private bool _loaded;
    private bool _syncingMyItems;
    private UnifiedTask? _selected;

    public ProjectsPage()
    {
        ViewModel = new ProjectsViewModel(App.Services);
        InitializeComponent();
        BuildViewMenu();
        FillMyItems();
        MainWindow.SearchChanged += (_, text) => { ViewModel.Search = text; RenderQueries(); };
        ProjectsTileFilter.Shared.Changed += (_, _) => DispatcherQueue.TryEnqueue(OnTileFilterChanged);
        WidgetStrip.Changed += tab => { if (tab == Tab) RenderGraphs(); };
        _services.CacheChanged += OnCacheChanged;
        _services.DevOpsSignedIn += OnDevOpsSignedIn;
        Queries.TaskSelected += (_, task) => ShowTask(task);
        Queries.TaskMenuRequested += (_, e) => ShowTaskMenu(e.Task, e.Target, e.At);
        Queries.OpenQueryRequested += (_, query) => { if (_services.StoredQueryUrl(query) is { } url) ExternalBrowser.Open(url); };
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ProjectsViewModel.ShowClosed)) RenderQueries();
        };
    }

    public ProjectsViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        RenderGraphs();
        HookLinks();
        if (!_loaded)
        {
            _loaded = true;
            await LoadAsync();
        }
        await TakePendingLinkAsync();
    }

    private async Task LoadAsync(bool force = false)
    {
        ViewModel.IsLoading = true;
        try
        {
            _ = _services.LoadMyWorkItemsAsync(force);
            _ = _services.EnsureGitHubIssuesAsync();
            await _services.LoadProjectTasksAsync(ViewModel.ProviderId);
            ViewModel.SyncBuckets();
            await ApplyViewAsync(force);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[projects] Load failed");
            ShowAction($"Could not load tasks: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            ViewModel.IsLoading = false;
        }
    }

    /// <summary>Show the panels for the current view and load what it needs.</summary>
    private async Task ApplyViewAsync(bool force = false)
    {
        ViewModel.Rebuild();
        RenderQueries();
        switch (ViewModel.View)
        {
            case ProjectsView.Queries when _services.UsesStoredQueries:
                await _services.LoadQueriesAsync(force);
                break;
            case ProjectsView.Queries:
                if (force || _services.RecentWorkItems.Count == 0) await _services.LoadRecentWorkItemsAsync();
                break;
            case ProjectsView.Mine:
                await _services.EnsureMineAsync(force);
                break;
            case ProjectsView.Board when ViewModel.ShowsGitHubBoard:
                if (force || _services.GitHubProjectItems.Count == 0) await _services.LoadGitHubProjectAsync();
                break;
        }
        ViewModel.Rebuild();
        RenderQueries();
    }

    private void OnCacheChanged(string key)
    {
        if (key is ShellServices.MyWorkItemsKey or ShellServices.IssuesKey)
        {
            if (ProjectsTileFilter.Shared.IsActive) ViewModel.RefreshTile();
            RenderGraphs();
        }
        if (key is ShellServices.ProjectTasksKey or ShellServices.QueriesKey or ShellServices.GitHubProjectKey or "Mine" or "WorkItems")
        {
            ViewModel.Rebuild();
            if (key == ShellServices.QueriesKey) RenderQueries();
        }
        ViewModel.IsLoading = new[] { ShellServices.ProjectTasksKey, ShellServices.QueriesKey, ShellServices.GitHubProjectKey, "Mine", "WorkItems" }
            .Any(_services.IsCacheLoading);
    }

    /// <summary>A list loaded before Azure DevOps sign-in finished left its work items out; load it again.</summary>
    private async void OnDevOpsSignedIn()
    {
        if (!_loaded) return;
        if (!_services.ProjectTasks.Any(t => t.Provider == "azdevops")) await LoadAsync(force: true);
        else if (ViewModel.ShowsQueries && _services.QueryRuns.Count == 0) await _services.LoadQueriesAsync(force: true);
    }

    // MARK: - Views

    private void ViewBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is not string tag || !Enum.TryParse<ProjectsView>(tag, out var view)) return;
        // A figure counts the signed-in user's items, so it filters Mine; leaving Mine drops it.
        if (view != ProjectsView.Mine) ProjectsTileFilter.Shared.Clear();
        ViewMenu.Content = tag;
        ViewModel.View = view;
        if (_loaded) _ = ApplyViewAsync();
    }

    private void SelectView(ProjectsView view)
    {
        var item = view switch
        {
            ProjectsView.Mine => MineItem,
            ProjectsView.Recent => RecentItem,
            ProjectsView.Board => BoardItem,
            _ => QueriesItem,
        };
        ViewBar.SelectedItem = item;
    }

    private void BuildViewMenu()
    {
        ViewMenu.Content = "Queries";
        foreach (var item in ViewBar.Items)
        {
            var entry = new MenuFlyoutItem { Text = item.Text };
            entry.Click += (_, _) => ViewBar.SelectedItem = item;
            ViewMenuFlyout.Items.Add(entry);
        }
    }

    /// <summary>Too little room for the views: they fold into one menu naming the open one, as on Mac.</summary>
    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var fold = e.NewSize.Width < 720;
        ViewBar.Visibility = ProjectsViewModel.Show(!fold);
        ViewMenu.Visibility = ProjectsViewModel.Show(fold);
        var compact = e.NewSize.Width < 900;
        foreach (var label in new[] { SortLabel, NewLabel, FiltersLabel }) label.Visibility = ProjectsViewModel.Show(!compact);
        GraphsScroller.MaxHeight = Math.Max(180, e.NewSize.Height * 0.4);
        FitDetail(e.NewSize.Width);
    }

    private async void BoardSourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded) await ApplyViewAsync();
    }

    private async void Provider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded && e.RemovedItems.Count > 0) await LoadAsync();
    }

    public Visibility IsBoard(ProjectsView view) => ProjectsViewModel.Show(view == ProjectsView.Board);

    public Visibility IsMine(ProjectsView view) => ProjectsViewModel.Show(view == ProjectsView.Mine);

    public bool HasText(string? text) => !string.IsNullOrEmpty(text);

    public Visibility EmptyFlat(int count) => ProjectsViewModel.Show(count == 0);

    // MARK: - Queries

    private void RenderQueries()
    {
        if (!ViewModel.ShowsQueries) return;
        if (_services.QueryRuns.Count == 0)
        {
            Queries.ShowMessage(_services.QueriesMessage ?? (_services.IsCacheLoading(ShellServices.QueriesKey) ? "Loading shared queries…" : "No shared queries."));
            return;
        }
        Queries.ShowRuns(_services.QueryRuns, ViewModel.Search, ViewModel.ShowClosed,
            ProjectsTileFilter.Shared.IsActive ? ViewModel.TilePasses : null);
    }

    /// <summary>Sort Queries By: the four orders, then the two directions worded for the chosen order.</summary>
    private void SortMenu_Opening(object sender, object e)
    {
        var current = UserPreferences.QueriesOrder;
        var ascending = UserPreferences.QueriesOrderAscending;
        SortMenu.Items.Clear();
        foreach (var order in QueryListOrders.All)
        {
            var item = new ToggleMenuFlyoutItem { Text = order.Title(), IsChecked = order == current };
            // Picking a new order starts it in its natural direction.
            item.Click += (_, _) => Queries.SetOrder(order, order == current ? ascending : order.DefaultAscending());
            SortMenu.Items.Add(item);
        }
        SortMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var direction in new[] { current.DefaultAscending(), !current.DefaultAscending() })
        {
            var item = new ToggleMenuFlyoutItem { Text = current.DirectionTitle(direction), IsChecked = direction == ascending };
            item.Click += (_, _) => Queries.SetOrder(current, direction);
            SortMenu.Items.Add(item);
        }
    }

    // MARK: - Graphs and the figures' filter

    private void RenderGraphs()
    {
        var shown = WidgetStrip.IsShown(Tab);
        GraphsScroller.Visibility = ProjectsViewModel.Show(shown);
        GraphsHost.Content = shown
            ? WidgetStrip.Build(Tab, ProjectsGraphCards.For(_services.ProjectsFigures, DateTime.UtcNow), OpenGraph)
            : null;
    }

    private void OpenGraph(Core.Development.GraphFilter filter)
    {
        if (ProjectsGraphCards.Apply(filter, ProjectsTileFilter.Shared) is { } url) ExternalBrowser.Open(url);
    }

    private void OnTileFilterChanged()
    {
        ViewModel.RefreshTile();
        SyncMyItems();
        RenderGraphs();
        // Picking a figure shows Mine.
        if (ProjectsTileFilter.Shared.IsActive && ViewModel.View != ProjectsView.Mine) SelectView(ProjectsView.Mine);
        RenderQueries();
    }

    private void ClearTileFilter_Click(object sender, RoutedEventArgs e) => ProjectsTileFilter.Shared.Clear();

    private void FillMyItems()
    {
        _syncingMyItems = true;
        MyItemsBox.Items.Add(new ComboBoxItem { Content = "All my items" });
        foreach (var tile in Enum.GetValues<ProjectsTile>())
            MyItemsBox.Items.Add(new ComboBoxItem { Content = ProjectsTileFilter.Title(tile), Tag = tile });
        _syncingMyItems = false;
        SyncMyItems();
    }

    private void SyncMyItems()
    {
        _syncingMyItems = true;
        var tile = ProjectsTileFilter.Shared.Selected;
        MyItemsBox.SelectedItem = MyItemsBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, tile));
        _syncingMyItems = false;
    }

    private void MyItemsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingMyItems || MyItemsBox.SelectedItem is not ComboBoxItem item) return;
        ProjectsTileFilter.Shared.Select(item.Tag as ProjectsTile?);
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e) => ClearFilters();

    private void ClearFiltersAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ClearFilters();
    }

    /// <summary>Clear Filters (Ctrl+Shift+K): the figure, Show Closed and the bucket.</summary>
    private void ClearFilters()
    {
        ProjectsTileFilter.Shared.Clear();
        ViewModel.ShowClosed = false;
        ViewModel.Bucket = TaskBoard.AllBuckets;
    }

    // MARK: - Refresh and Sync

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync(force: true);

    private async void RefreshAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await LoadAsync(force: true);
    }

    /// <summary>Sync the open tasks to Planner and to Markdown, each when configured.</summary>
    private async void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        if (_services.DemoMode)
        {
            ShowAction("Demo data can't be synced.", InfoBarSeverity.Informational);
            return;
        }
        ViewModel.IsLoading = true;
        try
        {
            var open = _services.ProjectTasks.Where(t => t.State != TaskState.Closed).ToList();
            var messages = new List<(string Text, bool Ok)>();
            var planner = new PlannerSyncService(_services.Config);
            if (planner.IsEnabled && await planner.AuthenticateAsync())
            {
                var result = await planner.SyncTasksAsync(open);
                messages.Add(($"Planner: {result.Message}", result.Success));
            }
            var markdown = new MarkdownSyncService(_services.Config);
            if (markdown.IsEnabled)
            {
                var result = await markdown.SyncBidirectionalAsync(open);
                messages.Add(($"Markdown: {result.Message}", result.Success));
            }
            if (!planner.IsEnabled && !markdown.IsEnabled)
                ShowAction("No sync destinations configured. Enable Planner or Markdown sync in your config.", InfoBarSeverity.Informational);
            else
                ShowAction(string.Join("  ", messages.Select(m => m.Text)),
                    messages.All(m => m.Ok) ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            ShowAction($"Sync failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            ViewModel.IsLoading = false;
        }
    }

    private void ShowAction(string message, InfoBarSeverity severity)
    {
        ActionBar.Message = message;
        ActionBar.Severity = severity;
        ActionBar.IsOpen = true;
    }

    // MARK: - Selection

    private void Card_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TaskCardItem card) ShowTask(card.Task);
    }

    private void WorkItem_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is WorkItemRowItem row) ShowTask(row.Item.AsUnifiedTask());
    }

    /// <summary>Issues open in the sidebar, as on the Mac; pull requests and drafts open in the browser.</summary>
    private void GitHubCard_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not GitHubCardItem card) return;
        var item = card.Item;
        if (item.Type == "ISSUE" && item.Content is { } content)
        {
            ShowTask(new UnifiedTask
            {
                Id = content.Number.ToString(),
                Provider = "github",
                Title = content.Title,
                ExternalUrl = content.Url,
                Assignees = content.Assignees.ToList(),
                Labels = content.Labels.ToList(),
                Description = content.Body,
                Metadata = new() { ["repository"] = content.Repository ?? "" },
            });
            return;
        }
        if (item.Content?.Url is { Length: > 0 } url) ExternalBrowser.Open(url);
    }

    /// <summary>Open <paramref name="task"/> in the detail sidebar beside the view (macOS 60/40).</summary>
    public void ShowTask(UnifiedTask task)
    {
        _selected = task;
        var pane = new TaskSummaryPane(task);
        pane.CloseRequested += (_, _) => CloseDetail();
        DetailHost.Content = pane;
        FitDetail(Root.ActualWidth);
    }

    private void CloseDetail()
    {
        _selected = null;
        DetailHost.Content = null;
        DetailColumn.Width = new GridLength(0);
    }

    /// <summary>The detail takes two fifths, at least 360 wide; with no room for both it covers the view.</summary>
    private void FitDetail(double width)
    {
        if (DetailHost.Content == null) return;
        var cover = width < 820;
        DetailColumn.Width = cover ? new GridLength(1, GridUnitType.Star) : new GridLength(Math.Max(360, width * 0.4));
        Grid.SetColumn(DetailHost, cover ? 0 : 1);
        Grid.SetColumnSpan(DetailHost, cover ? 2 : 1);
    }

    // MARK: - Board drag and drop

    private const string TaskDragFormat = "FleetMateTaskKey";
    private UnifiedTask? _dragging;

    private void BoardCards_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is not TaskCardItem card) return;
        _dragging = card.Task;
        e.Data.SetData(TaskDragFormat, card.Key);
        e.Data.SetText(card.Task.ExternalUrl is { Length: > 0 } url ? $"{card.Title} {url}" : card.Title);
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    /// <summary>
    /// A column lights up and says what the drop will change; a column the
    /// card cannot land on (its own, a Type column, a GitHub card) refuses it.
    /// </summary>
    private void BoardColumn_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not Grid { Tag: string column } target || !e.DataView.Contains(TaskDragFormat)) return;
        var task = _dragging;
        if (task == null || !TaskBoard.CanDrop(task, ViewModel.GroupBy, column))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.DragUIOverride.Caption = task?.Provider is { } p && p != "azdevops"
                ? "Only Azure DevOps items move between columns"
                : TaskBoard.DropRequest(ViewModel.GroupBy, column) == null ? $"Can't set {ViewModel.GroupBy} here" : "Already here";
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.Caption = TaskBoard.DropCaption(ViewModel.GroupBy, column);
        e.DragUIOverride.IsGlyphVisible = true;
        target.Background = GraphTheme.Resource("SubtleFillColorTertiaryBrush");
        target.BorderBrush = GraphTheme.Resource("AccentFillColorDefaultBrush");
    }

    private void BoardColumn_DragLeave(object sender, DragEventArgs e) => ResetColumn(sender);

    private static void ResetColumn(object sender)
    {
        if (sender is not Grid column) return;
        column.Background = GraphTheme.Resource("SubtleFillColorSecondaryBrush");
        column.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    /// <summary>Dropping a card on a column changes the grouped field (Azure DevOps only, as on the Mac).</summary>
    private async void BoardColumn_Drop(object sender, DragEventArgs e)
    {
        ResetColumn(sender);
        if (sender is not Grid { Tag: string column } || !e.DataView.Contains(TaskDragFormat)) return;
        var deferral = e.GetDeferral();
        string? key;
        try { key = await e.DataView.GetDataAsync(TaskDragFormat) as string; }
        finally { deferral.Complete(); }
        _dragging = null;
        var task = _services.ProjectTasks.FirstOrDefault(t => t.CompositeKey == key);
        if (task == null || TaskBoard.DropRequest(ViewModel.GroupBy, column) is not { } request) return;
        if (_services.DemoMode)
        {
            ShowAction("Demo data can't be changed.", InfoBarSeverity.Informational);
            return;
        }
        if (!await _services.UpdateProjectTaskAsync(task, request))
            ShowAction($"Azure DevOps did not accept the {ViewModel.GroupBy} for #{task.Id}.", InfoBarSeverity.Warning);
        else ViewModel.Rebuild();
    }
}
