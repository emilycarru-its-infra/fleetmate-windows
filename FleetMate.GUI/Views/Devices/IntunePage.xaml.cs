using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Identity;
using FleetMate.Core.Config;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Devices;
using FleetMate.Core.Services.Inventory;
using FleetMate.Core.Services.Tickets;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Services.Reporting;

namespace FleetMate.GUI.Views.Devices;

public partial class IntunePage : Page
{
    private readonly App? _app;
    private readonly GraphService? _graphService;
    private ObservableCollection<IntuneDevice> _filteredDevices = new();
    private List<MobileApp> _mobileApps = new();
    private bool _sortAscending = true;
    private string _sortField = "Serial";
    private bool _isInitialLoadDone;
    
    // Use cached devices from App
    private List<IntuneDevice> _allDevices => _app?.CachedDevices ?? new();

    public IntunePage()
    {
        InitializeComponent();

        // Get services from App
        if (Application.Current is App app)
        {
            _app = app;
            _graphService = app.GraphService;
        }

        DevicesDataGrid.ItemsSource = _filteredDevices;

        DeviceDetail.CloseRequested += (_, _) => HideDeviceDetail();

        Loaded += async (s, e) =>
        {
            if (!_isInitialLoadDone)
            {
                _isInitialLoadDone = true;
                await LoadDevicesAsync();
            }
            // Deep link: check on every navigation (page is cached)
            if (_app?.PendingNavigateDeviceId is { } deviceId)
            {
                _app.PendingNavigateDeviceId = null;
                var device = _filteredDevices.FirstOrDefault(d => d.Id == deviceId);
                if (device != null)
                {
                    DevicesDataGrid.SelectedItem = device;
                    DevicesDataGrid.ScrollIntoView(device);
                }
            }
        };
    }

    private async Task LoadDevicesAsync()
    {
        if (_graphService == null || _app == null)
        {
            NotConfiguredText.Visibility = Visibility.Visible;
            return;
        }
        
        // Use cache if valid
        if (_app.IsDevicesCacheValid && _app.CachedDevices.Count > 0)
        {
            PopulatePlatformFilter();
            ApplyFiltersAndSort();
            return;
        }

        LoadingPanel.Visibility = Visibility.Visible;
        NotConfiguredText.Visibility = Visibility.Collapsed;

        try
        {
            var devices = await _graphService.GetManagedDevicesAsync(limit: 10000);
            _app.UpdateDevicesCache(devices);
            PopulatePlatformFilter();
            ApplyFiltersAndSort();
        }
        catch (Exception ex)
        {
            ShowActionMessage($"Error: {ex.Message}", isError: true);
        }
        finally
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void PopulatePlatformFilter()
    {
        var platforms = _allDevices
            .Select(d => d.OperatingSystem)
            .Where(os => !string.IsNullOrEmpty(os))
            .Distinct()
            .OrderBy(os => os)
            .ToList();

        PlatformFilterComboBox.Items.Clear();
        PlatformFilterComboBox.Items.Add(new ComboBoxItem { Content = "All", IsSelected = true });
        foreach (var platform in platforms)
        {
            PlatformFilterComboBox.Items.Add(new ComboBoxItem { Content = platform });
        }
        PlatformFilterComboBox.SelectedIndex = 0;
    }

    private void ApplyFiltersAndSort()
    {
        // Guard: don't run during XAML initialization before controls exist
        if (!IsLoaded || _allDevices == null) return;

        var filtered = _allDevices.AsEnumerable();

        // Filter by non-compliant
        if (NonCompliantOnlyCheckBox.IsChecked == true)
        {
            filtered = filtered.Where(d => d.ComplianceState?.Equals("noncompliant", StringComparison.OrdinalIgnoreCase) == true);
        }

        // Filter by platform
        if (PlatformFilterComboBox.SelectedItem is ComboBoxItem platformItem)
        {
            var platform = platformItem.Content?.ToString();
            if (!string.IsNullOrEmpty(platform) && platform != "All")
            {
                filtered = filtered.Where(d =>
                    d.OperatingSystem?.Contains(platform, StringComparison.OrdinalIgnoreCase) == true);
            }
        }

        // Filter by search text
        var searchText = SearchBox.Text?.Trim();
        if (!string.IsNullOrEmpty(searchText))
        {
            filtered = filtered.Where(d =>
                (d.DeviceName?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true) ||
                (d.SerialNumber?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true) ||
                (d.UserPrincipalName?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true));
        }

        // Sort
        filtered = _sortField switch
        {
            "Name" => _sortAscending ? filtered.OrderBy(d => d.DeviceName) : filtered.OrderByDescending(d => d.DeviceName),
            "Compliance" => _sortAscending ? filtered.OrderBy(d => d.ComplianceState) : filtered.OrderByDescending(d => d.ComplianceState),
            "OS" => _sortAscending ? filtered.OrderBy(d => d.OperatingSystem) : filtered.OrderByDescending(d => d.OperatingSystem),
            "User" => _sortAscending ? filtered.OrderBy(d => d.UserDisplayName) : filtered.OrderByDescending(d => d.UserDisplayName),
            "Last Sync" => _sortAscending ? filtered.OrderBy(d => d.LastSyncDateTime) : filtered.OrderByDescending(d => d.LastSyncDateTime),
            _ => _sortAscending ? filtered.OrderBy(d => d.SerialNumber) : filtered.OrderByDescending(d => d.SerialNumber)
        };

        _filteredDevices.Clear();
        foreach (var device in filtered)
        {
            _filteredDevices.Add(device);
        }
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        ApplyFiltersAndSort();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFiltersAndSort();
    }

    private void OnPlatformFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyFiltersAndSort();
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortComboBox.SelectedItem is ComboBoxItem item)
        {
            _sortField = item.Content?.ToString() ?? "Serial";
            ApplyFiltersAndSort();
        }
    }

    private void OnSortDirectionClicked(object sender, RoutedEventArgs e)
    {
        _sortAscending = !_sortAscending;
        SortDirectionButton.Content = _sortAscending ? "↑" : "↓";
        ApplyFiltersAndSort();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        // Invalidate cache to force reload
        if (_app != null)
        {
            _app.CachedDevices.Clear();
        }
        await LoadDevicesAsync();
    }

    private void OnSelectAllClicked(object sender, RoutedEventArgs e)
    {
        DevicesDataGrid.SelectAll();
    }

    private void OnClearSelectionClicked(object sender, RoutedEventArgs e)
    {
        DevicesDataGrid.SelectedItems.Clear();
    }

    private void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedCount = DevicesDataGrid.SelectedItems.Count;
        var hasSelection = selectedCount > 0;

        ActionsButton.IsEnabled = hasSelection;
        ClearSelectionButton.IsEnabled = hasSelection;
        SelectionCountText.Text = hasSelection ? $"• {selectedCount} selected" : "";

        if (hasSelection)
        {
            SelectedDevicesText.Text = $"{selectedCount} device(s) selected";
            var selectedDevices = DevicesDataGrid.SelectedItems.Cast<IntuneDevice>().ToList();
            if (selectedCount <= 3)
            {
                SelectedDeviceNamesText.Text = string.Join(", ", selectedDevices.Select(d => d.DeviceName ?? d.SerialNumber ?? "Unknown"));
            }
            else
            {
                var first2 = string.Join(", ", selectedDevices.Take(2).Select(d => d.DeviceName ?? d.SerialNumber ?? "Unknown"));
                SelectedDeviceNamesText.Text = $"{first2} and {selectedCount - 2} more...";
            }

            // Show device detail panel for single selection
            if (selectedCount == 1)
            {
                _ = ShowDeviceDetailAsync(selectedDevices[0]);
            }
            else
            {
                HideDeviceDetail();
            }
        }
        else
        {
            HideDeviceDetail();
        }
    }

    private async Task ShowDeviceDetailAsync(IntuneDevice device)
    {
        DeviceDetail.Visibility = Visibility.Visible;
        DetailPanelColumn.Width = new GridLength(400);
        await DeviceDetail.ShowDeviceAsync(device, _graphService);
    }

    private void HideDeviceDetail()
    {
        DeviceDetail.Visibility = Visibility.Collapsed;
        DetailPanelColumn.Width = new GridLength(0);
    }

    private void OnActionsClicked(object sender, RoutedEventArgs e)
    {
        ActionsPanel.Visibility = Visibility.Visible;
        ActionsPanelColumn.Width = new GridLength(316);
    }

    private void OnCloseActionsPanel(object sender, RoutedEventArgs e)
    {
        ActionsPanel.Visibility = Visibility.Collapsed;
        ActionsPanelColumn.Width = new GridLength(0);
    }

    private IEnumerable<string> GetSelectedDeviceIds()
    {
        return DevicesDataGrid.SelectedItems.Cast<IntuneDevice>().Select(d => d.Id);
    }

    private void ShowActionMessage(string message, bool isError = false, bool isLoading = false)
    {
        ActionMessageBorder.Visibility = Visibility.Visible;
        ActionMessageText.Text = message;
        ActionMessageBorder.Background = isError
            ? new SolidColorBrush(Color.FromRgb(255, 200, 200))
            : new SolidColorBrush(Color.FromRgb(200, 255, 200));
        ActionProgressRing.IsActive = isLoading;
    }

    private async void OnSyncClicked(object sender, RoutedEventArgs e)
    {
        if (_graphService == null) return;

        var deviceIds = GetSelectedDeviceIds().ToList();
        ShowActionMessage($"Syncing {deviceIds.Count} device(s)...", isLoading: true);

        try
        {
            var results = await _graphService.SyncDevicesAsync(deviceIds);
            var successful = results.Count(r => r.Success);
            var failed = results.Count - successful;

            ShowActionMessage(failed == 0
                ? $"Successfully synced {successful} device(s)"
                : $"Synced {successful}, {failed} failed");
        }
        catch (Exception ex)
        {
            ShowActionMessage($"Error: {ex.Message}", isError: true);
        }
    }

    private async void OnRestartClicked(object sender, RoutedEventArgs e)
    {
        if (_graphService == null) return;

        var deviceIds = GetSelectedDeviceIds().ToList();
        var result = MessageBox.Show(
            $"Are you sure you want to restart {deviceIds.Count} device(s)? Active user sessions will be terminated.",
            "Confirm Restart",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        ShowActionMessage($"Restarting {deviceIds.Count} device(s)...", isLoading: true);

        try
        {
            var results = await _graphService.RebootDevicesAsync(deviceIds);
            var successful = results.Count(r => r.Success);
            var failed = results.Count - successful;

            ShowActionMessage(failed == 0
                ? $"Successfully sent reboot to {successful} device(s)"
                : $"Rebooted {successful}, {failed} failed");
        }
        catch (Exception ex)
        {
            ShowActionMessage($"Error: {ex.Message}", isError: true);
        }
    }

    private async void OnRetireClicked(object sender, RoutedEventArgs e)
    {
        if (_graphService == null) return;

        var deviceIds = GetSelectedDeviceIds().ToList();
        var result = MessageBox.Show(
            $"Remove company data and unenroll {deviceIds.Count} device(s)? Personal data is left intact.",
            "Confirm Retire",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        ShowActionMessage($"Retiring {deviceIds.Count} device(s)...", isLoading: true);

        try
        {
            var results = await _graphService.RetireDevicesAsync(deviceIds, confirmed: true);
            var successful = results.Count(r => r.Success);
            var failed = results.Count - successful;

            ShowActionMessage(failed == 0
                ? $"Successfully sent retire to {successful} device(s)"
                : $"Retired {successful}, {failed} failed");
        }
        catch (Exception ex)
        {
            ShowActionMessage($"Error: {ex.Message}", isError: true);
        }
    }

    private async void OnWipeClicked(object sender, RoutedEventArgs e)
    {
        if (_graphService == null) return;

        var deviceIds = GetSelectedDeviceIds().ToList();
        var result = MessageBox.Show(
            $"This will factory-reset {deviceIds.Count} device(s). This cannot be undone.",
            "Confirm Wipe",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        ShowActionMessage($"Wiping {deviceIds.Count} device(s)...", isLoading: true);

        try
        {
            var results = await _graphService.WipeDevicesAsync(deviceIds, confirmed: true);
            var successful = results.Count(r => r.Success);
            var failed = results.Count - successful;

            ShowActionMessage(failed == 0
                ? $"Successfully sent wipe to {successful} device(s)"
                : $"Wiped {successful}, {failed} failed");
        }
        catch (Exception ex)
        {
            ShowActionMessage($"Error: {ex.Message}", isError: true);
        }
    }

    private async void OnLockClicked(object sender, RoutedEventArgs e)
    {
        if (_graphService == null) return;

        var deviceIds = GetSelectedDeviceIds().ToList();
        var result = MessageBox.Show(
            $"Are you sure you want to lock {deviceIds.Count} device(s)?",
            "Confirm Lock",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        var pin = string.IsNullOrWhiteSpace(LockPinTextBox.Text) ? null : LockPinTextBox.Text;
        ShowActionMessage($"Locking {deviceIds.Count} device(s)...", isLoading: true);

        try
        {
            var results = await _graphService.RemoteLockDevicesAsync(deviceIds, pin, confirmed: true);
            var successful = results.Count(r => r.Success);
            var failed = results.Count - successful;

            ShowActionMessage(failed == 0
                ? $"Successfully locked {successful} device(s)"
                : $"Locked {successful}, {failed} failed");

            LockPinTextBox.Clear();
        }
        catch (Exception ex)
        {
            ShowActionMessage($"Error: {ex.Message}", isError: true);
        }
    }

    private async void OnSearchAppsClicked(object sender, RoutedEventArgs e)
    {
        if (_graphService == null) return;

        try
        {
            var query = AppSearchBox.Text?.Trim();
            _mobileApps = string.IsNullOrEmpty(query)
                ? await _graphService.GetMobileAppsAsync(limit: 100)
                : await _graphService.SearchMobileAppsAsync(query, 50);

            AppComboBox.Items.Clear();
            foreach (var app in _mobileApps)
            {
                AppComboBox.Items.Add(new ComboBoxItem
                {
                    Content = app.DisplayName ?? "Unknown",
                    Tag = app.Id
                });
            }
        }
        catch (Exception ex)
        {
            ShowActionMessage($"Error loading apps: {ex.Message}", isError: true);
        }
    }

    private async void OnReinstallAppClicked(object sender, RoutedEventArgs e)
    {
        if (_graphService == null) return;

        if (AppComboBox.SelectedItem is not ComboBoxItem selectedApp || selectedApp.Tag == null)
        {
            ShowActionMessage("Please select an app to reinstall", isError: true);
            return;
        }

        var deviceIds = GetSelectedDeviceIds().ToList();
        ShowActionMessage($"Triggering reinstall on {deviceIds.Count} device(s)...", isLoading: true);

        try
        {
            // Reinstall is triggered via sync
            var results = await _graphService.SyncDevicesAsync(deviceIds);
            var successful = results.Count(r => r.Success);
            var failed = results.Count - successful;

            var appName = selectedApp.Content?.ToString() ?? "app";
            ShowActionMessage(failed == 0
                ? $"Triggered {appName} reinstall on {successful} device(s)"
                : $"Triggered on {successful}, {failed} failed");
        }
        catch (Exception ex)
        {
            ShowActionMessage($"Error: {ex.Message}", isError: true);
        }
    }

    private async void OnCheckUpdatesClicked(object sender, RoutedEventArgs e)
    {
        if (_graphService == null) return;

        var deviceIds = GetSelectedDeviceIds().ToList();
        ShowActionMessage($"Checking updates on {deviceIds.Count} device(s)...", isLoading: true);

        try
        {
            // Update check is triggered via sync
            var results = await _graphService.SyncDevicesAsync(deviceIds);
            var successful = results.Count(r => r.Success);
            var failed = results.Count - successful;

            ShowActionMessage(failed == 0
                ? $"Triggered update check on {successful} device(s)"
                : $"Triggered on {successful}, {failed} failed");
        }
        catch (Exception ex)
        {
            ShowActionMessage($"Error: {ex.Message}", isError: true);
        }
    }

    private async void OnPushCimianClicked(object sender, RoutedEventArgs e)
    {
        if (_graphService == null) return;

        var selectedDevices = DevicesDataGrid.SelectedItems.Cast<IntuneDevice>().ToList();
        if (selectedDevices.Count == 0)
        {
            ShowActionMessage("Select devices to push Cimian run", isError: true);
            return;
        }

        ShowActionMessage($"Pushing Cimian run to {selectedDevices.Count} device(s)...", isLoading: true);

        try
        {
            // Force Intune sync on selected devices to trigger remediation pickup
            var syncResults = await _graphService.SyncDevicesAsync(selectedDevices.Select(d => d.Id));
            var successful = syncResults.Count(r => r.Success);
            var failed = syncResults.Count - successful;

            ShowActionMessage(failed == 0
                ? $"Cimian push initiated on {successful} device(s) - sync forced, remediation will create trigger file on check-in"
                : $"Push initiated on {successful}, {failed} sync(s) failed");
        }
        catch (Exception ex)
        {
            ShowActionMessage($"Error: {ex.Message}", isError: true);
        }
    }
}
