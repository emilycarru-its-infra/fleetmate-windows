using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using FleetMate.Core.Manage;
using FleetMate.Core.Models.Manage;
using FleetMate.Core.Services.Manage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Serilog;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace FleetMate.WinUI.Pages.Manage;

/// <summary>
/// The Manage tab: the roster sidebar (Curriculum, Kiosks, Staff, Faculty,
/// custom groups; rows drag to reorder within a section), the selected room's
/// machines with their scan and probe results, the command runner and its
/// streaming results, and an inspector for one machine. The logic is
/// FleetMate.Core's <see cref="ManageViewModel"/>, the same one the WPF app runs;
/// this page draws it and asks the questions a run needs answered.
/// </summary>
public sealed partial class ManagePage : Page, Shell.ICaptureViews
{
    private static readonly string[] Sections = { "Labs", "Kiosks", "Staff", "Faculty" };

    private readonly ManageStateStore _store;
    private readonly Dictionary<string, ObservableCollection<SidebarRoom>> _sections = new();
    private Dictionary<string, bool> _expanded;
    private Dictionary<string, List<string>> _order;
    private string _provenance = "local file";
    private bool _fetching;
    private bool _loaded;
    private bool _suppressSidebar;
    private HashSet<string> _pickedLabIds = new();
    private MachineRowViewModel? _detailRow;
    private MachineColumn? _sortColumn;
    private bool _sortAscending = true;
    private bool _extended;
    private ResultFilter _resultFilter;
    private MachineRowViewModel? _menuRow;
    private CommandResultViewModel? _menuResult;
    private CustomGroup? _menuGroup;
    private CheckBox? _headerCheck;
    private bool _syncingCommand;

    public ManagePage()
    {
        ViewModel = App.Services.CreateManageViewModel();
        _store = App.Services.ManageState;
        _expanded = _store.LoadSidebarState();
        _order = _store.LoadSidebarOrder();
        InitializeComponent();

        foreach (var (key, list) in SectionLists())
        {
            _sections[key] = new ObservableCollection<SidebarRoom>();
            list.ItemsSource = _sections[key];
        }
        ViewModel.PropertyChanged += OnViewModelChanged;
        ViewModel.Rows.CollectionChanged += (_, _) => RefreshRows();
        ViewModel.Results.CollectionChanged += (_, _) => RefreshResults();
        ViewModel.CustomGroups.CollectionChanged += (_, _) => UpdateGroupCounts();
        ViewModel.RunCompleted += OnRunCompleted;
        MainWindow.SearchChanged += (_, text) => ApplySearch(text);
        BuildTableHeader();
        Loaded += OnLoaded;
    }

    public ManageViewModel ViewModel { get; }

    /// <summary>The machine list as shown: <see cref="ManageViewModel.Rows"/> in the chosen column order.</summary>
    public ObservableCollection<MachineRowViewModel> VisibleRows { get; } = new();

    public bool HasResults => ViewModel.Results.Count > 0;

    private XamlRoot DialogRoot => XamlRoot;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // A capture walks every view each pass; each pass starts on the room list.
        if (App.Services.DemoMode) PreviewHost.Visibility = Visibility.Collapsed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        ReloadRoster();
        UpdateSelectionCount();
        RefreshResults();
    }

    // MARK: - Roster

    private IEnumerable<(string Key, ListView List)> SectionLists() => new[]
    {
        ("Labs", LabsList), ("Kiosks", KiosksList), ("Staff", StaffList), ("Faculty", FacultyList),
    };

    /// <summary>Show what is on disk at once, then refresh from the repository.</summary>
    private void ReloadRoster()
    {
        if (!App.Services.DemoMode && ViewModel.RosterPathOverride == null && RosterSource.CachedProvenance(RosterSource.CachePath) is { } cached)
        {
            ViewModel.RosterPathOverride = RosterSource.CachePath;
            _provenance = cached;
        }
        if (App.Services.DemoMode) _provenance = "demo roster";
        ReloadRosterFromDisk();
        if (!App.Services.DemoMode) _ = FetchRosterAsync();
    }

    private async Task FetchRosterAsync()
    {
        if (_fetching) return;
        _fetching = true;
        try
        {
            var (fetched, provenance) = await RosterSource.FetchAsync(App.Services.Config, RosterSource.CachePath);
            _provenance = provenance;
            if (fetched)
            {
                ViewModel.RosterPathOverride = RosterSource.CachePath;
                ReloadRosterFromDisk();
            }
            else UpdateFooter();
        }
        finally
        {
            _fetching = false;
        }
    }

    private void ReloadRosterFromDisk()
    {
        ViewModel.LoadRoster();
        _suppressSidebar = true;
        Fill("Labs", ViewModel.Roster.Labs, RosterSection.Labs);
        Fill("Kiosks", ViewModel.Roster.Kiosks, RosterSection.Kiosks);
        Fill("Staff", ViewModel.Roster.Staff, RosterSection.Staff);
        Fill("Faculty", ViewModel.Roster.Faculty, RosterSection.Faculty);
        _suppressSidebar = false;

        // Section badges count machines, not rooms: the Mac sidebar's numbers.
        LabsCount.Text = ViewModel.Roster.Labs.Sum(r => r.Count).ToString();
        KiosksCount.Text = ViewModel.Roster.Kiosks.Sum(r => r.Count).ToString();
        StaffCount.Text = ViewModel.Roster.Staff.Sum(r => r.Count).ToString();
        FacultyCount.Text = ViewModel.Roster.Faculty.Sum(r => r.Count).ToString();
        UpdateGroupCounts();
        UpdateFooter();
        ApplyExpanded();
        EmptyHint.Text = ManageText.EmptyHint(ViewModel, App.Services.ManageHasSsh);
        SyncCommandCombo();
    }

    private void Fill(string key, IEnumerable<RosterRoom> rooms, RosterSection section)
    {
        var rows = SidebarOrder.Apply(_order.GetValueOrDefault(key), rooms.Select(r => SidebarRoom.From(r, section)).ToList());
        var target = _sections[key];
        target.Clear();
        foreach (var row in rows) target.Add(row);
    }

    private void UpdateFooter() => RosterFooter.Text = ManageText.RosterFooter(ViewModel, _provenance);

    private void UpdateGroupCounts()
    {
        NoGroupsText.Visibility = Show(ViewModel.CustomGroups.Count == 0);
        GroupsCount.Text = ViewModel.CustomGroups.Sum(g => g.Devices.Count).ToString();
    }

    private void ReloadRoster_Click(object sender, RoutedEventArgs e) => ReloadRoster();

    private void ReloadRoster_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ReloadRoster();
    }

    /// <summary>Refresh, as on Mac: the roster, the command library and, when a room is open, a fresh scan.</summary>
    private async void Refresh_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ReloadRoster();
        if (ViewModel.HasSelection) await ScanAsync();
    }

    // MARK: - Sidebar

    private void ApplyExpanded()
    {
        foreach (var (key, list) in SectionLists())
        {
            var expanded = !_expanded.TryGetValue(key, out var value) || value;
            list.Visibility = Show(expanded);
            var toggle = (Button)FindName(key + "Toggle");
            ((FontIcon)toggle.Content).Glyph = expanded ? "" : "";
            var name = $"{(expanded ? "Collapse" : "Expand")} {key}";
            AutomationProperties.SetName(toggle, name);
            ToolTipService.SetToolTip(toggle, name);
        }
    }

    private void ToggleSection_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string key) return;
        _expanded[key] = !(!_expanded.TryGetValue(key, out var current) || current);
        _store.SaveSidebarState(_expanded);
        ApplyExpanded();
    }

    private async void RoomList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSidebar || sender is not ListView list || list.SelectedItem is not SidebarRoom room) return;
        ClearOtherSelections(list);
        HideDetail();
        await ViewModel.SelectRoomAsync(room.Room);
    }

    private async void GroupsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSidebar || GroupsList.SelectedItem is not CustomGroup group) return;
        ClearOtherSelections(GroupsList);
        HideDetail();
        await ViewModel.SelectGroupAsync(group);
    }

    private void ClearOtherSelections(ListView keep)
    {
        _suppressSidebar = true;
        foreach (var list in SectionLists().Select(s => s.List).Append(GroupsList).Append(SearchList))
            if (list != keep) list.SelectedItem = null;
        _suppressSidebar = false;
    }

    /// <summary>A row dropped in a new slot: keep the section's order.</summary>
    private void SectionList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (sender.Tag is not string key || args.DropResult != DataPackageOperation.Move) return;
        _order[key] = _sections[key].Select(r => r.Title).ToList();
        _store.SaveSidebarOrder(_order);
    }

    private void ResetOrder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string key || !_order.Remove(key)) return;
        _store.SaveSidebarOrder(_order);
        ReloadRosterFromDisk();
    }

    private async void LabPicker_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Roster.Labs.Count == 0) return;
        var dialog = new LabPickerDialog(ViewModel.Roster.Labs, _pickedLabIds);
        if (await dialog.ShowAsync(DialogRoot) != ContentDialogResult.Primary || dialog.SelectedRooms.Count == 0) return;
        _pickedLabIds = dialog.SelectedRooms.Select(r => r.Id).ToHashSet();
        ClearOtherSelections(SearchList);
        HideDetail();
        var label = dialog.SelectedRooms.Count == 1 ? dialog.SelectedRooms[0].Name : $"{dialog.SelectedRooms.Count} labs";
        await ViewModel.SelectSearchResultsAsync(LabPicker.Computers(dialog.SelectedRooms), label);
    }

    // MARK: - Search

    private string _search = "";

    /// <summary>The toolbar search: matching machines replace the sidebar sections while it has text.</summary>
    private void ApplySearch(string text)
    {
        _search = text.Trim();
        var searching = _search.Length > 0;
        SearchPanel.Visibility = Show(searching);
        SectionsScroll.Visibility = Hide(searching);
        if (!searching) return;
        var results = ViewModel.SearchResults(_search).Select(c => new ComputerRow(c)).ToList();
        SearchList.ItemsSource = results;
        SearchCount.Text = results.Count == 1 ? "1 MATCH" : $"{results.Count} MATCHES";
    }

    private void SearchSelectAll_Click(object sender, RoutedEventArgs e) => SearchList.SelectAll();

    private async void UseSearch_Click(object sender, RoutedEventArgs e)
    {
        var picked = SearchList.SelectedItems.OfType<ComputerRow>().Select(r => r.Computer).ToList();
        if (picked.Count == 0) picked = SearchList.Items.OfType<ComputerRow>().Select(r => r.Computer).ToList();
        if (picked.Count == 0) return;
        ClearOtherSelections(SearchList);
        HideDetail();
        await ViewModel.SelectSearchResultsAsync(picked, _search);
    }

    // MARK: - Custom groups

    private void GroupMenu_Opening(object? sender, object e) =>
        _menuGroup = ((sender as FlyoutBase)?.Target as FrameworkElement)?.DataContext as CustomGroup;

    private async void CreateGroup_Click(object sender, RoutedEventArgs e)
    {
        if (await TextPromptDialog.AskAsync(DialogRoot, "New custom group", "Group name", "") is not { } name) return;
        GroupsList.SelectedItem = ViewModel.CreateGroup(name);
    }

    private async void RenameGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_menuGroup is not { } group) return;
        var name = await TextPromptDialog.AskAsync(DialogRoot, "Rename group", "Group name", group.Name);
        if (string.IsNullOrWhiteSpace(name) || name == group.Name) return;
        ViewModel.RenameGroup(group, name);
    }

    private async void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_menuGroup is not { } group) return;
        if (!await ConfirmDialog.AskAsync(DialogRoot, "Delete group", $"Delete the custom group \"{group.Name}\"? Its machines stay in the roster.", "Delete")) return;
        ViewModel.DeleteGroup(group);
    }

    private async void AddDevicesToGroup_Click(object sender, RoutedEventArgs e) =>
        await ShowAddDeviceAsync(_menuGroup, Array.Empty<RosterComputer>());

    private async void AddDevice_Click(object sender, RoutedEventArgs e) =>
        await ShowAddDeviceAsync(ViewModel.SelectedGroup, Array.Empty<RosterComputer>());

    private async void AddSelectedToGroup_Click(object sender, RoutedEventArgs e)
    {
        var picked = ViewModel.SelectedRows.Select(r => r.Computer).ToList();
        if (picked.Count == 0) picked = MachinesList.SelectedItems.OfType<MachineRowViewModel>().Select(r => r.Computer).ToList();
        if (picked.Count == 0 && _menuRow != null) picked.Add(_menuRow.Computer);
        await ShowAddDeviceAsync(null, picked);
    }

    private async Task ShowAddDeviceAsync(CustomGroup? target, IReadOnlyList<RosterComputer> preselected)
    {
        var dialog = new AddDeviceDialog(ViewModel, target, preselected, () => PickFileAsync(".csv", ".txt"));
        await dialog.ShowAsync(DialogRoot);
        UpdateGroupCounts();
        if (dialog.ResultGroup != null && GroupsList.SelectedItem != dialog.ResultGroup) GroupsList.SelectedItem = dialog.ResultGroup;
    }

    // MARK: - Scan and probe

    private Task ScanAsync()
    {
        if (!ViewModel.HasSelection) return Task.CompletedTask;
        var stored = ViewModel.SelectedGroup?.Devices.Where(d => d.Ip.Length > 0).ToDictionary(d => d.Computer.Serial, d => d.Ip);
        return ViewModel.ScanAsync(stored);
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async void Scan_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await ScanAsync();
    }

    private void StopScan_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelScan();
        ViewModel.CancelProbe();
    }

    private async void FetchDetails_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanProbe)
        {
            await NoticeAsync("SSH not configured", "Machine details need the fleet SSH key. Set the key path in Settings, Manage.");
            return;
        }
        await ViewModel.ProbeAllAsync();
    }

    // MARK: - Selection

    private void SelectAll_Click(object sender, RoutedEventArgs e) => ViewModel.SelectAll();
    private void SelectOnline_Click(object sender, RoutedEventArgs e) => ViewModel.SelectOnline();
    private void SelectNone_Click(object sender, RoutedEventArgs e) => ViewModel.SelectNone();

    private void SelectOnline_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.SelectOnline();
    }

    private void UpdateSelectionCount()
    {
        var selected = ViewModel.SelectedCount;
        SelectionCount.Text = ManageText.SelectionCount(selected, ViewModel.OnlineCount);
        if (_headerCheck != null)
            _headerCheck.IsChecked = ViewModel.Rows.Count > 0 && selected == ViewModel.Rows.Count ? true : selected == 0 ? false : null;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ManageViewModel.SelectedCount):
            case nameof(ManageViewModel.OnlineCount):
                UpdateSelectionCount();
                break;
            case nameof(ManageViewModel.SelectedCategory):
            case nameof(ManageViewModel.SelectedCommand):
                SyncCommandCombo();
                break;
            case nameof(ManageViewModel.ResultSuccessCount):
            case nameof(ManageViewModel.ResultFailedCount):
            case nameof(ManageViewModel.ResultOfflineCount):
            case nameof(ManageViewModel.LastRunLabel):
                UpdateResultCounts();
                break;
            case nameof(ManageViewModel.LastScan):
                if (_detailRow != null) RefreshDetail();
                break;
        }
    }

    // MARK: - Machine list

    private static readonly (MachineColumn? Column, string Title, double Star, bool Extended)[] Columns =
    {
        (MachineColumn.Machine, "Machine", 1.4, false),
        (MachineColumn.Address, "Address", 1.1, false),
        (MachineColumn.User, "User", 1, false),
        (MachineColumn.Os, "OS", 1.2, true),
        (MachineColumn.Uptime, "Uptime", 0.8, true),
        (MachineColumn.Join, "Join", 0.8, true),
        (MachineColumn.ClientId, "Client identifier", 1.1, true),
        (MachineColumn.RemoteAccess, "Remote access", 1.3, false),
    };

    /// <summary>The header row, matching the row template's columns: a check-all box, status, then sortable titles.</summary>
    private void BuildTableHeader()
    {
        TableHeader.Children.Clear();
        TableHeader.ColumnDefinitions.Clear();
        void Column(GridLength width) => TableHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = width });

        Column(new GridLength(32));
        _headerCheck = new CheckBox { MinWidth = 0, Padding = new Thickness(0), IsThreeState = false, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(_headerCheck, "Select every machine");
        _headerCheck.Click += (_, _) => { if (_headerCheck.IsChecked == true) ViewModel.SelectAll(); else ViewModel.SelectNone(); };
        TableHeader.Children.Add(_headerCheck);

        Column(new GridLength(20));
        var status = SortButton(MachineColumn.Status, "", "Sort by status");
        Grid.SetColumn(status, 1);
        TableHeader.Children.Add(status);

        foreach (var (column, title, star, extended) in Columns)
        {
            if (extended && !_extended) continue;
            Column(new GridLength(star, GridUnitType.Star));
            var button = SortButton(column!.Value, title, $"Sort by {title.ToLowerInvariant()}");
            Grid.SetColumn(button, TableHeader.ColumnDefinitions.Count - 1);
            TableHeader.Children.Add(button);
        }
        Column(new GridLength(118));
        TableGrid.MinWidth = _extended ? 1080 : 680;
        UpdateSelectionCount();
    }

    private Button SortButton(MachineColumn column, string title, string name)
    {
        var text = new TextBlock { Text = title, TextWrapping = TextWrapping.WrapWholeWords };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { text } };
        if (_sortColumn == column)
            content.Children.Add(new FontIcon { Glyph = _sortAscending ? "" : "", FontSize = 9 });
        var button = new Button { Content = content, Style = (Style)Resources["HeaderCellStyle"] };
        if (title.Length == 0) button.Padding = new Thickness(0, 4, 0, 5);
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) =>
        {
            _sortAscending = _sortColumn != column || !_sortAscending;
            _sortColumn = column;
            BuildTableHeader();
            RefreshRows();
        };
        return button;
    }

    private void RefreshRows()
    {
        var selected = MachinesList.SelectedItems.OfType<MachineRowViewModel>().ToList();
        VisibleRows.Clear();
        foreach (var row in MachineSort.Apply(ViewModel.Rows, _sortColumn, _sortAscending)) VisibleRows.Add(row);
        foreach (var row in selected.Where(VisibleRows.Contains)) MachinesList.SelectedItems.Add(row);
        if (_detailRow != null && !ViewModel.Rows.Contains(_detailRow)) HideDetail();
        UpdateSelectionCount();
    }

    private void Extended_Click(object sender, RoutedEventArgs e)
    {
        _extended = ExtendedToggle.IsChecked == true;
        MachinesList.ItemTemplate = (DataTemplate)Resources[_extended ? "ExtendedRowTemplate" : "CompactRowTemplate"];
        BuildTableHeader();
    }

    private void MachinesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_detailRow != null && MachinesList.SelectedItems.Count == 1 && MachinesList.SelectedItem is MachineRowViewModel row) ShowDetail(row);
    }

    private void MachinesList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is MachineRowViewModel row) ShowDetail(row);
    }

    private void RowMenu_Opening(object? sender, object e) =>
        _menuRow = ((sender as FlyoutBase)?.Target as FrameworkElement)?.DataContext as MachineRowViewModel;

    private static MachineRowViewModel? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as MachineRowViewModel;

    private void RowSshButton_Click(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) ViewModel.OpenSsh(r); }
    private void RowRdpButton_Click(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) ViewModel.OpenRdp(r); }
    private void RowInfoButton_Click(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) ShowDetail(r); }

    private void RowSsh_Click(object sender, RoutedEventArgs e) { if (_menuRow is { } r) ViewModel.OpenSsh(r); }
    private void RowRdp_Click(object sender, RoutedEventArgs e) { if (_menuRow is { } r) ViewModel.OpenRdp(r); }
    private void RowBoth_Click(object sender, RoutedEventArgs e) { if (_menuRow is { } r) ViewModel.OpenSshAndRdp(r); }
    private async void RowRescan_Click(object sender, RoutedEventArgs e) { if (_menuRow is { } r) await ViewModel.RescanHostAsync(r); }
    private async void RowProbe_Click(object sender, RoutedEventArgs e) { if (_menuRow is { HasAddress: true } r) await ViewModel.ProbeAsync(new[] { r }); }
    private void RowInfo_Click(object sender, RoutedEventArgs e) { if (_menuRow is { } r) ShowDetail(r); }

    private void CopyHostname_Click(object sender, RoutedEventArgs e) { if (_menuRow is { } r) CopyText(ManageText.HostnameOf(r)); }
    private void CopyIp_Click(object sender, RoutedEventArgs e) { if (_menuRow is { HasAddress: true } r) CopyText(r.Ip); }
    private void CopySerial_Click(object sender, RoutedEventArgs e) { if (_menuRow is { } r) CopyText(r.Serial); }
    private void CopyAsset_Click(object sender, RoutedEventArgs e) { if (_menuRow is { } r) CopyText(r.Computer.Asset); }
    private void CopyOs_Click(object sender, RoutedEventArgs e) { if (_menuRow is { } r) CopyText(r.OsLabel); }
    private void CopyAllInfo_Click(object sender, RoutedEventArgs e) { if (_menuRow is { } r) CopyText(r.CopyAllInfo()); }

    private List<MachineRowViewModel> TargetRows() => ManageText.TargetRows(ViewModel, MachinesList.SelectedItems.OfType<MachineRowViewModel>());

    private void CopySelectedHostnames_Click(object sender, RoutedEventArgs e) => CopyLines(TargetRows().Select(ManageText.HostnameOf));
    private void CopySelectedAssets_Click(object sender, RoutedEventArgs e) => CopyLines(TargetRows().Select(r => r.Computer.Asset).Where(a => a.Length > 0));
    private void CopySelectedInventory_Click(object sender, RoutedEventArgs e) => CopyLines(TargetRows().Select(r => r.CopyLine));

    // MARK: - Command runner

    /// <summary>The command picker follows the category; the view model keeps which command is chosen.</summary>
    private void SyncCommandCombo()
    {
        _syncingCommand = true;
        CommandCombo.ItemsSource = ViewModel.SelectedCategory?.Commands.ToList();
        CommandCombo.SelectedItem = ViewModel.SelectedCommand;
        _syncingCommand = false;
    }

    private void CommandCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingCommand) return;
        ViewModel.SelectedCommand = CommandCombo.SelectedItem as ManagedCommand;
    }

    private async void Run_Click(object sender, RoutedEventArgs e) => await RunResolvedCommandAsync();

    private async void CustomCommand_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        if (!ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        e.Handled = true;
        await RunResolvedCommandAsync();
    }

    private async Task RunResolvedCommandAsync()
    {
        if (!ViewModel.CanRun)
        {
            await NoticeAsync("SSH not configured", "Running commands needs the fleet SSH key. Set the key path in Settings, Manage.");
            return;
        }
        var command = ViewModel.ResolvedCommandString;
        if (command.Length == 0) return;
        var label = ViewModel.ResolvedCommandLabel;
        var targets = ViewModel.OnlineSelectedRows.ToList();
        if (targets.Count == 0)
        {
            await NoticeAsync("No targets", "Check at least one online machine first. Select › Online checks every machine that answered.");
            return;
        }
        if (PlaceholderTemplate.Detect(label, command) is { } template)
        {
            var prompt = new PlaceholderDialog(template);
            if (await prompt.ShowAsync(DialogRoot) != ContentDialogResult.Primary || prompt.ResolvedCommand == null) return;
            command = prompt.ResolvedCommand;
        }
        var trust = ViewModel.EffectiveTrust;
        if (trust != CommandTrustLevel.Safe
            && !await ConfirmDialog.AskAsync(DialogRoot, trust.WarningTitle(),
                $"{trust.WarningMessage()}\n\n{label}\nTargets: {ManageText.Machines(targets.Count)}", $"Run on {ManageText.Machines(targets.Count)}"))
            return;
        await ViewModel.RunCommandAsync(command, label, recordHistory: true, targets);
    }

    private void Kill_Click(object sender, RoutedEventArgs e) => ViewModel.KillRun();

    private async void History_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new HistoryDialog(ViewModel.History);
        var answer = await dialog.ShowAsync(DialogRoot);
        if (dialog.Result == HistoryDialog.Outcome.Clear)
        {
            if (await ConfirmDialog.AskAsync(DialogRoot, "Clear history", "Clear the command history?", "Clear")) ViewModel.ClearHistory();
            return;
        }
        if (answer == ContentDialogResult.None && dialog.Result == HistoryDialog.Outcome.None) return;
        if (dialog.Chosen is not { } chosen) return;
        ViewModel.CustomCommand = chosen.Command;
        if (dialog.Result == HistoryDialog.Outcome.Rerun) await RunResolvedCommandAsync();
    }

    private async void AddCommand_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CommandEditorDialog(ViewModel.Categories.ToList(), ViewModel.SelectedCategory, null, ViewModel.CustomCommand);
        await dialog.ShowAsync(DialogRoot);
        if (dialog.Result != CommandEditorDialog.Outcome.Saved) return;
        var category = dialog.ResultCategory ?? ViewModel.AddCategory(dialog.ResultNewCategoryName);
        ViewModel.AddCommand(category, dialog.ResultLabel, dialog.ResultCommand, dialog.ResultTrust);
        ViewModel.CustomCommand = "";
        SyncCommandCombo();
    }

    private async void EditCommand_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedCategory is not { } category || ViewModel.SelectedCommand is not { } command) return;
        var dialog = new CommandEditorDialog(ViewModel.Categories.ToList(), category, command);
        await dialog.ShowAsync(DialogRoot);
        if (dialog.Result == CommandEditorDialog.Outcome.Deleted)
        {
            if (await ConfirmDialog.AskAsync(DialogRoot, "Delete command", $"Delete \"{command.Label}\" from the library?", "Delete"))
                ViewModel.DeleteCommand(category, command);
        }
        else if (dialog.Result == CommandEditorDialog.Outcome.Saved)
        {
            var target = dialog.ResultCategory ?? ViewModel.AddCategory(dialog.ResultNewCategoryName);
            if (target != category)
            {
                ViewModel.DeleteCommand(category, command);
                ViewModel.AddCommand(target, dialog.ResultLabel, dialog.ResultCommand, dialog.ResultTrust);
            }
            else ViewModel.EditCommand(category, command, dialog.ResultLabel, dialog.ResultCommand, dialog.ResultTrust);
        }
        SyncCommandCombo();
    }

    // MARK: - Quick actions, packages and SSH tabs

    private async void QuickRestart_Click(object sender, RoutedEventArgs e) => await RunQuickActionAsync(QuickActions.Restart);
    private async void QuickLogOut_Click(object sender, RoutedEventArgs e) => await RunQuickActionAsync(QuickActions.LogOutUser);
    private async void QuickSleep_Click(object sender, RoutedEventArgs e) => await RunQuickActionAsync(QuickActions.Sleep);
    private async void QuickLock_Click(object sender, RoutedEventArgs e) => await RunQuickActionAsync(QuickActions.LockScreen);

    private async Task RunQuickActionAsync(QuickActions.QuickAction action)
    {
        var targets = ViewModel.OnlineSelectedRows.ToList();
        if (targets.Count == 0)
        {
            await NoticeAsync(action.Label, "Check at least one online machine first.");
            return;
        }
        var what = targets.Count == 1 ? targets[0].FriendlyName : ManageText.Machines(targets.Count);
        if (!await ConfirmDialog.AskAsync(DialogRoot, string.Format(action.ConfirmTitle, what), action.ConfirmMessage, action.Label)) return;
        await ViewModel.RunCommandAsync(action.Command, action.Label, recordHistory: true, targets);
    }

    /// <summary>Copy a package to the checked online machines and install it silently (macOS parity).</summary>
    private async void InstallPackage_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanInstallPackages)
        {
            await NoticeAsync("Install Package", "Installing packages needs SSH configured in Settings.");
            return;
        }
        var targets = ViewModel.OnlineSelectedRows.ToList();
        if (targets.Count == 0)
        {
            await NoticeAsync("Install Package", "Check at least one online machine first.");
            return;
        }
        if (await PickFileAsync(PackageInstall.Extensions.ToArray()) is not { } path) return;
        var name = Path.GetFileName(path);
        if (!await ConfirmDialog.AskAsync(DialogRoot, $"Install {name} on {ManageText.Machines(targets.Count)}?",
                "The package is copied to each machine and installed silently as an administrator.", "Install"))
            return;
        await ViewModel.InstallPackageAsync(path, targets);
    }

    private async void OpenSshTabs_Click(object sender, RoutedEventArgs e)
    {
        var candidates = ViewModel.Rows.Where(r => r.IsOnline).ToList();
        if (candidates.Count == 0)
        {
            await NoticeAsync("Open SSH tabs", "No machine here is online.");
            return;
        }
        var dialog = new SshTabPickerDialog(candidates, RemoteSessionLauncher.WindowsTerminalAvailable);
        if (await dialog.ShowAsync(DialogRoot) == ContentDialogResult.Primary) ViewModel.OpenSshTabs(dialog.Chosen);
    }

    // MARK: - Results

    private void RefreshResults()
    {
        ResultsList.ItemsSource = _resultFilter == ResultFilter.All ? ViewModel.Results : ManageText.Filter(ViewModel.Results, _resultFilter).ToList();
        NoResultsText.Visibility = Show(ViewModel.Results.Count == 0);
        NoResultsText.Text = ViewModel.CanRun
            ? "Run a command to see each machine's result here."
            : "Running commands needs the fleet SSH key, set in Settings › Manage.";
        ResultActions.IsEnabled = HasResults;
        UpdateResultCounts();
    }

    private void UpdateResultCounts()
    {
        ResultsCounts.Text = ManageText.ResultCounts(ViewModel);
        ResultsTitle.Text = ManageText.ResultsTitle(ViewModel.LastRunLabel);
        if (_resultFilter != ResultFilter.All) ResultsList.ItemsSource = ManageText.Filter(ViewModel.Results, _resultFilter).ToList();
    }

    private void ResultFilter_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        _resultFilter = Enum.TryParse<ResultFilter>(sender.SelectedItem?.Tag as string, out var f) ? f : ResultFilter.All;
        if (ResultsList != null) RefreshResults();
    }

    private void CopyVisibleResults_Click(object sender, RoutedEventArgs e) =>
        CopyLines(ManageText.Filter(ViewModel.Results, _resultFilter).Select(r => r.Formatted()));

    private void SelectFailed_Click(object sender, RoutedEventArgs e)
    {
        var failed = ManageText.FailedSerials(ViewModel.Results);
        foreach (var row in ViewModel.Rows) row.IsSelected = failed.Contains(row.Serial);
    }

    private void ClearResults_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.KillRun();
        ViewModel.ClearResults();
    }

    private void ResultMenu_Opening(object? sender, object e) =>
        _menuResult = ((sender as FlyoutBase)?.Target as FrameworkElement)?.DataContext as CommandResultViewModel;

    private MachineRowViewModel? ResultRow() => _menuResult is { } r ? ViewModel.Rows.FirstOrDefault(x => x.Serial == r.Serial) : null;

    private void ResultSsh_Click(object sender, RoutedEventArgs e) { if (ResultRow() is { } row) ViewModel.OpenSsh(row); }
    private void ResultRdp_Click(object sender, RoutedEventArgs e) { if (ResultRow() is { } row) ViewModel.OpenRdp(row); }
    private void ResultBoth_Click(object sender, RoutedEventArgs e) { if (ResultRow() is { } row) ViewModel.OpenSshAndRdp(row); }
    private void ResultCopyHostname_Click(object sender, RoutedEventArgs e) { if (_menuResult is { } r) CopyText(ManageText.HostnameOf(r)); }
    private void ResultCopyIp_Click(object sender, RoutedEventArgs e) { if (_menuResult is { } r) CopyText(r.Ip); }
    private void ResultCopyHostnameIp_Click(object sender, RoutedEventArgs e) { if (_menuResult is { } r) CopyText($"{ManageText.HostnameOf(r)}\t{r.Ip}"); }
    private void ResultCopyOutput_Click(object sender, RoutedEventArgs e) { if (_menuResult is { } r) CopyText(r.Output); }
    private void ResultCopyError_Click(object sender, RoutedEventArgs e) { if (_menuResult is { } r) CopyText(r.ErrorOutput); }
    private void ResultCopyFull_Click(object sender, RoutedEventArgs e) { if (_menuResult is { } r) CopyText(r.Formatted()); }

    /// <summary>When the window is not in front, flash it so the operator notices the batch finished.</summary>
    private void OnRunCompleted(int success, int failed, int offline)
    {
        if (App.Current.Window is not { } window) return;
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (GetForegroundWindow() == hwnd) return;
            var info = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = hwnd, dwFlags = 0x0E, uCount = 3 };
            FlashWindowEx(ref info);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "FlashWindowEx failed");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    // MARK: - Inspector

    private void ShowDetail(MachineRowViewModel row)
    {
        if (_detailRow != null) _detailRow.PropertyChanged -= OnDetailRowChanged;
        _detailRow = row;
        row.PropertyChanged += OnDetailRowChanged;
        DetailPanel.Visibility = Visibility.Visible;
        LayoutPanes(Root_Width);
        RefreshDetail();
    }

    private void HideDetail()
    {
        if (_detailRow != null) _detailRow.PropertyChanged -= OnDetailRowChanged;
        _detailRow = null;
        DetailPanel.Visibility = Visibility.Collapsed;
        LayoutPanes(Root_Width);
    }

    private void OnDetailRowChanged(object? sender, PropertyChangedEventArgs e) => RefreshDetail();

    private void RefreshDetail()
    {
        if (_detailRow is not { } row) return;
        DetailTitle.Text = row.FriendlyName;
        DetailStatus.Text = string.Join("  ·  ", new[] { ManagePage.SecondName(row.FriendlyName, row.Hostname), row.StatusLabel, row.AddressLabel }.Where(s => s.Length > 0));
        DetailHelp.Text = row.RemoteAccessHelp;
        DetailError.Text = row.ProbeError ?? "";
        DetailError.Visibility = Show(!string.IsNullOrEmpty(row.ProbeError));
        DetailSsh.IsEnabled = DetailRdp.IsEnabled = row.HasAddress;

        DetailSections.Children.Clear();
        foreach (var section in MachineDetail.Sections(row, ViewModel.LastScan != null))
        {
            var body = new StackPanel { Spacing = 6 };
            body.Children.Add(new TextBlock { Text = section.Title, Style = (Style)Application.Current.Resources["CardHeaderTextStyle"], Margin = new Thickness(0) });
            foreach (var field in section.Fields)
            {
                var line = new Grid { ColumnSpacing = 10 };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
                line.ColumnDefinitions.Add(new ColumnDefinition());
                var label = new TextBlock { Text = field.Label, Style = (Style)Application.Current.Resources["FieldLabelStyle"], TextWrapping = TextWrapping.WrapWholeWords };
                var value = new TextBlock { Text = field.Value, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                Grid.SetColumn(value, 1);
                line.Children.Add(label);
                line.Children.Add(value);
                body.Children.Add(line);
            }
            if (section.Text is { } text)
                body.Children.Add(new TextBlock { Text = text, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            DetailSections.Children.Add(new Border { Style = (Style)Application.Current.Resources["CardStyle"], Padding = new Thickness(12), Child = body });
        }
    }

    private void CloseDetail_Click(object sender, RoutedEventArgs e) => HideDetail();
    private void DetailSsh_Click(object sender, RoutedEventArgs e) { if (_detailRow is { } r) ViewModel.OpenSsh(r); }
    private void DetailRdp_Click(object sender, RoutedEventArgs e) { if (_detailRow is { } r) ViewModel.OpenRdp(r); }
    private void DetailCopy_Click(object sender, RoutedEventArgs e) { if (_detailRow is { } r) CopyText(r.CopyAllInfo()); }
    private async void DetailRescan_Click(object sender, RoutedEventArgs e) { if (_detailRow is { } r) await ViewModel.RescanHostAsync(r); }
    private async void DetailProbe_Click(object sender, RoutedEventArgs e) { if (_detailRow is { HasAddress: true } r) await ViewModel.ProbeAsync(new[] { r }); }

    // MARK: - Layout

    private double Root_Width => Root.ActualWidth > 0 ? Root.ActualWidth : 1600;

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutPanes(e.NewSize.Width);

    /// <summary>
    /// The inspector docks beside the content from 1240 pixels and floats over
    /// it below; the command pane sits beside the list when the content area
    /// is at least 1000 pixels wide and under it otherwise.
    /// </summary>
    private void LayoutPanes(double width)
    {
        if (DetailPanel == null) return;
        var detailShown = DetailPanel.Visibility == Visibility.Visible;
        var dock = width >= 1240;
        DetailColumn.Width = new GridLength(detailShown && dock ? 340 : 0);
        Grid.SetColumn(DetailPanel, dock ? 2 : 1);
        DetailPanel.HorizontalAlignment = dock ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        DetailPanel.Translation = dock ? default : new System.Numerics.Vector3(0, 0, 32);
        DetailPanel.Shadow = dock ? null : new ThemeShadow();

        var content = width - SidebarColumn.ActualWidth - DetailColumn.Width.Value;
        var stacked = content < 1000;
        ListColumn.Width = new GridLength(1.1, GridUnitType.Star);
        PaneColumn.Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ListRow.Height = new GridLength(stacked ? 1.15 : 1, GridUnitType.Star);
        PaneRow.Height = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Body.ColumnSpacing = stacked ? 0 : 12;
        Body.RowSpacing = stacked ? 12 : 0;
        Grid.SetColumn(CommandPane, stacked ? 0 : 1);
        Grid.SetRow(CommandPane, stacked ? 1 : 0);
    }

    // MARK: - Helpers

    private async Task NoticeAsync(string title, string message) =>
        await new ContentDialog
        {
            XamlRoot = DialogRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        }.ShowAsync();

    private static async Task<string?> PickFileAsync(params string[] extensions)
    {
        if (App.Services.DemoMode || App.Current.Window is not { } window) return null;
        var picker = new FileOpenPicker();
        foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        return (await picker.PickSingleFileAsync())?.Path;
    }

    private static void CopyLines(IEnumerable<string> lines)
    {
        var text = string.Join(Environment.NewLine, lines);
        if (text.Length > 0) CopyText(text);
    }

    private static void CopyText(string text)
    {
        if (text.Length == 0) return;
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Clipboard unavailable");
        }
    }

    // MARK: - Binding helpers

    public static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility Hide(bool on) => on ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility HasText(string? text) => Show(!string.IsNullOrWhiteSpace(text));
    public static double RowOpacity(bool hasAddress) => hasAddress ? 1 : 0.6;
    public static string CheckName(string name) => $"Select {name}";
    public static string DetailsName(string name) => $"Details for {name}";
    public static string EmptyTitle(bool rosterLoaded) => rosterLoaded ? "Select a room or group" : "Roster not loaded";

    /// <summary>The hostname under the friendly name, when it says something the name does not.</summary>
    public static string SecondName(string friendly, string hostname) =>
        hostname.Length > 0 && !string.Equals(hostname, friendly, StringComparison.OrdinalIgnoreCase) ? hostname : "";

    public static Visibility SecondNameVisibility(string friendly, string hostname) => HasText(SecondName(friendly, hostname));

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    public static Brush StatusBrush(string key) => Brush(key switch
    {
        "online" or "success" => "SystemFillColorSuccessBrush",
        "unreachable" or "timeout" => "SystemFillColorCautionBrush",
        "scanning" or "running" => "AccentFillColorDefaultBrush",
        "failed" => "SystemFillColorCriticalBrush",
        _ => "SystemFillColorNeutralBrush",
    });

    public static Brush ScanBrush(ScanMode mode) => Brush(mode switch
    {
        ScanMode.ReportMate => "SystemFillColorSuccessBrush",
        ScanMode.DnsOnly => "SystemFillColorCautionBrush",
        ScanMode.Limited => "SystemFillColorCriticalBrush",
        _ => "SystemFillColorNeutralBrush",
    });

    public static Brush TrustBrush(CommandTrustLevel trust) => Brush(trust switch
    {
        CommandTrustLevel.Destructive => "SystemFillColorCriticalBrush",
        CommandTrustLevel.Caution => "SystemFillColorCautionBrush",
        _ => "SystemFillColorSuccessBrush",
    });

    public static string TrustTip(CommandTrustLevel trust) => trust.WarningMessage();

    // MARK: - QA capture

    /// <summary>The room and its panes, then each dialog drawn in place.</summary>
    public IReadOnlyList<string> CaptureViews { get; } = new[]
    {
        "room", "room-extended", "detail", "results", "results-expanded", "search", "group",
        "dialog-add-device", "dialog-add-device-manual", "dialog-command-editor", "dialog-history",
        "dialog-placeholder", "dialog-ssh-tabs", "dialog-lab-picker", "dialog-confirm",
    };

    public async Task ShowCaptureViewAsync(string view)
    {
        PreviewHost.Children.Clear();
        PreviewHost.Visibility = Visibility.Collapsed;
        ApplySearch("");
        if (!view.StartsWith("group", StringComparison.Ordinal) && LabsList.SelectedIndex != 0)
        {
            LabsList.SelectedIndex = 0;
            if (LabsList.SelectedItem is SidebarRoom room) await ViewModel.SelectRoomAsync(room.Room);
        }
        if (view != "room-extended" && _extended)
        {
            ExtendedToggle.IsChecked = false;
            Extended_Click(this, new RoutedEventArgs());
        }
        if (view != "detail") HideDetail();

        switch (view)
        {
            case "room-extended":
                ExtendedToggle.IsChecked = true;
                Extended_Click(this, new RoutedEventArgs());
                break;
            case "detail":
                if ((ViewModel.Rows.FirstOrDefault(r => r.HasProbe) ?? ViewModel.Rows.FirstOrDefault()) is { } row)
                {
                    MachinesList.SelectedItem = row;
                    ShowDetail(row);
                }
                break;
            case "results":
            case "results-expanded":
                ViewModel.SelectOnline();
                await ViewModel.RunCommandAsync("Get-Service sshd | Select-Object Status", "Check the SSH service", recordHistory: false);
                if (view == "results-expanded")
                    foreach (var result in ViewModel.Results.Take(2)) result.IsExpanded = true;
                break;
            case "search":
                ApplySearch("an");
                break;
            case "group":
                var group = ViewModel.CustomGroups.FirstOrDefault() ?? ViewModel.CreateGroup("Loaner laptops");
                if (group.Devices.Count == 0)
                {
                    ViewModel.AddRosterDevices(group, ViewModel.Roster.Staff.SelectMany(r => r.Computers).Take(3));
                    ViewModel.AddDevice(group, "LOANER-07", "192.0.2.77");
                }
                GroupsList.SelectedItem = group;
                await ViewModel.SelectGroupAsync(group);
                break;
            case "dialog-add-device":
                Preview(new AddDeviceDialog(ViewModel, null, ViewModel.Rows.Take(2).Select(r => r.Computer).ToList(), () => Task.FromResult<string?>(null)));
                break;
            case "dialog-add-device-manual":
                var manual = new AddDeviceDialog(ViewModel, null, Array.Empty<RosterComputer>(), () => Task.FromResult<string?>(null));
                Preview(manual);
                manual.ShowManual();
                break;
            case "dialog-command-editor":
                Preview(new CommandEditorDialog(ViewModel.Categories.ToList(), ViewModel.Categories.FirstOrDefault(),
                    ViewModel.Categories.FirstOrDefault()?.Commands.FirstOrDefault()));
                break;
            case "dialog-history":
                Preview(new HistoryDialog(new[]
                {
                    new CommandHistoryEntry("Check the SSH service", "Get-Service sshd | Select-Object Status"),
                    new CommandHistoryEntry("Restart", "Restart-Computer -Force"),
                    new CommandHistoryEntry("Custom command", "Get-ComputerInfo | Select-Object OsName, OsVersion"),
                }));
                break;
            case "dialog-placeholder":
                Preview(new PlaceholderDialog(PlaceholderTemplate.Detect("Add a local user", "net user <USERNAME> <PASSWORD> /add")!));
                break;
            case "dialog-ssh-tabs":
                Preview(new SshTabPickerDialog(ViewModel.Rows, terminalAvailable: true));
                break;
            case "dialog-lab-picker":
                Preview(new LabPickerDialog(ViewModel.Roster.Labs, ViewModel.Roster.Labs.Take(2).Select(l => l.Id)));
                break;
            case "dialog-confirm":
                Preview(new ConfirmDialog(string.Format(QuickActions.Restart.ConfirmTitle, "4 machines"), QuickActions.Restart.ConfirmMessage, QuickActions.Restart.Label));
                break;
        }
    }

    private void Preview(ManageDialog dialog)
    {
        PreviewHost.Children.Add(dialog.Preview());
        PreviewHost.Visibility = Visibility.Visible;
    }
}
