using FleetMate.Core.Development;
using FleetMate.Core.Devices;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Shared;
using FleetMate.WinUI.Shell;
using FleetMate.WinUI.Shell.Widgets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Serilog;

namespace FleetMate.WinUI.Pages.Devices;

/// <summary>
/// The Devices tab: every device in one list (Intune joined to Autopilot
/// identities and the Apple organizations), its filters, serial lookup and
/// columns; the Device Actions panel for any selection and the detail pane
/// for a single device; the compliance lightbox; and the widget strip the
/// toolbar's Graphs button shows. The list never waits on the slow reads:
/// Intune's devices show first and Autopilot and Apple join when they land.
/// FleetMate.GUI's IntunePage is the behaviour reference and the Mac's
/// DevicesView the layout.
/// </summary>
public sealed partial class DevicesPage : Page, ICaptureViews
{
    private const string Tab = "Devices";
    private const double NarrowSide = 420;

    private bool _hooked;
    private bool _loadedOnce;
    private Task? _loading;
    private bool _intuneReady;
    private bool _narrow;
    private bool _syncingSelection;

    public DevicesPage()
    {
        InitializeComponent();
        Table.Attach(ViewModel);
        Actions.Attach(this);
        Actions.CloseRequested += (_, _) => HideActions();
        Detail.CloseRequested += (_, _) => HideDetail();
        Detail.PolicyRequested += async (_, e) => await Lightbox.ShowAsync(e.Device, e.Policy, Detail);
        Table.List.SelectionChanged += (_, _) => OnSelectionChanged();
        ViewModel.RowsChanged += (_, _) => RestoreSelection();
        MainWindow.SearchChanged += (_, text) => ViewModel.SetSearch(text);
        Loaded += OnLoaded;
    }

    public DevicesViewModel ViewModel { get; } = new();

    private IReadOnlyList<DeviceListRow> _keepSelected = Array.Empty<DeviceListRow>();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = TakePendingLinkAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_hooked)
        {
            _hooked = true;
            var services = App.Services;
            services.CacheChanged += OnCacheChanged;
            services.DeviceLinkRequested += () => _ = TakePendingLinkAsync();
            WidgetStrip.Changed += tab => { if (tab == Tab) RenderGraphs(); };
        }
        RenderGraphs();
        // Retry on every visit while the list is empty: the page is cached, so a
        // failed first load must not leave it empty for the rest of the session.
        if (!_loadedOnce || ViewModel.AllRows.Count == 0)
        {
            _loadedOnce = true;
            await LoadDevicesAsync();
        }
        await TakePendingLinkAsync();
    }

    private void OnCacheChanged(string key)
    {
        if (key == "Devices")
        {
            if (_intuneReady && !App.Services.IsCacheLoading("Devices")) Rebuild();
            RenderGraphs();
        }
        // Inventory arriving later lets the "Missing from Inventory" check speak.
        else if (key == "Assets" && _intuneReady) Rebuild();
    }

    // MARK: - Loading

    /// <summary>Show Intune's devices as soon as they arrive, then join Autopilot and Apple as they land.</summary>
    private Task LoadDevicesAsync()
    {
        if (_loading is { IsCompleted: false }) return _loading;
        return _loading = LoadDevicesCoreAsync();
    }

    private async Task LoadDevicesCoreAsync()
    {
        var services = App.Services;
        if (!services.DevicesConfigured)
        {
            ShowEmpty("Add the Microsoft Graph tenant and client IDs in Settings. Authentication uses your signed-in Windows account.");
            return;
        }
        EmptyText.Visibility = Visibility.Collapsed;

        if (services.DemoMode)
        {
            _intuneReady = true;
            Rebuild();
            return;
        }

        var autopilot = services.CachedAutopilot.Count == 0 ? LoadAutopilotAsync() : Task.CompletedTask;
        var apple = services.CachedAppleOrgs.Count == 0 ? LoadAppleOrgsAsync() : Task.CompletedTask;

        _intuneReady = false;
        if (!(services.IsDevicesCacheValid && services.CachedDevices.Count > 0) && services.Graph is { } graph)
        {
            LoadingPanel.Visibility = Visibility.Visible;
            using var loading = services.TrackCacheLoad("Devices");
            try
            {
                services.UpdateDevicesCache(await graph.GetManagedDevicesAsync(limit: 10000));
            }
            catch (Exception ex)
            {
                ShowNotice($"Error: {ex.Message}", InfoBarSeverity.Error);
            }
            finally
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
            }
        }
        _intuneReady = true;
        Rebuild();
        await Task.WhenAll(autopilot, apple);
    }

    /// <summary>Read Autopilot's identities again, after an Autopilot action changed them.</summary>
    internal async Task ReloadAutopilotAsync()
    {
        App.Services.CachedAutopilot = new();
        await LoadAutopilotAsync();
    }

    private async Task LoadAutopilotAsync()
    {
        var services = App.Services;
        if (services.DemoMode || services.Graph is not { } graph) return;
        if (!ShellServices.DeviceEnrollmentOn)
        {
            services.CachedAutopilot = new();
            if (_intuneReady) Rebuild();
            return;
        }
        SetLoadingNote("reading Autopilot…", true);
        try
        {
            services.CachedAutopilot = await graph.GetAutopilotDevicesAsync(limit: 20000);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Devices: failed to read Autopilot identities");
        }
        finally
        {
            SetLoadingNote("reading Autopilot…", false);
        }
        if (_intuneReady) Rebuild();
    }

    /// <summary>
    /// Every configured Apple organization at once, after Intune: the quota is
    /// per organization, so the wait is the slowest one, not the sum.
    /// </summary>
    private async Task LoadAppleOrgsAsync()
    {
        var services = App.Services;
        if (services.DemoMode) return;
        if (!ShellServices.DeviceEnrollmentOn)
        {
            services.CachedAppleOrgs = new();
            return;
        }
        List<AppleOrgProfile> profiles;
        try { profiles = new Core.Services.Devices.AppleOrgCredentialStore().Profiles(); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Devices: could not list Apple organization profiles");
            return;
        }
        if (profiles.Count == 0) return;

        SetLoadingNote("reading Apple organizations…", true);
        try
        {
            var reads = profiles.Select(async p =>
            {
                try
                {
                    var service = services.AppleService(p.Name);
                    return service == null ? null : await service.SnapshotAsync();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Devices: failed to read {Service} profile {Profile}", p.ServiceName, p.Name);
                    ShowNotice($"{p.ServiceName}: {ex.Message}", InfoBarSeverity.Warning);
                    return null;
                }
            });
            services.CachedAppleOrgs = (await Task.WhenAll(reads)).OfType<AppleOrgSnapshot>().ToList();
        }
        finally
        {
            SetLoadingNote("reading Apple organizations…", false);
        }
        if (_intuneReady) Rebuild();
    }

    /// <summary>
    /// After an Apple action: a few devices are re-read one by one; more
    /// re-read the whole organization, which costs fewer requests.
    /// </summary>
    internal async Task RereadAppleAsync(string orgName, IReadOnlyList<string> serials)
    {
        var services = App.Services;
        if (services.AppleService(orgName) is not { } service) return;
        if (serials.Count <= DeviceActionText.AppleRereadLimit)
        {
            var fresh = (await service.RereadAsync(serials.ToList())).ToDictionary(d => DeviceListJoin.Normalize(d.SerialNumber));
            services.CachedAppleOrgs = services.CachedAppleOrgs.Select(o => o.Profile.Name != orgName ? o : o with
            {
                Devices = o.Devices.Select(d => fresh.GetValueOrDefault(DeviceListJoin.Normalize(d.SerialNumber)) ?? d).ToList(),
            }).ToList();
            Rebuild();
        }
        else
        {
            services.CachedAppleOrgs = new();
            await LoadAppleOrgsAsync();
        }
    }

    private readonly HashSet<string> _loadingNotes = new();

    private void SetLoadingNote(string note, bool on)
    {
        if (on) _loadingNotes.Add(note); else _loadingNotes.Remove(note);
        LoadingNote.Text = string.Join(" ", _loadingNotes.Select(n => "· " + n));
        LoadingNote.Visibility = DeviceUi.Show(_loadingNotes.Count > 0);
    }

    /// <summary>Join everything again, keeping the selection, filters and search.</summary>
    private void Rebuild()
    {
        var services = App.Services;
        _keepSelected = ViewModel.Selected;
        ViewModel.Rebuild(services.CachedDevices, services.CachedAutopilot, services.CachedAppleOrgs, InventorySerials());
        EmptyText.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Serials the asset inventory holds, once it has been read; null until
    /// then, so the "Missing from Inventory" check stays quiet. The WinUI
    /// Inventory tab provides the asset cache when it is ported.
    /// </summary>
    private static IReadOnlySet<string>? InventorySerials() => null;

    private void ShowEmpty(string message)
    {
        EmptyText.Text = message;
        EmptyText.Visibility = Visibility.Visible;
    }

    private void ShowNotice(string message, InfoBarSeverity severity)
    {
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void RefreshAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        App.Services.InvalidateDevices();
        _loadedOnce = true;
        await LoadDevicesAsync();
    }

    // MARK: - Graphs

    /// <summary>The widget strip, when the toolbar's Graphs button shows it for Devices.</summary>
    private void RenderGraphs()
    {
        var services = App.Services;
        FrameworkElement? content = null;
        if (WidgetStrip.IsShown(Tab))
        {
            services.EnsureDeviceErrors();
            var cards = DeviceGraphCards.Build(services.CachedDevices, services.IsCacheLoading("Devices"),
                services.ReportMateService != null || services.DemoMode ? services.DeviceErrors : null, DateTime.UtcNow);
            content = WidgetStrip.Build(Tab, cards, ApplyWidgetFilter);
        }
        GraphsHost.Content = content;
        GraphsScroller.Visibility = DeviceUi.Show(content != null);
        GraphsScroller.MaxHeight = Math.Max(180, ActualHeight * 0.4);
    }

    /// <summary>
    /// A Platform tile picks that platform, a Compliance wedge or the
    /// Non-Compliant figure that state. The widgets name platforms as the Mac
    /// does ("Macintosh"); the filter holds Intune's names, matched through that label.
    /// </summary>
    private void ApplyWidgetFilter(GraphFilter filter)
    {
        DeviceFacet? facet = filter.Category switch
        {
            DeviceGraphCategory.Platform => DeviceFacet.Platform,
            DeviceGraphCategory.Compliance => DeviceFacet.Compliance,
            _ => null,
        };
        if (facet is not { } f) return;
        var available = DeviceFacets.Counts(ViewModel.AllRows, f).Select(c => c.Value);
        ViewModel.SetFacetFilter(f, WidgetFilterValues.Match(filter.Value, available,
            f == DeviceFacet.Platform ? DeviceFigures.PlatformLabel : null));
    }

    // MARK: - Filters

    private void FiltersFlyout_Opening(object sender, object e) => BuildFilters();

    /// <summary>Every category, Apple organization's first, each value with how many devices carry it.</summary>
    private void BuildFilters()
    {
        FiltersHost.Children.Clear();
        foreach (var (facet, values) in ViewModel.FilterGroups())
        {
            if (values.Count == 0) continue;
            var heading = new TextBlock
            {
                Text = facet.Title(),
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                Margin = new Thickness(0, FiltersHost.Children.Count == 0 ? 0 : 12, 0, 2),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHeadingLevel(heading, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level3);
            FiltersHost.Children.Add(heading);
            foreach (var (value, count) in values)
            {
                var box = new CheckBox { Content = $"{value} ({count})", IsChecked = ViewModel.IsChecked(facet, value), MinHeight = 28 };
                var (f, v) = (facet, value);
                box.Checked += (_, _) => ViewModel.SetFacet(f, v, true);
                box.Unchecked += (_, _) => ViewModel.SetFacet(f, v, false);
                FiltersHost.Children.Add(box);
            }
        }
        if (FiltersHost.Children.Count == 0)
            FiltersHost.Children.Add(new TextBlock { Text = "No devices to filter yet.", Foreground = DeviceUi.Theme("TextFillColorSecondaryBrush") });
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearFilters();
        BuildFilters();
    }

    /// <summary>Ctrl+Shift+L, from the shell: clear the list's filters.</summary>
    public void ClearFilters() => ViewModel.ClearFilters();

    // MARK: - Serial lookup

    private void SerialsFlyout_Opened(object sender, object e) => SerialsInput.Focus(FocusState.Programmatic);

    private void SerialsInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        var count = SerialListParser.Parse(SerialsInput.Text).Count;
        SerialsParsedText.Text = DeviceListText.ParsedSerials(count);
        SerialsShowButton.IsEnabled = count > 0;
    }

    private async void ImportSerials_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFileAsync(".txt", ".csv") is not { } path) return;
        try
        {
            var serials = SerialListParser.Parse(await File.ReadAllTextAsync(path));
            if (serials.Count == 0)
            {
                SerialsParsedText.Text = $"No serial numbers found in {Path.GetFileName(path)}.";
                return;
            }
            SerialsInput.Text = string.Join(Environment.NewLine, serials);
        }
        catch (Exception ex)
        {
            SerialsParsedText.Text = $"Could not read the file: {ex.Message}";
        }
        SerialsFlyout.ShowAt(SerialsButton);
    }

    private void ShowSerials_Click(object sender, RoutedEventArgs e)
    {
        var serials = SerialListParser.Parse(SerialsInput.Text);
        if (serials.Count == 0) return;
        ViewModel.SetSerialLookup(serials);
        SerialsFlyout.Hide();
    }

    private void ClearSerials_Click(object sender, RoutedEventArgs e)
    {
        SerialsInput.Text = "";
        ViewModel.SetSerialLookup(null);
        SerialsFlyout.Hide();
    }

    private static async Task<string?> PickFileAsync(params string[] types)
    {
        if (App.Current.Window is not { } window) return null;
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(window.AppWindow.Id);
        foreach (var type in types) picker.FileTypeFilter.Add(type);
        var result = await picker.PickSingleFileAsync();
        return result?.Path;
    }

    // MARK: - Hardware hash import

    private async void ImportHashes_Click(object sender, RoutedEventArgs e)
    {
        if (App.Services.DemoMode)
        {
            await DeviceUi.InformAsync(XamlRoot, "Import Hashes", "Demo mode: nothing is imported.");
            return;
        }
        if (App.Services.Graph is not { } graph)
        {
            ShowNotice("Microsoft Graph is not configured.", InfoBarSeverity.Warning);
            return;
        }
        if (await PickFileAsync(".csv") is not { } path) return;
        AutopilotHashCsv csv;
        try
        {
            csv = AutopilotHashCsv.Parse(await File.ReadAllBytesAsync(path));
        }
        catch (Exception ex)
        {
            await DeviceUi.InformAsync(XamlRoot, "Import Hashes", ex.Message);
            return;
        }
        var dialog = new AutopilotImportDialog(graph, csv, Path.GetFileName(path)) { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
        if (dialog.Imported) await ReloadAutopilotAsync();
    }

    // MARK: - Selection

    private void SelectAll_Click(object sender, RoutedEventArgs e) => Table.List.SelectAll();

    private void SelectAllAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Table.List.SelectAll();
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e) => Table.List.SelectedItems.Clear();

    private void EscapeAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Lightbox.IsOpen) { Lightbox.Close(); args.Handled = true; }
        else if (ViewModel.HasSelection) { Table.List.SelectedItems.Clear(); args.Handled = true; }
    }

    private void ActionsButton_Click(object sender, RoutedEventArgs e) => ShowActions(focus: true);

    /// <summary>The refilled list keeps whichever selected rows it still shows; a hidden row leaves the selection.</summary>
    private void RestoreSelection()
    {
        var keep = new HashSet<string>(_keepSelected.Select(r => r.Id));
        _syncingSelection = true;
        try
        {
            foreach (var row in ViewModel.Rows.Where(r => keep.Contains(r.Id)))
                if (!Table.List.SelectedItems.Contains(row)) Table.List.SelectedItems.Add(row);
        }
        finally
        {
            _syncingSelection = false;
        }
        OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        if (_syncingSelection) return;
        var selected = Table.List.SelectedItems.OfType<DeviceListRow>().ToList();
        _keepSelected = selected;
        var org = AppleOrgFor(selected);
        ViewModel.SetSelection(selected, org?.Profile);
        DeviceContext.Devices(selected);

        if (selected.Count == 0)
        {
            HideDetail();
            HideActions();
            return;
        }
        // Mac parity: any selection opens the actions; one device also opens its detail.
        Actions.Show(selected, ViewModel.Sections, org);
        ShowActions(focus: false);
        if (selected.Count == 1 && !selected[0].IsUnknown) _ = ShowDetailAsync(selected[0]);
        else HideDetail();
    }

    private static AppleOrgSnapshot? AppleOrgFor(IReadOnlyCollection<DeviceListRow> rows) =>
        AppleOrgJoin.SingleOrg(rows) is { } name ? App.Services.CachedAppleOrgs.FirstOrDefault(o => o.Profile.Name == name) : null;

    private async Task ShowDetailAsync(DeviceListRow row)
    {
        _detailOpen = true;
        if (_narrow) SideSwitch.SelectedItem = DetailSwitchItem;
        LayoutSide();
        var apple = AppleDetail(row);
        try
        {
            if (row.Intune is { } device) await Detail.ShowDeviceAsync(device, row.Autopilot, apple);
            else if (apple != null) await Detail.ShowAppleOnlyAsync(apple);
            else if (row.Autopilot is { } identity) Detail.ShowAutopilotOnly(identity);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Devices: could not show the detail for {Id}", row.Id);
        }
    }

    /// <summary>The Apple organization record for a row, with how to read AppleCare and Activation Lock.</summary>
    private AppleOrgDetail? AppleDetail(DeviceListRow row)
    {
        if (row.Apple is not { } device) return null;
        var services = App.Services;
        var org = services.CachedAppleOrgs.FirstOrDefault(o => o.Profile.Name == device.OrgId);
        if (org == null) return null;
        var view = new AppleOrgView(device, row.OrgName ?? org.Profile.ServiceName, row.ServerName, ReadsAppleCare: true, ReadsActivationLock: true);
        var serial = device.SerialNumber;
        if (services.DemoMode)
            return new AppleOrgDetail(view,
                () => Task.FromResult(new List<AppleCareAgreement>
                {
                    new("AppleCare for Business (demo)", "Active", DateTimeOffset.Now.AddYears(-1), DateTimeOffset.Now.AddYears(2), null, null, false),
                }),
                () => Task.FromResult(AppleActivationLock.Disabled));
        return new AppleOrgDetail(view,
            () => services.AppleService(org.Profile.Name)?.AppleCareAsync(serial) ?? Task.FromResult(new List<AppleCareAgreement>()),
            () => ReadActivationLockAsync(org.Profile.Name, serial));
    }

    /// <summary>Read once per device per session; the optional column shows it from then on.</summary>
    private async Task<AppleActivationLock> ReadActivationLockAsync(string profileName, string serial)
    {
        if (ActivationLockCache.Get(serial) is { } known) return known;
        var state = App.Services.AppleService(profileName) is { } service
            ? await service.ActivationLockAsync(serial)
            : AppleActivationLock.Unknown;
        ActivationLockCache.Set(serial, state);
        Table.Refresh();
        return state;
    }

    // MARK: - Side panels

    private bool _actionsOpen;
    private bool _detailOpen;

    private void ShowActions(bool focus)
    {
        _actionsOpen = true;
        if (_narrow && focus) SideSwitch.SelectedItem = ActionsSwitchItem;
        LayoutSide();
    }

    private void HideActions()
    {
        _actionsOpen = false;
        LayoutSide();
    }

    private void HideDetail()
    {
        _detailOpen = false;
        LayoutSide();
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Wide enough for the list beside both panels, as on Mac; otherwise they share one column.
        _narrow = e.NewSize.Width < 1360;
        LayoutSide();
        GraphsScroller.MaxHeight = Math.Max(180, e.NewSize.Height * 0.4);
    }

    /// <summary>
    /// Wide: actions and detail side by side. Narrow: one column, with a
    /// switch between them when both are open.
    /// </summary>
    private void LayoutSide()
    {
        var both = _actionsOpen && _detailOpen;
        SidePanel.Visibility = DeviceUi.Show(_actionsOpen || _detailOpen);
        if (!_actionsOpen && !_detailOpen)
        {
            SideColumn.Width = new GridLength(0);
            return;
        }
        if (!_narrow)
        {
            SideSwitch.Visibility = Visibility.Collapsed;
            Actions.Visibility = DeviceUi.Show(_actionsOpen);
            Detail.Visibility = DeviceUi.Show(_detailOpen);
            ActionsColumn.Width = new GridLength(_actionsOpen ? 300 : 0);
            DetailColumn.Width = _detailOpen ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            SideGrid.ColumnSpacing = both ? 12 : 0;
            Grid.SetColumn(Detail, 1);
            SideColumn.Width = new GridLength((_actionsOpen ? 300 : 0) + (_detailOpen ? 480 : 0) + (both ? 12 : 0));
            return;
        }
        SideSwitch.Visibility = DeviceUi.Show(both);
        var showDetail = _detailOpen && (!both || SideSwitch.SelectedItem == DetailSwitchItem);
        Actions.Visibility = DeviceUi.Show(_actionsOpen && !showDetail);
        Detail.Visibility = DeviceUi.Show(showDetail);
        SideGrid.ColumnSpacing = 0;
        ActionsColumn.Width = new GridLength(1, GridUnitType.Star);
        DetailColumn.Width = new GridLength(0);
        Grid.SetColumn(Detail, 0);
        SideColumn.Width = new GridLength(NarrowSide);
    }

    private void SideSwitch_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => LayoutSide();

    // MARK: - Links

    /// <summary>A <c>fleetmate:</c> link or search result asked for a device: clear what hides it and select it.</summary>
    private async Task TakePendingLinkAsync()
    {
        if (App.Services.PendingDeviceId is not { } id) return;
        if (_loading is { IsCompleted: false } loading) await loading;
        App.Services.PendingDeviceId = null;
        var row = ViewModel.AllRows.FirstOrDefault(r => r.Id == id || r.Intune?.Id == id);
        if (row == null)
        {
            App.Current.Window?.ShowLinkBanner("That device is not in the Devices list.");
            return;
        }
        if (!ViewModel.Rows.Contains(row))
        {
            // A filter, lookup or search hid it: clear them so the link lands.
            ViewModel.ClearFilters();
            ViewModel.SetSerialLookup(null);
            ViewModel.SetSearch("");
        }
        Table.List.SelectedItems.Clear();
        Table.List.SelectedItem = row;
        Table.List.ScrollIntoView(row);
    }

    // MARK: - QA capture

    public IReadOnlyList<string> CaptureViews { get; } = new[]
    {
        "graphs", "filters", "detail", "detail-apple", "actions", "actions-multi", "lightbox",
    };

    public async Task ShowCaptureViewAsync(string view)
    {
        Lightbox.Close();
        FiltersFlyout.Hide();
        WidgetStrip.SetShown(Tab, view == "graphs");
        RenderGraphs();
        Table.List.SelectedItems.Clear();
        await Task.Delay(50);
        switch (view)
        {
            case "filters":
                FiltersFlyout.ShowAt(FiltersButton);
                break;
            case "detail" or "lightbox":
                Select(ViewModel.Rows.FirstOrDefault(r => r.Intune != null && r.IsWindows && r.Autopilot != null));
                if (_narrow) SideSwitch.SelectedItem = DetailSwitchItem;
                LayoutSide();
                await Task.Delay(400);
                if (view == "lightbox" && Detail.FirstPolicy is { } first) await Lightbox.ShowAsync(first.Device, first.Policy);
                break;
            case "detail-apple":
                Select(ViewModel.Rows.FirstOrDefault(r => r.Intune != null && r.Apple != null));
                break;
            case "actions":
                Select(ViewModel.Rows.FirstOrDefault(r => r.Intune != null && r.IsWindows && r.Autopilot != null));
                ShowActions(focus: true);
                Actions.ExpandAll();
                break;
            case "actions-multi":
                foreach (var row in ViewModel.Rows.Where(r => r.Apple != null).Take(3)) Table.List.SelectedItems.Add(row);
                ShowActions(focus: true);
                break;
        }
    }

    private void Select(DeviceListRow? row)
    {
        if (row == null) return;
        Table.List.SelectedItem = row;
        Table.List.ScrollIntoView(row);
    }
}
