using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using FleetMate.Config;
using FleetMate.Core.Models.Tasks;
using FleetMate.Core.Services.Tasks;

namespace FleetMate.GUI.Views;

public partial class BoardsPage : Page
{
    private readonly FleetMateConfig _config;
    private TaskProviderRegistry? _registry;
    private List<UnifiedTask> _allTasks = new();
    private string? _filterProvider;
    private string? _filterBucket;
    private string _searchText = "";
    private bool _showClosed;

    public BoardsPage()
    {
        InitializeComponent();
        _config = FleetMateConfig.Load();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeRegistryAsync();
        await LoadBucketsAsync();
        await LoadTasksAsync();
    }

    private async Task InitializeRegistryAsync()
    {
        _registry = new TaskProviderRegistry();

        var azdo = new AzureDevOpsTaskProvider(_config);
        var github = new GitHubTaskProvider(_config);
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
}
