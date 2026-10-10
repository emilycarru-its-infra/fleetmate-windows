using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FleetMate.Core.Services.Repos;
using FleetMate.GUI.Views.Shared;

namespace FleetMate.GUI.Views.Development.Repos;

/// <summary>A host heading in the grouped sidebar: Azure DevOps, GitHub, Other.</summary>
public sealed record SidebarSectionRow(string Id, string Title, int Count, bool IsExpanded)
{
    public string Chevron => IsExpanded ? "" : "";
    public string ToggleHint => IsExpanded ? "Collapse" : "Expand";
}

/// <summary>A project (Azure DevOps) or owner (GitHub) folder in the grouped sidebar.</summary>
public sealed record SidebarGroupRow(string Id, string Title, string Tooltip, bool IsExpanded)
{
    public string Chevron => IsExpanded ? "" : "";
}

/// <summary>One tracked repository: name, then branch, sync and change state.</summary>
public sealed record SidebarRepoRow(RepoRecord Record, RepoStatus? Status, bool IsFetching, bool ShowScope, int Depth)
{
    public string Id => Record.Id;
    public string Name => Record.Key.Name;
    public string Scope => Record.Key.Scope;
    public bool IsLocal => Record.Local != null;
    public Visibility ScopeVisibility => ShowScope ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FetchingVisibility => IsFetching ? Visibility.Visible : Visibility.Collapsed;
    public Thickness Indent => new(Depth * 14 + 4, 3, 2, 3);
    public string Tooltip => Record.Local is { } local ? RepoShell.Abbreviate(local.Path) : Record.Key.DisplayName;

    public string Metadata
    {
        get
        {
            if (Status is null) return Record.Key.Scope;
            if (Status.Error is { } error) return "⚠ " + error;
            var parts = new List<string> { "⎇ " + (Status.Branch ?? "detached") };
            var sync = (Status.Ahead > 0 ? $"↑{Status.Ahead}" : "") + (Status.Behind > 0 ? (Status.Ahead > 0 ? " " : "") + $"↓{Status.Behind}" : "");
            if (sync.Length > 0) parts.Add(sync);
            if (Status.ChangedCount > 0) parts.Add($"{Status.ChangedCount} changed");
            else if (Status.IsClean) parts.Add("clean");
            return string.Join("   ", parts);
        }
    }

    public Brush MetadataBrush => Status?.Error != null ? RepoBrushes.Warning : RepoBrushes.Secondary;
}

/// <summary>
/// The Repos sidebar: the workspace's mode (Changes, History, Files,
/// Insights) on top, then tracked repositories grouped the way checkouts sit
/// on disk — host, then project or owner — with a filter and sort below.
/// Status refreshes when shown and every 30 seconds while shown; fetch is on
/// demand. Sort, grouping, filter and collapsed groups persist.
/// </summary>
public partial class RepoSidebar : UserControl
{
    private RepoWorkspaceModel? _model;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private RepoSidebarSort _sort = RepoWorkspacePreferences.ReadEnum("sidebar.sort", RepoSidebarSort.Name);
    private bool _grouped = RepoWorkspacePreferences.Read("sidebar.grouped") != "0";
    private readonly HashSet<string> _collapsed = (RepoWorkspacePreferences.Read("sidebar.collapsed") ?? "")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
    private bool _rendering;

    public RepoSidebar()
    {
        InitializeComponent();
        AgentContextMenu.Attach(RepoList);
        FilterBox.Text = RepoWorkspacePreferences.Read("sidebar.filter") ?? "";
        _timer.Tick += async (_, _) => { if (_model != null) await _model.RefreshStatusesAsync(); };
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible && _model != null)
            {
                _timer.Start();
                await _model.RefreshStatusesAsync();
            }
            else
            {
                _timer.Stop();
            }
        };
        Unloaded += (_, _) => _timer.Stop();
        UpdateSortTooltip();
    }

    public void Attach(RepoWorkspaceModel model)
    {
        _model = model;
        model.PropertyChanged += OnModelChanged;
        model.Git.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(GitPaneModel.Panel)) SyncPanel(); };
        SyncPanel();
        Render();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RepoWorkspaceModel.Tracked):
            case nameof(RepoWorkspaceModel.Statuses):
            case nameof(RepoWorkspaceModel.Fetching):
            case nameof(RepoWorkspaceModel.SelectedId):
                Render();
                break;
            case nameof(RepoWorkspaceModel.IsRefreshing):
                UpdateBusy();
                break;
        }
    }

    // ── Mode ────────────────────────────────────────────────────────────

    private void SyncPanel()
    {
        if (_model == null) return;
        var target = _model.Git.Panel switch
        {
            RepoPanel.History => PanelHistory,
            RepoPanel.Files => PanelFiles,
            RepoPanel.Insights => PanelInsights,
            _ => PanelChanges,
        };
        if (target.IsChecked != true) target.IsChecked = true;
    }

    private void OnPanelChecked(object sender, RoutedEventArgs e)
    {
        if (_model == null || sender is not RadioButton { Tag: string tag }) return;
        if (Enum.TryParse<RepoPanel>(tag, out var panel)) _model.Git.Panel = panel;
    }

    // ── List ────────────────────────────────────────────────────────────

    private string Filter => FilterBox.Text.Trim();

    private void Render()
    {
        if (_model == null) return;
        _rendering = true;
        try
        {
            var filtering = Filter.Length > 0;
            var rows = new List<object>();
            if (_grouped)
            {
                foreach (var section in RepoSidebarOrganizer.Sections(_model.Tracked, _model.Statuses, _sort, Filter))
                {
                    var sectionOpen = filtering || !_collapsed.Contains(section.Id);
                    rows.Add(new SidebarSectionRow(section.Id, section.Title, section.RepositoryCount, sectionOpen));
                    if (!sectionOpen) continue;
                    foreach (var group in section.Groups)
                    {
                        // While a filter is typed every group is open, so a match is
                        // never hidden inside a collapsed folder.
                        var groupOpen = filtering || !_collapsed.Contains(group.Id);
                        rows.Add(new SidebarGroupRow(group.Id, group.Scope, group.Title, groupOpen));
                        if (!groupOpen) continue;
                        rows.AddRange(group.Records.Select(r => Row(r, showScope: false, depth: 1)));
                    }
                }
            }
            else
            {
                rows.AddRange(RepoSidebarOrganizer.Flat(_model.Tracked, _model.Statuses, _sort, Filter)
                    .Select(r => Row(r, showScope: true, depth: 0)));
            }
            RepoList.ItemsSource = rows;
            RepoList.SelectedItem = rows.OfType<SidebarRepoRow>().FirstOrDefault(r => r.Id == _model.SelectedId);
            EmptyState.Visibility = _model.Tracked.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            NoMatches.Visibility = _model.Tracked.Count > 0 && !rows.OfType<SidebarRepoRow>().Any() && filtering
                ? Visibility.Visible : Visibility.Collapsed;
            FetchAllButton.IsEnabled = _model.Tracked.Count > 0 && _model.Fetching.Count == 0;
            UpdateBusy();
        }
        finally
        {
            _rendering = false;
        }
    }

    private SidebarRepoRow Row(RepoRecord record, bool showScope, int depth) =>
        new(record, _model!.Statuses.GetValueOrDefault(record.Id), _model.Fetching.Contains(record.Id), showScope, depth);

    private void UpdateBusy() =>
        BusyRing.Visibility = _model is { } m && (m.IsRefreshing || m.Fetching.Count > 0) ? Visibility.Visible : Visibility.Collapsed;

    private void OnRepoSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering || _model == null) return;
        if (RepoList.SelectedItem is SidebarRepoRow row) _model.SelectedId = row.Id;
    }

    /// <summary>A click on a section or group heading opens or closes it; it never selects.</summary>
    private void OnListClicked(object sender, MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(RepoList, (DependencyObject)e.OriginalSource) as ListBoxItem;
        var id = item?.DataContext switch
        {
            SidebarSectionRow s => s.Id,
            SidebarGroupRow g => g.Id,
            _ => null,
        };
        if (id == null || Filter.Length > 0) return;
        if (!_collapsed.Remove(id)) _collapsed.Add(id);
        SaveCollapsed();
        Render();
        e.Handled = true;
    }

    private void SaveCollapsed() =>
        RepoWorkspacePreferences.Write("sidebar.collapsed", string.Join("\n", _collapsed.OrderBy(c => c)));

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        RepoWorkspacePreferences.Write("sidebar.filter", FilterBox.Text.Length > 0 ? FilterBox.Text : null);
        Render();
    }

    // ── Sort menu ───────────────────────────────────────────────────────

    private void OnSortClicked(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = SortButton, Placement = PlacementMode.Top };
        menu.Items.Add(new MenuItem { Header = "Sort By", IsEnabled = false });
        foreach (var sort in Enum.GetValues<RepoSidebarSort>())
        {
            var item = new MenuItem { Header = sort.Title(), IsCheckable = true, IsChecked = sort == _sort };
            item.Click += (_, _) =>
            {
                _sort = sort;
                RepoWorkspacePreferences.Write("sidebar.sort", sort.ToString());
                UpdateSortTooltip();
                Render();
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var group = new MenuItem { Header = "Group by Source", IsCheckable = true, IsChecked = _grouped };
        group.Click += (_, _) =>
        {
            _grouped = !_grouped;
            RepoWorkspacePreferences.Write("sidebar.grouped", _grouped ? null : "0");
            UpdateSortTooltip();
            Render();
        };
        menu.Items.Add(group);
        if (_grouped && _model != null)
        {
            var expand = new MenuItem { Header = "Expand All" };
            expand.Click += (_, _) => { _collapsed.Clear(); SaveCollapsed(); Render(); };
            var collapse = new MenuItem { Header = "Collapse All" };
            collapse.Click += (_, _) =>
            {
                var sections = RepoSidebarOrganizer.Sections(_model.Tracked, new Dictionary<string, RepoStatus>(), RepoSidebarSort.Name);
                _collapsed.Clear();
                foreach (var g in sections.SelectMany(s => s.Groups)) _collapsed.Add(g.Id);
                SaveCollapsed();
                Render();
            };
            menu.Items.Add(expand);
            menu.Items.Add(collapse);
        }
        menu.IsOpen = true;
    }

    private void UpdateSortTooltip() =>
        SortButton.ToolTip = $"Sort by {_sort.Title().ToLowerInvariant()}{(_grouped ? ", grouped by source" : "")}";

    // ── Actions ─────────────────────────────────────────────────────────

    private async void OnFetchAll(object sender, RoutedEventArgs e)
    {
        if (_model != null) await _model.FetchAsync(_model.Tracked.Select(r => r.Id).ToList());
    }

    private static SidebarRepoRow? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as SidebarRepoRow;

    private async void OnFetchRepo(object sender, RoutedEventArgs e)
    {
        if (_model != null && RowOf(sender) is { } row) await _model.FetchAsync(new[] { row.Id });
    }

    private void OnRevealRepo(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender)?.Record.Local is { } local) RepoShell.Reveal(local.Path);
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender)?.Record.Local is { } local) RepoShell.Copy(local.Path);
    }

    private void OnCopyName(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) RepoShell.Copy(row.Record.Key.DisplayName);
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) => RepositoriesSettingsView.OpenInSettings(Window.GetWindow(this));
}
