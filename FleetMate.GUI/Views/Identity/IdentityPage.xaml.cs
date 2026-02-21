using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Identity;
using FleetMate.Core.Config;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Devices;
using FleetMate.Core.Services.Inventory;
using FleetMate.Core.Services.Tickets;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Services.Reporting;

namespace FleetMate.GUI.Views.Identity;

public partial class IdentityPage : Page
{
    private readonly App? _app;
    private readonly GraphService? _graphService;
    private bool _isInitialLoadDone;

    public IdentityPage()
    {
        InitializeComponent();

        if (Application.Current is App app)
        {
            _app = app;
            _graphService = app.GraphService;
        }

        Loaded += async (s, e) =>
        {
            if (!_isInitialLoadDone)
            {
                _isInitialLoadDone = true;
                await LoadGroupsAsync();
            }
        };
    }

    private void OnTabChanged(object sender, RoutedEventArgs e)
    {
        var isGroups = GroupsRadio.IsChecked == true;
        GroupsPanel.Visibility = isGroups ? Visibility.Visible : Visibility.Collapsed;
        UsersPanel.Visibility = isGroups ? Visibility.Collapsed : Visibility.Visible;
    }

    // MARK: - Groups

    private async Task LoadGroupsAsync()
    {
        if (_graphService == null)
        {
            GroupsNotConfiguredText.Visibility = Visibility.Visible;
            return;
        }

        GroupsLoadingPanel.Visibility = Visibility.Visible;
        GroupsNotConfiguredText.Visibility = Visibility.Collapsed;

        try
        {
            var groups = await _graphService.SearchGroupsAsync("Devices-", 100);
            
            GroupsTreeView.Items.Clear();
            foreach (var group in groups.OrderBy(g => g.DisplayName))
            {
                var item = new TreeViewItem
                {
                    Header = $"{group.DisplayName} ({group.Description ?? ""})",
                    Tag = group.Id
                };
                // Add dummy child for expand arrow
                item.Items.Add("Loading...");
                item.Expanded += OnGroupExpanded;
                GroupsTreeView.Items.Add(item);
            }
        }
        catch (Exception ex)
        {
            GroupsNotConfiguredText.Text = $"Error: {ex.Message}";
            GroupsNotConfiguredText.Visibility = Visibility.Visible;
        }
        finally
        {
            GroupsLoadingPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnGroupExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem item || item.Tag is not string groupId) return;
        
        // If already loaded real data, skip
        if (item.Items.Count > 0 && item.Items[0] is not string) return;
        
        item.Items.Clear();

        try
        {
            var devices = await _graphService!.GetGroupDevicesAsync(groupId);
            if (devices.Count == 0)
            {
                item.Items.Add(new TreeViewItem { Header = "(no devices)" });
            }
            else
            {
                foreach (var device in devices.OrderBy(d => d.DeviceName))
                {
                    item.Items.Add(new TreeViewItem
                    {
                        Header = $"💻 {device.DeviceName} — {device.OperatingSystem} {device.OsVersion}"
                    });
                }
            }
        }
        catch (Exception ex)
        {
            item.Items.Add(new TreeViewItem { Header = $"Error: {ex.Message}" });
        }
    }

    private void OnGroupsSearchChanged(object sender, TextChangedEventArgs e)
    {
        var search = GroupsSearchBox.Text?.Trim().ToLower() ?? "";
        foreach (var obj in GroupsTreeView.Items)
        {
            if (obj is TreeViewItem item)
            {
                var header = item.Header?.ToString()?.ToLower() ?? "";
                item.Visibility = string.IsNullOrEmpty(search) || header.Contains(search)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
    }

    private async void OnGroupsRefreshClicked(object sender, RoutedEventArgs e)
    {
        await LoadGroupsAsync();
    }

    // MARK: - Users

    private void OnUsersSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = SearchUsersAsync();
    }

    private async void OnUsersSearchClicked(object sender, RoutedEventArgs e)
    {
        await SearchUsersAsync();
    }

    private async Task SearchUsersAsync()
    {
        var query = UsersSearchBox.Text?.Trim();
        if (string.IsNullOrEmpty(query) || _graphService == null)
        {
            if (_graphService == null) UsersNotConfiguredText.Visibility = Visibility.Visible;
            return;
        }

        UsersLoadingPanel.Visibility = Visibility.Visible;
        UsersListView.Visibility = Visibility.Collapsed;
        UsersPlaceholderText.Visibility = Visibility.Collapsed;
        UsersNotConfiguredText.Visibility = Visibility.Collapsed;

        try
        {
            var results = new List<FleetMate.Models.Graph.EntraUser>();

            // Try exact lookup first (for UPN, email, or UUID)
            if (query.Contains('@') || Guid.TryParse(query, out _))
            {
                var exactUser = await _graphService.GetUserAsync(query, includeGroups: true);
                if (exactUser != null)
                {
                    results.Add(exactUser);
                }
            }

            // Fall back to fuzzy search if no exact match
            if (results.Count == 0)
            {
                results = await _graphService.SearchUsersAsync(query);
            }

            if (results.Count > 0)
            {
                UsersListView.ItemsSource = results;
                UsersListView.Visibility = Visibility.Visible;
            }
            else
            {
                UsersPlaceholderText.Text = $"No users found matching '{query}'";
                UsersPlaceholderText.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            UsersPlaceholderText.Text = $"Search error: {ex.Message}";
            UsersPlaceholderText.Visibility = Visibility.Visible;
        }
        finally
        {
            UsersLoadingPanel.Visibility = Visibility.Collapsed;
        }
    }
}
