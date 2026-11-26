using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FleetMate.Core.Devices;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Identity;
using FleetMate.Core.Config;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Devices;
using FleetMate.Core.Services.Inventory;
using FleetMate.Core.Services.Tickets;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Services.Reporting;
using FleetMate.Core.Shared;
using FleetMate.GUI.Views.Shared;

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

    /// <summary>The looked-up serials narrowing the list; null shows every device.</summary>
    private List<string>? _lookupSerials;

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
        // Copy or Send to Agent on device rows; the header's column menu is its own.
        AgentContextMenu.Attach(DevicesDataGrid);

        // Get services from App
        if (Application.Current is App app)
        {
            _app = app;
            _graphService = app.GraphService;
        }

        DevicesDataGrid.ItemsSource = _rows;
        InitializeLifecycleControls();
        DevicesDataGrid.PreviewMouseRightButtonUp += OnGridRightClick;
        RestoreColumns();

        DeviceDetail.CloseRequested += (_, _) => HideDeviceDetail();
        DevicesDataGrid.ColumnReordered += (_, _) => SaveColumns();
        // Inventory arriving later lets the "Missing from Inventory" check speak.
        if (_app != null)
            _app.CacheChanged += key => { if (key == "Assets" && _intuneReady) RebuildRows(); };

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
            using var loading = _app.TrackCacheLoad("Devices");
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

    /// <summary>
    /// Enrollment records (Autopilot identities and Apple organizations) are
    /// read only while the Enrollment module is on; switched off, Devices
    /// lists management records alone.
    /// </summary>
    private static bool EnrollmentOn =>
        AppModules.IsOn(UserPreferences.HiddenModules, AppModules.Enrollment);

    private async Task LoadAutopilotAsync()
    {
        if (_graphService == null) return;
        if (!EnrollmentOn)
        {
            _autopilot = new();
            if (_intuneReady) RebuildRows();
            return;
        }
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
        DeviceDiscrepancies.Apply(_allRows, new DeviceDiscrepancies.Sources(
            AutopilotRead: _autopilot.Count > 0,
            AppleOrgsRead: _appleOrgs.Count > 0,
            InventorySerials: InventorySerials()));
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
        FiltersButtonText.Text = DeviceListText.FiltersButton(active);
    }

    private void ApplyFilters()
    {
        // Guard: don't run during XAML initialization before controls exist
        if (!IsLoaded && _allRows.Count == 0) return;

        IReadOnlyList<DeviceListRow> source = _allRows;
        SerialLookup.Result? lookup = null;
        if (_lookupSerials != null)
        {
            lookup = SerialLookup.Apply(_allRows, _lookupSerials);
            source = lookup.Rows;
        }

        var filtered = DeviceFacets.Apply(source, _facetSelection);
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

        DeviceCountText.Text = DeviceListText.Count(visible.Count, _allRows.Count, lookup, _lookupSerials?.Count ?? 0);
    }

    /// <summary>Serials the asset inventory holds, once it has been read; null until then.</summary>
    private IReadOnlySet<string>? InventorySerials()
    {
        var assets = _app?.CachedAssets;
        if (assets is not { Count: > 0 }) return null;
        return assets.Select(a => a.Serial).Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => DeviceListJoin.Normalize(s!)).ToHashSet();
    }

    // ── Serial lookup ────────────────────────────────────────────────────

    private void OnSerialsClicked(object sender, RoutedEventArgs e)
    {
        SerialsPopup.IsOpen = !SerialsPopup.IsOpen;
        if (SerialsPopup.IsOpen) SerialsInput.Focus();
    }

    private void OnSerialsInputChanged(object sender, TextChangedEventArgs e)
    {
        var count = SerialListParser.Parse(SerialsInput.Text).Count;
        SerialsParsedText.Text = DeviceListText.ParsedSerials(count);
        SerialsShowButton.IsEnabled = count > 0;
    }

    private async void OnImportSerialsClicked(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import serial numbers",
            Filter = "Serial lists (*.txt;*.csv)|*.txt;*.csv|All files (*.*)|*.*"
        };
        // The file dialog takes focus; keep the popup open behind it.
        SerialsPopup.StaysOpen = true;
        try
        {
            if (picker.ShowDialog() != true) return;
            var text = await System.IO.File.ReadAllTextAsync(picker.FileName);
            var serials = SerialListParser.Parse(text);
            if (serials.Count == 0)
            {
                SerialsParsedText.Text = $"No serial numbers found in {System.IO.Path.GetFileName(picker.FileName)}.";
                return;
            }
            SerialsInput.Text = string.Join(Environment.NewLine, serials);
        }
        catch (Exception ex)
        {
            SerialsParsedText.Text = $"Could not read the file: {ex.Message}";
        }
        finally
        {
            SerialsPopup.StaysOpen = false;
        }
    }

    private void OnShowSerialsClicked(object sender, RoutedEventArgs e)
    {
        var serials = SerialListParser.Parse(SerialsInput.Text);
        if (serials.Count == 0) return;
        SetSerialLookup(serials);
        SerialsPopup.IsOpen = false;
    }

    private void OnClearSerialsClicked(object sender, RoutedEventArgs e)
    {
        SerialsInput.Text = "";
        SetSerialLookup(null);
        SerialsPopup.IsOpen = false;
    }

    /// <summary>Narrow the list to these serials, or show every device again with null.</summary>
    public void SetSerialLookup(IReadOnlyList<string>? serials)
    {
        _lookupSerials = serials is { Count: > 0 } ? serials.ToList() : null;
        SerialsButtonText.Text = DeviceListText.SerialsButton(_lookupSerials?.Count);
        ApplyFilters();
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
        foreach (var column in DevicesDataGrid.Columns
                     .Where(c => OptionalColumns.Contains(c.Header as string))
                     .Where(c => c.Visibility == Visibility.Visible || CanFill((string)c.Header))
                     .OrderBy(c => c.DisplayIndex))
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

    /// <summary>The default column order, as the XAML declares it, before any saved layout moves it.</summary>
    private List<string>? _defaultColumnOrder;

    private void RestoreColumns()
    {
        _defaultColumnOrder = DevicesDataGrid.Columns.Select(c => (string)c.Header).ToList();
        try
        {
            if (!System.IO.File.Exists(ColumnsStatePath)) return;
            var layout = DeviceColumnLayout.Parse(System.IO.File.ReadAllText(ColumnsStatePath));
            foreach (var column in DevicesDataGrid.Columns.Where(c => OptionalColumns.Contains(c.Header as string)))
                column.Visibility = layout.Shown.Contains((string)column.Header) ? Visibility.Visible : Visibility.Collapsed;

            var order = layout.Arrange(_defaultColumnOrder);
            foreach (var column in DevicesDataGrid.Columns)
                column.DisplayIndex = order.IndexOf((string)column.Header);
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
            var layout = new DeviceColumnLayout
            {
                Shown = DevicesDataGrid.Columns
                    .Where(c => OptionalColumns.Contains(c.Header as string) && c.Visibility == Visibility.Visible)
                    .Select(c => (string)c.Header).ToList(),
                Order = DevicesDataGrid.Columns.OrderBy(c => c.DisplayIndex).Select(c => (string)c.Header).ToList(),
            };
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ColumnsStatePath)!);
            System.IO.File.WriteAllText(ColumnsStatePath, layout.Serialize());
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Devices: could not save column layout");
        }
    }

    /// <summary>
    /// Whether the connected services fill an optional column for any device,
    /// so the header menu offers only columns that would show something.
    /// </summary>
    private bool CanFill(string header) => DeviceListColumns.CanFill(header, _allRows);

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
        Terminal.ContextPublisher.Devices(selected.Where(r => r.Intune != null).Select(r => r.Intune!));
        var hasSelection = selectedCount > 0;

        ActionsButton.IsEnabled = hasSelection;
        ClearSelectionButton.IsEnabled = hasSelection;
        SelectionCountText.Text = hasSelection ? $"• {selectedCount} selected" : "";

        if (hasSelection)
        {
            SelectedDevicesText.Text = DeviceListText.SelectedTitle(selectedCount);
            SelectedDeviceNamesText.Text = DeviceListText.SelectedNames(selected);

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
    private static string Label(DeviceListRow r) => DeviceListText.Label(r);

    /// <summary>
    /// Offer only the actions valid for every selected device, as
    /// <see cref="DeviceActionSections"/> decides for both Windows apps.
    /// </summary>
    private void UpdateActionSections(List<DeviceListRow> selected)
    {
        var orgName = AppleOrgJoin.SingleOrg(selected);
        var org = orgName == null ? null : _appleOrgs.FirstOrDefault(o => o.Profile.Name == orgName);
        var s = DeviceActionSections.For(selected, org?.Profile);
        static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

        foreach (var section in new[] { SyncSection, RestartSection, LockSection, RetireSection, WipeSection,
                     DeleteRecordSection, ReinstallAppSection, OsUpdateSection })
            section.Visibility = Show(s.Intune);
        foreach (var section in new[] { FreshStartSection, AutopilotResetSection, PushCimianSection })
            section.Visibility = Show(s.WindowsIntune);

        AutopilotGroupTagSection.Visibility = Show(s.AutopilotGroupTag);
        AutopilotAssignUserSection.Visibility = Show(s.AutopilotAssignUser);
        AutopilotUnassignUserSection.Visibility = Show(s.AutopilotUnassignUser);
        AutopilotSyncSection.Visibility = Show(s.AutopilotSync);
        AutopilotDeleteSection.Visibility = Show(s.AutopilotDelete);

        UpdateAppleSections(s, org);
        AutopilotActionsHeader.Visibility = Show(s.AutopilotHeaderShown);
        IntuneActionsHeader.Visibility = Show(s.IntuneHeaderShown);
        NoActionsText.Visibility = Show(s.NoActions);
        UpdateLifecycleSections(s);
    }

    private async Task ShowDeviceDetailAsync(DeviceListRow row)
    {
        // A serial no system knows has nothing to show.
        if (row.IsUnknown)
        {
            HideDeviceDetail();
            return;
        }
        DeviceDetail.Visibility = Visibility.Visible;
        DetailPanelColumn.Width = new GridLength(520);
        DeviceDetail.AgentButton.Context = AgentContexts.Device(row);
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

    /// <summary>Ask the question Core words for <paramref name="kind"/>; true when the person says yes.</summary>
    private static bool ConfirmAction(DeviceActionKind kind, int count, bool keepUserData = false) =>
        DeviceActionText.Confirm(kind, count, keepUserData) is not { } confirm
        || MessageBox.Show(confirm.Message, confirm.Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private async void OnSyncClicked(object sender, RoutedEventArgs e)
    {
        if (_graphService == null) return;

        var deviceIds = GetSelectedDeviceIds().ToList();
        ShowActionMessage(DeviceActionText.Progress(DeviceActionKind.Sync, deviceIds.Count), isLoading: true);

        try
        {
            var results = await _graphService.SyncDevicesAsync(deviceIds);
            var successful = results.Count(r => r.Success);
            var failed = results.Count - successful;

            ShowActionMessage(DeviceActionText.Result(DeviceActionKind.Sync, successful, failed));
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
        if (!ConfirmAction(DeviceActionKind.Restart, deviceIds.Count)) return;

        ShowActionMessage(DeviceActionText.Progress(DeviceActionKind.Restart, deviceIds.Count), isLoading: true);

        try
        {
            var results = await _graphService.RebootDevicesAsync(deviceIds);
            var successful = results.Count(r => r.Success);
            var failed = results.Count - successful;

            ShowActionMessage(DeviceActionText.Result(DeviceActionKind.Restart, successful, failed));
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
        if (!ConfirmAction(DeviceActionKind.Retire, deviceIds.Count)) return;

        ShowActionMessage(DeviceActionText.Progress(DeviceActionKind.Retire, deviceIds.Count), isLoading: true);

        try
        {
            var results = await _graphService.RetireDevicesAsync(deviceIds, confirmed: true);
            var successful = results.Count(r => r.Success);
            var failed = results.Count - successful;

            ShowActionMessage(DeviceActionText.Result(DeviceActionKind.Retire, successful, failed));
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
            ShowActionMessage(DeviceActionText.FreshStartNoWindows, isError: true);
            return;
        }

        var keepUserData = FreshStartKeepUserDataCheckBox.IsChecked == true;
        if (!ConfirmAction(DeviceActionKind.FreshStart, targets.Count, keepUserData)) return;

        ShowActionMessage(DeviceActionText.Progress(DeviceActionKind.FreshStart, targets.Count), isLoading: true);
        try
        {
            var results = await _graphService.FreshStartDevicesAsync(targets, keepUserData, confirmed: true);
            var successful = results.Count(r => r.Success);
            var failed = results.Count - successful;
            ShowActionMessage(DeviceActionText.Result(DeviceActionKind.FreshStart, successful, failed));
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
        if (!ConfirmAction(DeviceActionKind.AutopilotReset, deviceIds.Count)) return;

        ShowActionMessage(DeviceActionText.Progress(DeviceActionKind.AutopilotReset, deviceIds.Count), isLoading: true);
        try
        {
            var successful = 0;
            foreach (var id in deviceIds)
            {
                var actionResult = await _graphService.AutopilotResetDeviceAsync(id, confirmed: true);
                if (actionResult.Success) successful++;
            }
            var failed = deviceIds.Count - successful;
            ShowActionMessage(DeviceActionText.Result(DeviceActionKind.AutopilotReset, successful, failed));
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
        if (!ConfirmAction(DeviceActionKind.DeleteRecord, deviceIds.Count)) return;

        ShowActionMessage(DeviceActionText.Progress(DeviceActionKind.DeleteRecord, deviceIds.Count), isLoading: true);
        try
        {
            var successful = 0;
            foreach (var id in deviceIds)
            {
                var actionResult = await _graphService.DeleteManagedDeviceAsync(id, confirmed: true);
                if (actionResult.Success) successful++;
            }
            var failed = deviceIds.Count - successful;
            ShowActionMessage(DeviceActionText.Result(DeviceActionKind.DeleteRecord, successful, failed));
        }
        catch (Exception ex)
        {
            ShowActionMessage($"Error: {ex.Message}", isError: true);
        }
    }

    private async void OnWipeClicked(object sender, RoutedEventArgs e)
    {
        if (_graphService == null) return;

        // Wipe takes device records, not bare ids, so each body is built for its own platform.
        var devices = SelectedRows().Where(r => r.Intune != null).Select(r => r.Intune!).ToList();
        var options = CurrentWipeOptions();
        if (!ConfirmAction(DeviceActionKind.Wipe, devices.Count, options.KeepUserData)) return;

        ShowActionMessage(DeviceActionText.Progress(DeviceActionKind.Wipe, devices.Count), isLoading: true);

        try
        {
            var results = await _graphService.WipeDevicesAsync(devices, options, confirmed: true);
            var successful = results.Count(r => r.Success);
            var failed = results.Count - successful;

            ShowActionMessage(DeviceActionText.Result(DeviceActionKind.Wipe, successful, failed));
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
        if (!ConfirmAction(DeviceActionKind.Lock, deviceIds.Count)) return;

        var pin = string.IsNullOrWhiteSpace(LockPinTextBox.Text) ? null : LockPinTextBox.Text;
        ShowActionMessage(DeviceActionText.Progress(DeviceActionKind.Lock, deviceIds.Count), isLoading: true);

        try
        {
            var results = await _graphService.RemoteLockDevicesAsync(deviceIds, pin, confirmed: true);
            var successful = results.Count(r => r.Success);
            var failed = results.Count - successful;

            ShowActionMessage(DeviceActionText.Result(DeviceActionKind.Lock, successful, failed));

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
        ShowActionMessage(DeviceActionText.Progress(DeviceActionKind.ReinstallApp, deviceIds.Count), isLoading: true);

        try
        {
            // Reinstall is triggered via sync
            var results = await _graphService.SyncDevicesAsync(deviceIds);
            var successful = results.Count(r => r.Success);
            var failed = results.Count - successful;

            var appName = selectedApp.Content?.ToString() ?? "app";
            ShowActionMessage(DeviceActionText.Result(DeviceActionKind.ReinstallApp, successful, failed, appName));
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
        ShowActionMessage(DeviceActionText.Progress(DeviceActionKind.OsUpdate, deviceIds.Count), isLoading: true);

        try
        {
            // Update check is triggered via sync
            var results = await _graphService.SyncDevicesAsync(deviceIds);
            var successful = results.Count(r => r.Success);
            var failed = results.Count - successful;

            ShowActionMessage(DeviceActionText.Result(DeviceActionKind.OsUpdate, successful, failed));
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

        ShowActionMessage(DeviceActionText.Progress(DeviceActionKind.PushCimian, selectedDevices.Count), isLoading: true);

        try
        {
            // Force Intune sync on selected devices to trigger remediation pickup
            var syncResults = await _graphService.SyncDevicesAsync(selectedDevices.Select(d => d.Id));
            var successful = syncResults.Count(r => r.Success);
            var failed = syncResults.Count - successful;

            ShowActionMessage(DeviceActionText.Result(DeviceActionKind.PushCimian, successful, failed));
        }
        catch (Exception ex)
        {
            ShowActionMessage($"Error: {ex.Message}", isError: true);
        }
    }
}
