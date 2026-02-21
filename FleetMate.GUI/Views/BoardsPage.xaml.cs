using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using FleetMate.Config;
using FleetMate.Core.Models.GitHub;
using FleetMate.Core.Models.Tasks;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Tasks;
using FleetMate.Models.AzureDevOps;
using FleetMate.Services;

namespace FleetMate.GUI.Views;

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
        _config = FleetMateConfig.Load();
        _app = Application.Current as App;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isInitialLoadDone) return;
        _isInitialLoadDone = true;

        // Initialize AzDO service for list mode
        if (_config.AzureDevOps != null && !string.IsNullOrEmpty(_config.AzureDevOps.Organization))
        {
            _devOpsService = new AzureDevOpsService(_config.AzureDevOps);
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
        var github = new GitHubProjectsTaskProvider(_config);
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

        var tasks = filtered.ToList();

        // Separate by state
        var openTasks = tasks.Where(t => t.State == TaskState.Open).ToList();
        var inProgressTasks = tasks.Where(t => t.State == TaskState.InProgress).ToList();
        var closedTasks = tasks.Where(t => t.State == TaskState.Closed).ToList();

        // Update lists
        OpenTasksList.ItemsSource = openTasks;
        InProgressTasksList.ItemsSource = inProgressTasks;
        ClosedTasksList.ItemsSource = _showClosed ? closedTasks : closedTasks.Take(10).ToList();

        // Update counts
        OpenCount.Text = $"({openTasks.Count})";
        InProgressCount.Text = $"({inProgressTasks.Count})";
        ClosedCount.Text = $"({closedTasks.Count})";

        TaskCountLabel.Text = $"{tasks.Count} tasks";
    }

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

    private void TasksList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView listView && listView.SelectedItem is UnifiedTask task)
        {
            // Open external URL if available
            if (!string.IsNullOrEmpty(task.ExternalUrl))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = task.ExternalUrl,
                        UseShellExecute = true
                    });
                }
                catch { }
            }

            // Clear selection
            listView.SelectedItem = null;
        }
    }

    // MARK: - View Mode Toggle

    private async void OnViewModeChanged(object sender, RoutedEventArgs e)
    {
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
