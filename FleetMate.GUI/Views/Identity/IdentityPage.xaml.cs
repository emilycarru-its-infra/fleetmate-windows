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
        // GroupsRadio is checked in markup, so this fires mid-parse — before
        // the panels it toggles have been created.
        if (!IsInitialized) return;

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
            var groups = await _graphService.SearchGroupsAsync("Devices-", DeviceGroupFetch.Limit);
            
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
        UsersSplit.Visibility = Visibility.Collapsed;
        UsersPlaceholderText.Visibility = Visibility.Collapsed;
        UsersNotConfiguredText.Visibility = Visibility.Collapsed;

        try
        {
            var results = new List<FleetMate.Core.Models.Identity.EntraUser>();

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
                UsersListView.ItemsSource = results
                    .Select(u => new EntraUserViewModel { User = u })
                    .ToList();
                UsersSplit.Visibility = Visibility.Visible;

                // Open the first result. A single hit is the common case, and
                // making the operator click it again is a step for nothing.
                UsersListView.SelectedIndex = 0;
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

    // MARK: - User inspector

    private async void OnUserSelected(object sender, SelectionChangedEventArgs e)
    {
        if (UsersListView.SelectedItem is not EntraUserViewModel vm)
        {
            UserDetailPane.Visibility = Visibility.Collapsed;
            UserDetailPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        UserDetailPlaceholder.Visibility = Visibility.Collapsed;
        UserDetailPane.Visibility = Visibility.Visible;

        UserInitialsText.Text = vm.Initials;
        UserNameText.Text = vm.DisplayName;
        UserUpnText.Text = vm.UserPrincipalName;
        UserSubtitleText.Text = vm.Subtitle;
        UserBadgeText.Text = vm.AccountBadgeText;
        UserBadgeText.Foreground = vm.AccountBadgeBrush;
        ToggleAccountButton.Content = vm.User.AccountEnabled == false ? "Enable Account" : "Disable Account";
        UserPropertiesControl.ItemsSource = vm.Sections;

        await LoadUserRelationsAsync(vm);
    }

    /// <summary>
    /// Fill in the Devices and Groups tabs.
    ///
    /// Both are fetched per selection rather than with the search results: the
    /// search can return many users and neither tab is visible until one is
    /// picked, so doing this eagerly would be N round trips to render one.
    /// </summary>
    private async Task LoadUserRelationsAsync(EntraUserViewModel vm)
    {
        if (_graphService == null) return;

        var upn = vm.UserPrincipalName;

        UserDevicesEmpty.Text = "Loading devices…";
        UserDevicesEmpty.Visibility = Visibility.Visible;
        UserGroupsEmpty.Text = "Loading groups…";
        UserGroupsEmpty.Visibility = Visibility.Visible;
        UserDevicesList.ItemsSource = null;
        UserGroupsList.ItemsSource = null;

        try
        {
            var devicesTask = _graphService.GetUserDevicesAsync(upn);
            var groupsTask = vm.User.MemberOf is { Count: > 0 } cached
                ? Task.FromResult(cached)
                : _graphService.GetUserGroupsAsync(upn);

            await Task.WhenAll(devicesTask, groupsTask);

            // The selection can move while these are in flight; a late reply
            // must not overwrite the pane with another user's data.
            if (UsersListView.SelectedItem is not EntraUserViewModel current
                || current.UserPrincipalName != upn)
            {
                return;
            }

            var devices = await devicesTask;
            UserDevicesList.ItemsSource = devices;
            UserDevicesEmpty.Text = "No Intune devices are assigned to this user.";
            UserDevicesEmpty.Visibility = devices.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

            var groups = await groupsTask;
            UserGroupsList.ItemsSource = groups;
            UserGroupsEmpty.Text = "This user is not a member of any groups.";
            UserGroupsEmpty.Visibility = groups.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex)
        {
            UserDevicesEmpty.Text = $"Could not load devices: {ex.Message}";
            UserGroupsEmpty.Text = $"Could not load groups: {ex.Message}";
        }
    }

    // ── User actions (enable/disable, group membership) ───────────

    private EntraUserViewModel? SelectedUserVm => UsersListView.SelectedItem as EntraUserViewModel;

    private async void OnToggleAccountClicked(object sender, RoutedEventArgs e)
    {
        if (_graphService == null || SelectedUserVm is not { } vm) return;
        var enable = vm.User.AccountEnabled == false;
        var verb = enable ? "Enable" : "Disable";
        if (MessageBox.Show($"{verb} the account {vm.UserPrincipalName}?", $"Confirm {verb}",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var ok = await _graphService.SetUserAccountEnabledAsync(vm.User.Id, enable);
        if (!ok)
        {
            MessageBox.Show("The account update failed — see the FleetMate log for detail.",
                "Account", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        vm.User.AccountEnabled = enable;
        UserBadgeText.Text = vm.AccountBadgeText;
        UserBadgeText.Foreground = vm.AccountBadgeBrush;
        ToggleAccountButton.Content = enable ? "Disable Account" : "Enable Account";
        UsersListView.Items.Refresh();
    }

    private async void OnAddToGroupClicked(object sender, RoutedEventArgs e)
    {
        if (_graphService == null || SelectedUserVm is not { } vm) return;
        var group = AddGroupBox.Text?.Trim();
        if (string.IsNullOrEmpty(group)) return;

        var ok = await _graphService.AddGroupMemberAsync(group, vm.User.Id);
        if (!ok)
        {
            MessageBox.Show($"Could not add {vm.DisplayName} to '{group}'.",
                "Groups", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        AddGroupBox.Text = "";
        vm.User.MemberOf = new List<EntraGroup>();
        await LoadUserRelationsAsync(vm);
    }

    private async void OnRemoveFromGroupClicked(object sender, RoutedEventArgs e)
    {
        if (_graphService == null || SelectedUserVm is not { } vm) return;
        if ((sender as Button)?.Tag is not EntraGroup group) return;
        if (MessageBox.Show($"Remove {vm.DisplayName} from '{group.DisplayName}'?", "Confirm Remove",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var ok = await _graphService.RemoveGroupMemberAsync(group.Id, vm.User.Id);
        if (!ok)
        {
            MessageBox.Show($"Could not remove {vm.DisplayName} from '{group.DisplayName}'.",
                "Groups", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        vm.User.MemberOf = new List<EntraGroup>();
        await LoadUserRelationsAsync(vm);
    }
}
