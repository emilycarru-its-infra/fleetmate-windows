using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Services.Projects.Tasks;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Devices;
using FleetMate.Core.Services.Inventory;
using FleetMate.Core.Services.Tickets;
using FleetMate.Core.Services.Reporting;

namespace FleetMate.GUI.Views.Projects;

public partial class BoardsPage : Page
{
    private readonly FleetMateConfig _config;
    private readonly App? _app;
    private TaskProviderRegistry? _registry;
    private List<UnifiedTask> _allTasks = new();
    private string? _filterProvider;
    private string? _filterBucket;
    private string _searchText = "";
    private bool _showClosed;
    private bool _isInitialLoadDone;
    private string _groupBy = "State"; // board column dimension, macOS GroupByOption parity

    // List mode
    private AzureDevOpsService? _devOpsService;
    private List<WorkItem> _allWorkItems = new();
    private string _listSearchText = "";
    private string? _listStateFilter;

    // Projects mode (GitHub Projects v2 dynamic board)
    private GitHubProjectsService? _projectsService;
    private List<GitHubProjectItem> _projectItems = new();
    private GitHubProjectField? _statusField;

    public BoardsPage()
    {
        InitializeComponent();
        _app = Application.Current as App;
        _config = _app?.Config ?? FleetMateConfig.Load();

        DetailPanel.CloseRequested += (_, _) => DetailPanel.Visibility = Visibility.Collapsed;
        DetailPanel.TaskUpdated += async (_, _) => await LoadTasksAsync();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isInitialLoadDone) return;
        _isInitialLoadDone = true;

        // Initialize AzDO service for list mode
        if (_config.AzureDevOps != null && !string.IsNullOrEmpty(_config.AzureDevOps.Organization))
        {
            _devOpsService = _app?.DevOpsService ?? new AzureDevOpsService(_config.AzureDevOps);
        }

        // Show SSO button if ClientId + TenantId are configured
        if (_config.AzureDevOps != null
            && !string.IsNullOrEmpty(_config.AzureDevOps.ClientId)
            && !string.IsNullOrEmpty(_config.AzureDevOps.TenantId))
        {
            SsoButton.Visibility = Visibility.Visible;
        }
        UpdateSsoButtonState();

        await InitializeRegistryAsync();
        await LoadBucketsAsync();
        await LoadTasksAsync();
    }

    private async Task InitializeRegistryAsync()
    {
        _registry = new TaskProviderRegistry();

        var azdo = new AzureDevOpsTaskProvider(_config);
        var github = new GitHubProjectsTaskProvider(_config.Tasks?.Providers?.GitHub ?? new GitHubProviderConfig());
        var gitea = new GiteaTaskProvider(_config);

        _registry.RegisterProvider(azdo);
        _registry.RegisterProvider(github);
        _registry.RegisterProvider(gitea);

        // Authenticate enabled providers
        foreach (var provider in _registry.GetProviders().Where(p => p.IsEnabled))
        {
            await provider.AuthenticateAsync();
        }
    }

    private async Task LoadBucketsAsync()
    {
        if (_registry == null) return;

        var allBuckets = new List<string> { "(All Buckets)" };

        foreach (var provider in _registry.GetProviders().Where(p => p.IsEnabled))
        {
            var buckets = await provider.ListBucketsAsync();
            allBuckets.AddRange(buckets.Select(b => b.Name));
        }

        BucketFilter.ItemsSource = allBuckets.Distinct().ToList();
        BucketFilter.SelectedIndex = 0;
    }

    private async Task LoadTasksAsync()
    {
        if (_registry == null) return;

        try
        {
            LoadingOverlay.Visibility = Visibility.Visible;

            var filter = new TaskFilter
            {
                IncludeClosed = _showClosed || true, // Always fetch to populate columns
                Limit = 100
            };

            if (!string.IsNullOrEmpty(_filterProvider))
            {
                _allTasks = await _registry.ListTasksAsync(_filterProvider, filter);
            }
            else
            {
                _allTasks = await _registry.ListAllTasksAsync(filter);
            }

            UpdateDisplay();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load tasks: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateDisplay()
    {
        var filtered = _allTasks.AsEnumerable();

        // Apply search filter
        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            var search = _searchText.ToLowerInvariant();
            filtered = filtered.Where(t =>
                t.Title.ToLowerInvariant().Contains(search) ||
                (t.Description?.ToLowerInvariant().Contains(search) ?? false));
        }

        // Apply bucket filter
        if (!string.IsNullOrEmpty(_filterBucket) && _filterBucket != "(All Buckets)")
        {
            filtered = filtered.Where(t => t.Bucket == _filterBucket);
        }

        // State columns handle closed visibility themselves; every other
        // dimension drops closed tasks entirely, like the Mac board.
        if (!_showClosed && _groupBy != "State")
        {
            filtered = filtered.Where(t => t.State != TaskState.Closed);
        }

        var tasks = filtered.ToList();
        TaskBoardColumnsControl.ItemsSource = BuildTaskColumns(tasks);
        TaskCountLabel.Text = $"{tasks.Count} tasks";
    }

    private static readonly string[] ClosedStateNames = { "closed", "done", "removed", "completed", "resolved" };

    private List<TaskBoardColumn> BuildTaskColumns(List<UnifiedTask> tasks)
    {
        static string MetaOr(UnifiedTask t, string key, string fallback)
            => t.Metadata.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : fallback;

        switch (_groupBy)
        {
            case "Priority":
            {
                var labels = new (int Key, string Label, string Color)[]
                {
                    (1, "Critical", "#C05050"), (2, "High", "#DD8844"), (3, "Medium", "#C9A83A"),
                    (4, "Low", "#4C9A60"), (0, "None", "#808080")
                };
                var grouped = tasks.GroupBy(t => t.Priority ?? 0).ToDictionary(g => g.Key, g => g.ToList());
                return labels
                    .Select(l => MakeColumn(l.Label, l.Color, grouped.GetValueOrDefault(l.Key) ?? new List<UnifiedTask>()))
                    .ToList();
            }
            case "Assigned To":
                return tasks.GroupBy(t => t.Assignees.FirstOrDefault() ?? "Unassigned")
                    .OrderBy(g => g.Key == "Unassigned" ? 0 : 1)
                    .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(g => MakeColumn(g.Key, "#5B6BAE", g.ToList()))
                    .ToList();
            case "Area Path":
                return GroupedAlpha(tasks, t => MetaOr(t, "areaPath", "No Area"), "No Area", "#8E6BA5");
            case "Iteration":
                return GroupedAlpha(tasks, t => MetaOr(t, "iterationPath", "No Iteration"), "No Iteration", "#A5783A");
            case "Type":
                return GroupedAlpha(tasks, t => MetaOr(t, "workItemType", "Unknown"), "Unknown", "#4C8C8C");
            default: // State
            {
                var grouped = tasks.GroupBy(t => MetaOr(t, "state", "New")).ToDictionary(g => g.Key, g => g.ToList());
                return grouped.Keys
                    .Where(k => _showClosed || !ClosedStateNames.Contains(k.ToLowerInvariant()))
                    .OrderBy(StateOrder)
                    .ThenBy(k => k, StringComparer.OrdinalIgnoreCase)
                    .Select(s => MakeColumn(s, StateColorHex(s), grouped[s]))
                    .ToList();
            }
        }
    }

    private static List<TaskBoardColumn> GroupedAlpha(
        List<UnifiedTask> tasks, Func<UnifiedTask, string> key, string fallback, string colorHex)
        => tasks.GroupBy(key)
            .OrderBy(g => g.Key == fallback ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => MakeColumn(g.Key, colorHex, g.ToList()))
            .ToList();

    private static TaskBoardColumn MakeColumn(string title, string colorHex, List<UnifiedTask> tasks) => new()
    {
        Title = title,
        Color = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex)),
        CountLabel = $"({tasks.Count})",
        Tasks = tasks.OrderByDescending(t => t.UpdatedAt).Select(t => new TaskCardVm { Task = t }).ToList()
    };

    private static int StateOrder(string state) => state.ToLowerInvariant() switch
    {
        "new" or "to do" or "proposed" or "open" => 0,
        "active" or "in progress" or "doing" or "committed" => 1,
        "resolved" or "done" or "completed" => 2,
        "closed" => 3,
        "removed" => 4,
        _ => 2
    };

    private static string StateColorHex(string state) => state.ToLowerInvariant() switch
    {
        "new" or "to do" or "proposed" or "open" => "#4C9A60",
        "active" or "in progress" or "doing" or "committed" => "#3A6EA5",
        "resolved" or "done" or "completed" => "#8E6BA5",
        "closed" => "#707070",
        "removed" => "#C05050",
        _ => "#3A6EA5"
    };

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadTasksAsync();
    }

    private async void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            LoadingOverlay.Visibility = Visibility.Visible;

            var openTasks = _allTasks.Where(t => t.State != TaskState.Closed).ToList();

            // Try Planner sync
            var plannerService = new PlannerSyncService(_config);
            if (plannerService.IsEnabled)
            {
                if (await plannerService.AuthenticateAsync())
                {
                    var result = await plannerService.SyncTasksAsync(openTasks);
                    MessageBox.Show(result.Message, "Planner Sync", MessageBoxButton.OK,
                        result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
                }
            }

            // Try Markdown sync
            var mdService = new MarkdownSyncService(_config);
            if (mdService.IsEnabled)
            {
                var result = await mdService.SyncBidirectionalAsync(openTasks);
                MessageBox.Show(result.Message, "Markdown Sync", MessageBoxButton.OK,
                    result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }

            if (!plannerService.IsEnabled && !mdService.IsEnabled)
            {
                MessageBox.Show("No sync destinations configured. Enable Planner or Markdown sync in your config.",
                    "Sync", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Sync failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void ProviderFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderFilter.SelectedItem is ComboBoxItem item)
        {
            _filterProvider = item.Tag?.ToString();
            if (string.IsNullOrEmpty(_filterProvider)) _filterProvider = null;
            _ = LoadTasksAsync();
        }
    }

    private void BucketFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _filterBucket = BucketFilter.SelectedItem?.ToString();
        UpdateDisplay();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text;
        UpdateDisplay();
    }

    private void ShowClosedCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        _showClosed = ShowClosedCheckbox.IsChecked == true;
        UpdateDisplay();
    }

    private void GroupByCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        _groupBy = (GroupByCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "State";
        UpdateDisplay();
    }

    private void OnTaskCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border card && card.Tag is TaskCardVm vm)
        {
            var provider = _registry?.GetProvider(vm.Task.Provider);
            DetailPanel.ShowTask(vm.Task, provider);
            DetailPanel.Visibility = Visibility.Visible;
        }
    }

    // ── Board drag-and-drop (macOS parity: DevOps-only field updates) ──

    private const string TaskDragFormat = "FleetMateTaskKey";
    private Point _taskDragStart;

    private void OnTaskCardPreviewMouseDown(object sender, MouseButtonEventArgs e)
        => _taskDragStart = e.GetPosition(null);

    private void OnTaskCardMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not Border card) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _taskDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _taskDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        if (card.Tag is TaskCardVm vm)
            DragDrop.DoDragDrop(card, new DataObject(TaskDragFormat, vm.Task.CompositeKey), DragDropEffects.Move);
    }

    private void OnTaskColumnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(TaskDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnTaskColumnDrop(object sender, DragEventArgs e)
    {
        if (sender is not Border column || column.Tag is not string columnTitle) return;
        if (!e.Data.GetDataPresent(TaskDragFormat)) return;
        var key = (string)e.Data.GetData(TaskDragFormat)!;
        var task = _allTasks.FirstOrDefault(t => t.CompositeKey == key);
        if (task == null) return;

        // Field updates via drag are DevOps-only, exactly like the Mac.
        if (task.Provider != "azdevops" || _devOpsService == null || !int.TryParse(task.Id, out var id))
            return;

        UpdateWorkItemRequest? request = _groupBy switch
        {
            "State" => new UpdateWorkItemRequest { State = columnTitle },
            "Priority" => PriorityFromLabel(columnTitle) is int p and > 0
                ? new UpdateWorkItemRequest { Priority = p } : null,
            "Assigned To" => columnTitle == "Unassigned" ? null
                : new UpdateWorkItemRequest { AssignedTo = columnTitle },
            "Iteration" => columnTitle == "No Iteration" ? null
                : new UpdateWorkItemRequest { IterationPath = columnTitle },
            "Area Path" => columnTitle == "No Area" ? null
                : new UpdateWorkItemRequest { AreaPath = columnTitle },
            _ => null // Type changes are not drag-updatable
        };
        if (request == null) return;

        await ApplyWorkItemUpdateAsync(task, id, request);
    }

    /// <summary>Apply a DevOps work-item update and refresh the local task + board.</summary>
    private async Task ApplyWorkItemUpdateAsync(UnifiedTask task, int id, UpdateWorkItemRequest request)
    {
        if (_devOpsService == null) return;
        var updated = await _devOpsService.UpdateWorkItemAsync(id, request);
        if (updated == null) return;

        task.Metadata["state"] = updated.State;
        task.Metadata["areaPath"] = updated.AreaPath ?? "";
        task.Metadata["iterationPath"] = updated.IterationPath ?? "";
        task.Metadata["workItemType"] = updated.WorkItemType;
        task.Priority = updated.Priority;
        task.Assignees = updated.AssignedTo != null ? new List<string> { updated.AssignedTo } : new List<string>();
        task.State = updated.State.ToLowerInvariant() switch
        {
            "new" or "to do" or "proposed" or "open" => TaskState.Open,
            "active" or "in progress" or "doing" or "committed" => TaskState.InProgress,
            _ => TaskState.Closed
        };
        UpdateDisplay();
    }

    private static int? PriorityFromLabel(string label) => label switch
    {
        "Critical" => 1, "High" => 2, "Medium" => 3, "Low" => 4, _ => null
    };

    // ── Card context menu ─────────────────────────────────────────

    /// <summary>Resolve the card view-model behind a context-menu item.</summary>
    private static TaskCardVm? VmFromMenuItem(object sender)
    {
        DependencyObject? current = sender as MenuItem;
        while (current != null && current is not System.Windows.Controls.ContextMenu)
        {
            current = LogicalTreeHelper.GetParent(current) ?? VisualTreeHelper.GetParent(current);
        }
        return (current as System.Windows.Controls.ContextMenu)?.PlacementTarget is Border card
            && card.Tag is TaskCardVm vm ? vm : null;
    }

    private void OnTaskOpenInBrowser(object sender, RoutedEventArgs e)
    {
        if (VmFromMenuItem(sender)?.Task.ExternalUrl is not { Length: > 0 } url) return;
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
    }

    private void OnTaskCopyLink(object sender, RoutedEventArgs e)
    {
        if (VmFromMenuItem(sender)?.Task.ExternalUrl is not { Length: > 0 } url) return;
        try { Clipboard.SetText(url); } catch { }
    }

    private async void OnTaskSetState(object sender, RoutedEventArgs e)
    {
        if (VmFromMenuItem(sender) is not { } vm || (sender as MenuItem)?.Header is not string state) return;
        if (vm.Task.Provider != "azdevops" || !int.TryParse(vm.Task.Id, out var id)) return;
        await ApplyWorkItemUpdateAsync(vm.Task, id, new UpdateWorkItemRequest { State = state });
    }

    private async void OnTaskSetPriority(object sender, RoutedEventArgs e)
    {
        if (VmFromMenuItem(sender) is not { } vm || (sender as MenuItem)?.Header is not string label) return;
        if (vm.Task.Provider != "azdevops" || !int.TryParse(vm.Task.Id, out var id)) return;
        if (PriorityFromLabel(label) is not int priority) return;
        await ApplyWorkItemUpdateAsync(vm.Task, id, new UpdateWorkItemRequest { Priority = priority });
    }

    // MARK: - View Mode Toggle

    private async void OnViewModeChanged(object sender, RoutedEventArgs e)
    {
        // BoardModeRadio carries IsChecked="True", so this fires mid-parse —
        // before the other radios and every panel below them exist. As an
        // async void handler the resulting NullReferenceException reaches the
        // dispatcher unhandled and kills the process, so opening Projects took
        // the whole app down.
        if (!IsInitialized) return;

        var isBoardMode = BoardModeRadio.IsChecked == true;
        var isListMode = ListModeRadio.IsChecked == true;
        var isProjectsMode = ProjectsModeRadio.IsChecked == true;

        // Toggle visibility
        BoardFilters.Visibility = isBoardMode ? Visibility.Visible : Visibility.Collapsed;
        ListFilters.Visibility = isListMode ? Visibility.Visible : Visibility.Collapsed;
        ProjectsFilters.Visibility = isProjectsMode ? Visibility.Visible : Visibility.Collapsed;
        KanbanBoard.Visibility = isBoardMode ? Visibility.Visible : Visibility.Collapsed;
        WorkItemsList.Visibility = isListMode ? Visibility.Visible : Visibility.Collapsed;
        ProjectsBoard.Visibility = isProjectsMode ? Visibility.Visible : Visibility.Collapsed;

        if (isListMode && _allWorkItems.Count == 0)
        {
            await LoadWorkItemsAsync();
        }

        if (isProjectsMode && _projectItems.Count == 0)
        {
            await LoadProjectsBoardAsync();
        }
    }

    // MARK: - List Mode (AzDO Work Items)

    private async Task LoadWorkItemsAsync()
    {
        if (_devOpsService == null) return;

        try
        {
            LoadingOverlay.Visibility = Visibility.Visible;
            _allWorkItems = await _devOpsService.GetWorkItemsAsync(limit: 200);
            UpdateListDisplay();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load work items: {ex.Message}", "Error", 
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateListDisplay()
    {
        // ComboBox/TextBox change events fire during InitializeComponent, before
        // the controls declared after them in the XAML exist. Without this guard
        // the first SelectionChanged crashed the whole app with a NullReference.
        if (WorkItemsListView is null || ListCountLabel is null) return;

        var filtered = _allWorkItems.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(_listSearchText))
        {
            var search = _listSearchText.ToLowerInvariant();
            filtered = filtered.Where(w =>
                w.Title.ToLowerInvariant().Contains(search) ||
                w.Id.ToString().Contains(search) ||
                (w.AssignedTo?.ToLowerInvariant().Contains(search) ?? false));
        }

        if (!string.IsNullOrEmpty(_listStateFilter))
        {
            filtered = filtered.Where(w => w.State.Equals(_listStateFilter, StringComparison.OrdinalIgnoreCase));
        }

        var items = filtered.ToList();
        WorkItemsListView.ItemsSource = items;
        ListCountLabel.Text = $"{items.Count} work items";
    }

    private void ListSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _listSearchText = ListSearchBox.Text;
        UpdateListDisplay();
    }

    private void ListStateFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ListStateFilter.SelectedItem is ComboBoxItem item)
        {
            _listStateFilter = item.Tag?.ToString();
            if (string.IsNullOrEmpty(_listStateFilter)) _listStateFilter = null;
            UpdateListDisplay();
        }
    }

    private void WorkItemsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WorkItemsListView.SelectedItem is WorkItem wi && _config.AzureDevOps != null)
        {
            var url = $"{_config.AzureDevOps.BaseUrl}/{_config.AzureDevOps.Project}/_workitems/edit/{wi.Id}";
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch { }

            WorkItemsListView.SelectedItem = null;
        }
    }

    // MARK: - Projects Mode (GitHub Projects v2 Dynamic Board)

    private async Task LoadProjectsBoardAsync()
    {
        var ghConfig = _config.Tasks?.Providers?.GitHub;
        if (ghConfig == null || !ghConfig.Enabled)
        {
            ProjectsCountLabel.Text = "GitHub not configured";
            return;
        }

        try
        {
            LoadingOverlay.Visibility = Visibility.Visible;

            _projectsService = new GitHubProjectsService(ghConfig);
            if (!await _projectsService.AuthenticateAsync())
            {
                ProjectsCountLabel.Text = "Auth failed";
                return;
            }

            // Resolve project
            var scope = (ghConfig.ProjectScope?.ToLowerInvariant()) switch
            {
                "user" => ProjectScope.User,
                "repository" or "repo" => ProjectScope.Repository,
                _ => ProjectScope.Organization
            };
            var owner = ghConfig.Organization ?? ghConfig.Owner ?? "";
            string? projectId = null;

            if (ghConfig.ProjectNumber.HasValue)
            {
                var project = await _projectsService.GetProjectAsync(scope, owner, ghConfig.ProjectNumber.Value, ghConfig.Repo);
                projectId = project?.Id;
            }
            else
            {
                var projects = await _projectsService.ListProjectsAsync(scope, owner, ghConfig.Repo, limit: 1);
                projectId = projects.FirstOrDefault()?.Id;
            }

            if (projectId == null)
            {
                ProjectsCountLabel.Text = "No project found";
                return;
            }

            _statusField = await _projectsService.GetStatusFieldAsync(projectId);
            _projectItems = await _projectsService.ListProjectItemsAsync(projectId, 100);

            RenderProjectsBoard();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load project: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private string _projectsSearchText = "";

    private void ProjectsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _projectsSearchText = ProjectsSearchBox.Text;
        RenderProjectsBoard();
    }

    private void RenderProjectsBoard()
    {
        ProjectsColumnsPanel.Children.Clear();

        var items = _projectItems.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(_projectsSearchText))
        {
            var search = _projectsSearchText.ToLowerInvariant();
            items = items.Where(i =>
                (i.Content?.Title?.ToLowerInvariant().Contains(search) ?? false) ||
                (i.DraftContent?.Title?.ToLowerInvariant().Contains(search) ?? false));
        }
        var filteredItems = items.ToList();

        // Build columns from status field options
        var columns = new List<(string Name, System.Windows.Media.Brush Color, List<GitHubProjectItem> Items)>();

        if (_statusField != null)
        {
            foreach (var opt in _statusField.Options)
            {
                columns.Add((opt.Name, GetStatusBrush(opt.Name), new List<GitHubProjectItem>()));
            }
        }
        columns.Add(("(No Status)", new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(113, 128, 150)), new List<GitHubProjectItem>()));

        foreach (var item in filteredItems)
        {
            var statusValue = item.FieldValues
                .FirstOrDefault(fv => fv.FieldName.Equals("Status", StringComparison.OrdinalIgnoreCase))
                ?.SingleSelectValue;

            var col = columns.FirstOrDefault(c => c.Name.Equals(statusValue, StringComparison.OrdinalIgnoreCase));
            if (col.Items != null)
                col.Items.Add(item);
            else
                columns.Last().Items.Add(item);
        }

        // Remove empty "(No Status)" column
        if (columns.Last().Items.Count == 0)
            columns.RemoveAt(columns.Count - 1);

        foreach (var col in columns)
        {
            var columnBorder = new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("SystemControlBackgroundListLowBrush"),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 0, 12, 0),
                MinWidth = 250,
                MaxWidth = 300,
                Padding = new Thickness(8)
            };

            var columnPanel = new StackPanel();

            // Column header
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 4, 4, 8) };
            headerPanel.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 10, Height = 10, Fill = col.Color,
                VerticalAlignment = VerticalAlignment.Center
            });
            headerPanel.Children.Add(new TextBlock
            {
                Text = col.Name, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
            });
            headerPanel.Children.Add(new TextBlock
            {
                Text = $"({col.Items.Count})", Opacity = 0.6,
                Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
            });
            columnPanel.Children.Add(headerPanel);

            // Cards
            foreach (var item in col.Items)
            {
                var card = CreateProjectCard(item);
                columnPanel.Children.Add(card);
            }

            columnBorder.Child = columnPanel;
            ProjectsColumnsPanel.Children.Add(columnBorder);
        }

        ProjectsCountLabel.Text = $"{filteredItems.Count} items across {columns.Count} columns";
    }

    private Border CreateProjectCard(GitHubProjectItem item)
    {
        var title = item.Content?.Title ?? item.DraftContent?.Title ?? "(untitled)";
        var typeIcon = item.Type switch
        {
            "ISSUE" => "●",
            "PULL_REQUEST" => "⊙",
            "DRAFT_ISSUE" => "○",
            _ => "?"
        };
        var typeColor = item.Type switch
        {
            "ISSUE" => System.Windows.Media.Colors.Green,
            "PULL_REQUEST" => System.Windows.Media.Colors.Purple,
            _ => System.Windows.Media.Colors.Gray
        };

        var cardPanel = new StackPanel();

        // Title row
        var titlePanel = new StackPanel { Orientation = Orientation.Horizontal };
        titlePanel.Children.Add(new TextBlock
        {
            Text = typeIcon,
            Foreground = new System.Windows.Media.SolidColorBrush(typeColor),
            Margin = new Thickness(0, 0, 6, 0)
        });
        titlePanel.Children.Add(new TextBlock
        {
            Text = title, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, MaxWidth = 220
        });
        cardPanel.Children.Add(titlePanel);

        // Assignees
        if (item.Content?.Assignees.Count > 0)
        {
            cardPanel.Children.Add(new TextBlock
            {
                Text = "  @" + string.Join(", ", item.Content.Assignees.Take(2)),
                FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 2, 0, 0)
            });
        }

        // Labels
        if (item.Content?.Labels.Count > 0)
        {
            var labelPanel = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            foreach (var label in item.Content.Labels.Take(3))
            {
                var labelBorder = new Border
                {
                    Background = (System.Windows.Media.Brush)FindResource("SystemControlBackgroundBaseLowBrush"),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(0, 0, 4, 0)
                };
                labelBorder.Child = new TextBlock { Text = label, FontSize = 10 };
                labelPanel.Children.Add(labelBorder);
            }
            cardPanel.Children.Add(labelPanel);
        }

        var card = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("SystemControlBackgroundListLowBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        card.Child = cardPanel;

        // Click to open URL
        card.MouseLeftButtonUp += (_, _) =>
        {
            var url = item.Content?.Url;
            if (!string.IsNullOrEmpty(url))
            {
                try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
                catch { }
            }
        };

        return card;
    }

    private static System.Windows.Media.Brush GetStatusBrush(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("done") || lower.Contains("closed"))
            return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(56, 161, 105));
        if (lower.Contains("progress") || lower.Contains("active"))
            return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(49, 130, 206));
        if (lower.Contains("review"))
            return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(128, 90, 213));
        if (lower.Contains("backlog"))
            return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(160, 174, 192));
        if (lower.Contains("todo"))
            return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(237, 137, 54));
        return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(214, 158, 46));
    }

    // MARK: - SSO

    private void OnSsoButtonClicked(object sender, RoutedEventArgs e)
    {
        if (_app == null) return;

        if (_app.IsDevOpsSsoAuthenticated)
        {
            // Already signed in — sign out
            _app.SignOutDevOpsSso();
            UpdateSsoButtonState();
        }
        else
        {
            // Launch OAuth2 PKCE SSO flow
            _app.ShowDevOpsSsoLogin(success =>
            {
                UpdateSsoButtonState();
                if (success)
                {
                    // Reload work items with new auth
                    _ = LoadWorkItemsAsync();
                }
            });
        }
    }

    private void UpdateSsoButtonState()
    {
        if (_app == null) return;

        if (_app.IsDevOpsSsoAuthenticated)
        {
            SsoIcon.Text = "🔓";
            SsoLabel.Text = _app.DevOpsAuthenticatedUserName ?? "Signed In";
            SsoButton.ToolTip = "Click to sign out of Azure DevOps SSO";
        }
        else
        {
            SsoIcon.Text = "🔒";
            SsoLabel.Text = "Sign In";
            SsoButton.ToolTip = "Sign in to Azure DevOps via SSO";
        }
    }
}


/// <summary>One board column: title, accent color, and its task cards.</summary>
public sealed class TaskBoardColumn
{
    public string Title { get; init; } = "";
    public Brush Color { get; init; } = Brushes.Gray;
    public string CountLabel { get; init; } = "";
    public List<TaskCardVm> Tasks { get; init; } = new();
}

/// <summary>Card view-model wrapping a UnifiedTask for the board.</summary>
public sealed class TaskCardVm
{
    public required UnifiedTask Task { get; init; }

    public string Title => Task.Title;

    public string ProviderName => Task.Provider switch
    {
        "azdevops" => "DevOps",
        "github" => "GitHub",
        "gitea" => "Gitea",
        _ => Task.Provider
    };

    public string TypeName => Meta("workItemType");
    public Visibility TypeVisibility => Vis(TypeName);

    public string AreaPath => Meta("areaPath");
    public Visibility AreaVisibility => Vis(AreaPath);

    public string Iteration
    {
        get
        {
            var iteration = Meta("iterationPath");
            return string.IsNullOrEmpty(iteration) ? Task.Bucket ?? "" : iteration;
        }
    }
    public Visibility IterationVisibility => Vis(Iteration);

    public string AssigneesLabel => Task.Assignees.Count > 0 ? "@ " + string.Join(", ", Task.Assignees) : "";
    public Visibility AssigneesVisibility => Vis(AssigneesLabel);

    private string Meta(string key) => Task.Metadata.TryGetValue(key, out var value) ? value : "";
    private static Visibility Vis(string s) => string.IsNullOrEmpty(s) ? Visibility.Collapsed : Visibility.Visible;
}
