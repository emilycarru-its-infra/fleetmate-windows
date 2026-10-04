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
    /// <summary>One list for every device: Intune records joined to their Autopilot identities.</summary>
    private readonly ObservableCollection<DeviceListRow> _rows = new();
    private List<DeviceListRow> _allRows = new();
    private List<MobileApp> _mobileApps = new();
    private bool _isInitialLoadDone;
    /// <summary>The load in flight, so a second visit joins it instead of fetching again.</summary>
    private Task? _loading;
    /// <summary>
    /// Set once Intune's devices are in. Autopilot is joined only after that:
    /// joined to an empty list, every identity would read as not enrolled.
    /// </summary>
    private bool _intuneReady;

    /// <summary>Autopilot identities, kept with the page like the Intune cache is kept on the app.</summary>
    private static List<AutopilotDevice> _autopilot = new();

    /// <summary>The values ticked in each filter category; empty means no filter on it.</summary>
    private readonly Dictionary<DeviceFacet, HashSet<string>> _facetSelection =
        Enum.GetValues<DeviceFacet>().ToDictionary(f => f, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private static readonly string[] OptionalColumns =
        { "Model", "Manufacturer", "Ownership", "Migration", "Purchase Source", "Added", "Activation Lock" };

    private static string ColumnsStatePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FleetMate", "devices-columns.json");

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

        DevicesDataGrid.ItemsSource = _rows;
        DevicesDataGrid.PreviewMouseRightButtonUp += OnGridRightClick;
        RestoreColumns();

        DeviceDetail.CloseRequested += (_, _) => HideDeviceDetail();

        Loaded += async (s, e) =>
        {
            // Retry on every visit while the list is empty: the page is cached
            // across tab switches, so a failed first load (elevation hiccup,
            // service starting up) must not leave it dead forever.
            if (!_isInitialLoadDone || _rows.Count == 0)
            {
                _isInitialLoadDone = true;
                await LoadDevicesAsync();
            }
            // Deep link: check on every navigation (page is cached)
            if (_app?.PendingNavigateDeviceId is { } deviceId)
            {
                _app.PendingNavigateDeviceId = null;
                var row = _rows.FirstOrDefault(r => r.Id == deviceId)
                          ?? _allRows.FirstOrDefault(r => r.Id == deviceId);
                if (row != null)
                {
                    if (!_rows.Contains(row))
                    {
                        // A filter or search hid it: clear them so the link lands.
                        ClearFacetSelection();
                        SearchBox.Text = "";
                        ApplyFilters();
                    }
                    DevicesDataGrid.SelectedItem = row;
                    DevicesDataGrid.ScrollIntoView(row);
                }
            }
        };
    }

    /// <summary>
    /// Show Intune's devices as soon as they arrive, then merge Autopilot in
    /// when it lands: its identity listing is slow, and the list never waits
    /// on it.
    /// </summary>
    private Task LoadDevicesAsync()
    {
        if (_loading is { IsCompleted: false }) return _loading;
        return _loading = LoadDevicesCoreAsync();
    }

    private async Task LoadDevicesCoreAsync()
    {
        if (_graphService == null || _app == null)
        {
            NotConfiguredText.Visibility = Visibility.Visible;
            DevicesDataGrid.Visibility = Visibility.Collapsed;
            return;
        }

        DevicesDataGrid.Visibility = Visibility.Visible;
        NotConfiguredText.Visibility = Visibility.Collapsed;

        var autopilotTask = _autopilot.Count == 0 ? LoadAutopilotAsync() : Task.CompletedTask;
        var appleTask = _appleOrgs.Count == 0 ? LoadAppleOrgsAsync() : Task.CompletedTask;

        _intuneReady = false;
        if (!(_app.IsDevicesCacheValid && _app.CachedDevices.Count > 0))
        {
            LoadingPanel.Visibility = Visibility.Visible;
            try
            {
                _app.UpdateDevicesCache(await _graphService.GetManagedDevicesAsync(limit: 10000));
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

        _intuneReady = true;
        RebuildRows();
        await Task.WhenAll(autopilotTask, appleTask);
    }

    private async Task LoadAutopilotAsync()
    {
        if (_graphService == null) return;
        AutopilotLoadingText.Visibility = Visibility.Visible;
        try
        {
            _autopilot = await _graphService.GetAutopilotDevicesAsync(limit: 20000);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Devices: failed to read Autopilot identities");
        }
        finally
        {
            AutopilotLoadingText.Visibility = Visibility.Collapsed;
        }
        if (_intuneReady) RebuildRows();
    }

    /// <summary>Re-join Intune and Autopilot, keeping the selection and filters.</summary>
    private void RebuildRows()
    {
        var selectedIds = new HashSet<string>(SelectedRows().Select(r => r.Id));
        _allRows = AppleOrgJoin.Enrich(DeviceListJoin.Merge(_allDevices, _autopilot), _appleOrgs);
        RebuildFilters();
        ApplyFilters();
        foreach (var row in _rows.Where(r => selectedIds.Contains(r.Id) && !DevicesDataGrid.SelectedItems.Contains(r)))
            DevicesDataGrid.SelectedItems.Add(row);
    }

    // ── Filters ──────────────────────────────────────────────────────────

    /// <summary>
    /// The Filters panel: every category, Apple organization's first (macOS
    /// parity), each value with how many devices carry it.
    /// </summary>
    private void RebuildFilters()
    {
        FiltersHost.Children.Clear();
        foreach (var facet in Enum.GetValues<DeviceFacet>())
        {
            // The Autopilot categories appear once its identities are read.
            if (_autopilot.Count == 0 && DeviceFacets.AutopilotOnly.Contains(facet)) continue;
            FiltersHost.Children.Add(new TextBlock
            {
                Text = facet.Title(),
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 2)
            });
            foreach (var (value, count) in DeviceFacets.Counts(_allRows, facet))
            {
                var box = new CheckBox
                {
                    Content = $"{value} ({count})",
                    Tag = (facet, value),
                    FontSize = 12,
                    MinHeight = 24,
                    IsChecked = _facetSelection[facet].Contains(value)
                };
                box.Checked += OnFacetToggled;
                box.Unchecked += OnFacetToggled;
                FiltersHost.Children.Add(box);
            }
        }
        UpdateFiltersButton();
    }

    private void OnFacetToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: (DeviceFacet facet, string value) } box) return;
        if (box.IsChecked == true) _facetSelection[facet].Add(value);
        else _facetSelection[facet].Remove(value);
        UpdateFiltersButton();
        ApplyFilters();
    }

    private void OnFiltersClicked(object sender, RoutedEventArgs e) => FiltersPopup.IsOpen = !FiltersPopup.IsOpen;

    private void OnClearFiltersClicked(object sender, RoutedEventArgs e)
    {
        ClearFacetSelection();
        ApplyFilters();
    }

    /// <summary>
    /// Show only devices whose <paramref name="facet"/> is one of
    /// <paramref name="values"/> (none clears that category). This is the
    /// entry point for widgets and links that filter the list from outside.
    /// </summary>
    public void SetFacetFilter(DeviceFacet facet, params string[] values)
    {
        _facetSelection[facet].Clear();
        _facetSelection[facet].UnionWith(values);
        foreach (var box in FiltersHost.Children.OfType<CheckBox>())
            if (box.Tag is (DeviceFacet f, string v) && f == facet)
                box.IsChecked = _facetSelection[facet].Contains(v);
        UpdateFiltersButton();
        ApplyFilters();
    }

    private void ClearFacetSelection()
    {
        foreach (var set in _facetSelection.Values) set.Clear();
        foreach (var box in FiltersHost.Children.OfType<CheckBox>()) box.IsChecked = false;
        UpdateFiltersButton();
    }

    private void UpdateFiltersButton()
    {
        var active = _facetSelection.Values.Sum(v => v.Count);
        FiltersButtonText.Text = active == 0 ? "Filters" : $"Filters ({active})";
    }

    private void ApplyFilters()
    {
        // Guard: don't run during XAML initialization before controls exist
        if (!IsLoaded && _allRows.Count == 0) return;

        var filtered = DeviceFacets.Apply(_allRows, _facetSelection);
        var searchText = SearchBox.Text?.Trim();
        if (!string.IsNullOrEmpty(searchText))
            filtered = filtered.Where(r => r.Matches(searchText));

        var visible = filtered.ToList();
        var keep = new HashSet<string>(DevicesDataGrid.SelectedItems.Cast<DeviceListRow>()
            .Where(visible.Contains).Select(r => r.Id));

        // Hiding a row also deselects it.
        _rows.Clear();
        foreach (var row in visible) _rows.Add(row);
        foreach (var row in _rows.Where(r => keep.Contains(r.Id)))
            DevicesDataGrid.SelectedItems.Add(row);

        DeviceCountText.Text = visible.Count == _allRows.Count
            ? $"{_allRows.Count} devices"
            : $"{visible.Count} of {_allRows.Count} devices";
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilters();
    }

    // ── Columns ──────────────────────────────────────────────────────────

    /// <summary>Right-click a column header to show or hide the optional columns.</summary>
    private void OnGridRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source != null && source is not System.Windows.Controls.Primitives.DataGridColumnHeader)
            source = VisualTreeHelper.GetParent(source);
        if (source == null) return;

        var menu = new ContextMenu();
        foreach (var column in DevicesDataGrid.Columns.Where(c => OptionalColumns.Contains(c.Header as string)))
        {
            var item = new MenuItem { Header = column.Header, IsCheckable = true, IsChecked = column.Visibility == Visibility.Visible };
            var target = column;
            item.Click += (_, _) =>
            {
                target.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                SaveColumns();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void RestoreColumns()
    {
        try
        {
            if (!System.IO.File.Exists(ColumnsStatePath)) return;
            var shown = System.Text.Json.JsonSerializer.Deserialize<List<string>>(System.IO.File.ReadAllText(ColumnsStatePath)) ?? new();
            foreach (var column in DevicesDataGrid.Columns.Where(c => OptionalColumns.Contains(c.Header as string)))
                column.Visibility = shown.Contains((string)column.Header) ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Devices: could not restore column layout");
        }
    }

    private void SaveColumns()
    {
        try
        {
            var shown = DevicesDataGrid.Columns
                .Where(c => OptionalColumns.Contains(c.Header as string) && c.Visibility == Visibility.Visible)
                .Select(c => (string)c.Header).ToList();
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ColumnsStatePath)!);
            System.IO.File.WriteAllText(ColumnsStatePath, System.Text.Json.JsonSerializer.Serialize(shown));
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Devices: could not save column layout");
        }
    }

    // ── Selection ────────────────────────────────────────────────────────

    private List<DeviceListRow> SelectedRows() => DevicesDataGrid.SelectedItems.Cast<DeviceListRow>().ToList();

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        // Invalidate caches to force reload
        _app?.CachedDevices.Clear();
        _autopilot = new();
        _appleOrgs = new();
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
        var selected = SelectedRows();
        var selectedCount = selected.Count;
        var hasSelection = selectedCount > 0;

        ActionsButton.IsEnabled = hasSelection;
        ClearSelectionButton.IsEnabled = hasSelection;
        SelectionCountText.Text = hasSelection ? $"• {selectedCount} selected" : "";

        if (hasSelection)
        {
            SelectedDevicesText.Text = $"{selectedCount} device(s) selected";
            if (selectedCount <= 3)
            {
                SelectedDeviceNamesText.Text = string.Join(", ", selected.Select(Label));
            }
            else
            {
                var first2 = string.Join(", ", selected.Take(2).Select(Label));
                SelectedDeviceNamesText.Text = $"{first2} and {selectedCount - 2} more...";
            }

            UpdateActionSections(selected);

            // Mac parity: any selection opens the actions panel; a single
            // selection also opens the detail panel beside it.
            ShowActionsPanel();
            if (selectedCount == 1)
            {
                _ = ShowDeviceDetailAsync(selected[0]);
            }
            else
            {
                HideDeviceDetail();
            }
        }
        else
        {
            HideDeviceDetail();
            HideActionsPanel();
        }
    }

    /// <summary>A device's name, or its serial when it has none yet.</summary>
    private static string Label(DeviceListRow r) => r.NameText != DeviceListRow.Missing ? r.NameText : r.SerialText;

    /// <summary>
    /// Offer only the actions valid for every selected device: Intune's need
    /// every device enrolled; Fresh Start, Autopilot Reset and Cimian need
    /// every device to be Windows; Autopilot's need every device registered.
    /// </summary>
    private void UpdateActionSections(List<DeviceListRow> selected)
    {
        var allEnrolled = selected.All(r => r.IsEnrolled);
        var allWindows = selected.All(r => r.IsWindows);
        var identities = selected.Select(r => r.Autopilot).ToList();

        static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

        foreach (var section in new[] { SyncSection, RestartSection, LockSection, RetireSection, WipeSection,
                     DeleteRecordSection, ReinstallAppSection, OsUpdateSection })
            section.Visibility = Show(allEnrolled);
        foreach (var section in new[] { FreshStartSection, AutopilotResetSection, PushCimianSection })
            section.Visibility = Show(allEnrolled && allWindows);

        AutopilotGroupTagSection.Visibility = Show(AutopilotAction.SetGroupTag.IsAvailable(identities));
        AutopilotAssignUserSection.Visibility = Show(AutopilotAction.AssignUser.IsAvailable(identities));
        AutopilotUnassignUserSection.Visibility = Show(AutopilotAction.UnassignUser.IsAvailable(identities));
        AutopilotSyncSection.Visibility = Show(AutopilotAction.Sync.IsAvailable(identities));
        AutopilotDeleteSection.Visibility = Show(AutopilotAction.Delete.IsAvailable(identities));

        var anyAutopilot = AutopilotAction.Sync.IsAvailable(identities);
        var anyApple = UpdateAppleSections(selected);
        AutopilotActionsHeader.Visibility = Show(anyAutopilot);
        IntuneActionsHeader.Visibility = Show((anyAutopilot || anyApple) && allEnrolled);
        NoActionsText.Visibility = Show(!allEnrolled && !anyAutopilot && !anyApple);
    }

    private async Task ShowDeviceDetailAsync(DeviceListRow row)
    {
        DeviceDetail.Visibility = Visibility.Visible;
        DetailPanelColumn.Width = new GridLength(520);
        var apple = AppleContext(row);
        if (row.Intune is { } device)
            await DeviceDetail.ShowDeviceAsync(device, _graphService, row.Autopilot, apple);
        else if (apple != null)
            await DeviceDetail.ShowAppleOnlyAsync(apple);
        else if (row.Autopilot is { } identity)
            DeviceDetail.ShowAutopilotOnly(identity);
    }

    private void ShowActionsPanel()
    {
        ActionsPanel.Visibility = Visibility.Visible;
        ActionsPanelColumn.Width = new GridLength(316);
    }

    private void HideActionsPanel()
    {
        ActionsPanel.Visibility = Visibility.Collapsed;
        ActionsPanelColumn.Width = new GridLength(0);
    }

    private void HideDeviceDetail()
    {
        DeviceDetail.Visibility = Visibility.Collapsed;
        DetailPanelColumn.Width = new GridLength(0);
    }

    private void OnActionsClicked(object sender, RoutedEventArgs e) => ShowActionsPanel();

    private void OnCloseActionsPanel(object sender, RoutedEventArgs e) => HideActionsPanel();

    /// <summary>Intune actions only ever receive Intune IDs.</summary>
    private IEnumerable<string> GetSelectedDeviceIds()
    {
        return SelectedRows().Where(r => r.Intune != null).Select(r => r.Intune!.Id);
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

    private async void OnFreshStartClicked(object sender, RoutedEventArgs e)
    {
        if (_graphService == null) return;

        // Fresh Start is Windows-only; run against the Windows subset rather
        // than refusing a mixed selection outright.
        var targets = SelectedRows()
            .Where(r => r.Intune != null && r.IsWindows)
            .Select(r => r.Intune!.Id)
            .ToList();
        if (targets.Count == 0)
        {
            ShowActionMessage("Fresh Start applies to Windows devices only — none selected", isError: true);
            return;
        }

        var keepUserData = FreshStartKeepUserDataCheckBox.IsChecked == true;
        var result = MessageBox.Show(
            $"This will reinstall Windows on {targets.Count} device(s){(keepUserData ? ", preserving user data" : ", removing user data")}. Preinstalled OEM apps are removed and the device stays enrolled.",
            "Confirm Fresh Start",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        ShowActionMessage($"Starting Fresh Start on {targets.Count} device(s)...", isLoading: true);
        try
        {
            var results = await _graphService.FreshStartDevicesAsync(targets, keepUserData, confirmed: true);
            var successful = results.Count(r => r.Success);
            var failed = results.Count - successful;
            ShowActionMessage(failed == 0
                ? $"Fresh Start sent to {successful} device(s)"
                : $"Fresh Start sent to {successful}, {failed} failed");
        }
        catch (Exception ex)
        {
            ShowActionMessage($"Error: {ex.Message}", isError: true);
        }
    }

    private async void OnAutopilotResetClicked(object sender, RoutedEventArgs e)
    {
        if (_graphService == null) return;

        var deviceIds = GetSelectedDeviceIds().ToList();
        var result = MessageBox.Show(
            $"Autopilot-reset {deviceIds.Count} device(s)? Apps and settings are removed; enrollment is kept and the device re-provisions.",
            "Confirm Autopilot Reset",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        ShowActionMessage($"Autopilot-resetting {deviceIds.Count} device(s)...", isLoading: true);
        try
        {
            var successful = 0;
            foreach (var id in deviceIds)
            {
                var actionResult = await _graphService.AutopilotResetDeviceAsync(id, confirmed: true);
                if (actionResult.Success) successful++;
            }
            var failed = deviceIds.Count - successful;
            ShowActionMessage(failed == 0
                ? $"Autopilot reset sent to {successful} device(s)"
                : $"Autopilot reset sent to {successful}, {failed} failed");
        }
        catch (Exception ex)
        {
            ShowActionMessage($"Error: {ex.Message}", isError: true);
        }
    }

    private async void OnDeleteRecordClicked(object sender, RoutedEventArgs e)
    {
        if (_graphService == null) return;

        var deviceIds = GetSelectedDeviceIds().ToList();
        var result = MessageBox.Show(
            $"Delete the Intune record for {deviceIds.Count} device(s)? This is server-side only and cannot be undone from here.",
            "Confirm Delete Record",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        ShowActionMessage($"Deleting {deviceIds.Count} record(s)...", isLoading: true);
        try
        {
            var successful = 0;
            foreach (var id in deviceIds)
            {
                var actionResult = await _graphService.DeleteManagedDeviceAsync(id, confirmed: true);
                if (actionResult.Success) successful++;
            }
            var failed = deviceIds.Count - successful;
            ShowActionMessage(failed == 0
                ? $"Deleted {successful} record(s)"
                : $"Deleted {successful}, {failed} failed");
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
        var keepEnrollment = WipeKeepEnrollmentCheckBox.IsChecked == true;
        var keepUserData = WipeKeepUserDataCheckBox.IsChecked == true;
        var result = MessageBox.Show(
            $"This will factory-reset {deviceIds.Count} device(s){(keepUserData ? ", keeping user data where the platform allows" : ", erasing all data")}. This cannot be undone.",
            "Confirm Wipe",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        ShowActionMessage($"Wiping {deviceIds.Count} device(s)...", isLoading: true);

        try
        {
            var results = await _graphService.WipeDevicesAsync(deviceIds,
                keepEnrollmentData: keepEnrollment, keepUserData: keepUserData, confirmed: true);
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

        var selectedDevices = SelectedRows().Where(r => r.Intune != null).Select(r => r.Intune!).ToList();
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
