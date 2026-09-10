using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using FleetMate.Core.Models.Manage;
using FleetMate.Core.Services.Manage;
using FleetMate.GUI.ViewModels.Manage;

namespace FleetMate.GUI.Views.Manage;

/// <summary>
/// The Manage tab: roster sidebar, machine list for the selected room or
/// group, and a detail panel. Session launch and the command runner attach
/// to this page in later work; this page owns selection, scanning and copy.
/// </summary>
public partial class ManagePage : Page
{
    private readonly ManageViewModel _vm;
    private readonly ManageStateStore _store;
    private Dictionary<string, bool> _sidebarExpanded = new();
    private Dictionary<string, List<string>> _sidebarOrder = new();

    // Row drag state for sidebar reordering.
    private System.Windows.Point _dragStart;
    private SidebarRoomVm? _dragCandidate;
    private ListBox? _dragSourceList;
    private bool _rosterLoaded;
    private bool _suppressSidebarSelection;
    private MachineRowViewModel? _detailRow;

    public ManageViewModel ViewModel => _vm;

    public ManagePage()
    {
        InitializeComponent();
        _vm = BuildViewModel();
        DataContext = _vm;

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ManageViewModel.SelectedCount) or nameof(ManageViewModel.OnlineCount))
                UpdateSelectionCount();
            if (e.PropertyName == nameof(ManageViewModel.ScanMode)) UpdateScanBadge();
            if (e.PropertyName is nameof(ManageViewModel.EffectiveTrust) or nameof(ManageViewModel.EffectiveTrustIsInferred))
                UpdateTrustBadge();
            if (e.PropertyName is nameof(ManageViewModel.ResultSuccessCount) or nameof(ManageViewModel.ResultFailedCount) or nameof(ManageViewModel.ResultOfflineCount))
                UpdateResultCounts();
        };
        _store = (Application.Current as App)?.ManageState ?? new ManageStateStore();
        _sidebarExpanded = _store.LoadSidebarState();
        _sidebarOrder = _store.LoadSidebarOrder();

        _vm.Results.CollectionChanged += (_, _) => { UpdateResultsVisibility(); ApplyResultFilter(); };
        _vm.RunCompleted += OnRunCompleted;
        _vm.CustomGroups.CollectionChanged += (_, _) =>
        {
            NoGroupsText.Visibility = _vm.CustomGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            GroupsCountText.Text = _vm.CustomGroups.Sum(g => g.Devices.Count).ToString();
        };

        Loaded += (_, _) =>
        {
            if (!_rosterLoaded)
            {
                _rosterLoaded = true;
                ReloadRoster();
            }
            Focus();
            UpdateTrustBadge();
            UpdateResultsVisibility();
        };

        PreviewKeyDown += OnPreviewKeyDown;
    }

    private static ManageViewModel BuildViewModel()
    {
        var app = Application.Current as App;
        var config = app?.Config.Manage ?? new Core.Config.ManageConfig();
        var store = app?.ManageState ?? new ManageStateStore();
        IDeviceDirectory? directory = app?.ReportMateService != null ? new ReportMateDeviceDirectory(app.ReportMateService) : null;
        IRemoteRunner? runner = app?.SecureShellService != null ? new SecureShellRemoteRunner(app.SecureShellService) : null;
        var launcher = new RemoteSessionLauncher(config, new RdpCredentialStore());
        return new ManageViewModel(config, store, directory, new NetworkReachabilityProbe(), runner, launcher);
    }

    // ── Remote roster ───────────────────────────────────────────────────
    // The roster is fetched from the source repository through the Azure
    // DevOps REST API on load and on Reload, cached per user; the local file
    // is only the fallback. Removes the dependency on a checkout being pulled.

    private static string RosterCachePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FleetMate", "cache", "computers.csv");

    private string _rosterProvenance = "local file";
    private bool _rosterFetchRunning;

    /// <summary>Show what we have on disk immediately, then refresh from the repo.</summary>
    public void ReloadRoster()
    {
        if (_vm.RosterPathOverride == null && System.IO.File.Exists(RosterCachePath))
        {
            _vm.RosterPathOverride = RosterCachePath;
            _rosterProvenance = $"cached {System.IO.File.GetLastWriteTime(RosterCachePath):HH:mm}";
        }
        ReloadRosterFromDisk();
        _ = FetchRemoteRosterAsync();
    }

    private async Task FetchRemoteRosterAsync()
    {
        if (_rosterFetchRunning) return;
        _rosterFetchRunning = true;
        try
        {
            var app = Application.Current as App;
            var manage = app?.Config.Manage ?? new Core.Config.ManageConfig();
            var adoConfig = app?.Config.AzureDevOps;
            if (adoConfig == null || string.IsNullOrWhiteSpace(adoConfig.Organization))
            {
                _rosterProvenance = "local file (DevOps not configured)";
                Dispatcher.Invoke(UpdateRosterFooter);
                return;
            }

            using var devops = new Core.Services.Projects.AzureDevOpsService(adoConfig);
            var content = await devops.GetRepositoryItemContentAsync(
                manage.RosterRepoProject, manage.RosterRepo, manage.RosterRepoPath);

            if (string.IsNullOrWhiteSpace(content))
            {
                _rosterProvenance = System.IO.File.Exists(RosterCachePath)
                    ? $"cached {System.IO.File.GetLastWriteTime(RosterCachePath):HH:mm} (fetch failed)"
                    : "local file (fetch failed)";
                Dispatcher.Invoke(UpdateRosterFooter);
                return;
            }

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(RosterCachePath)!);
            System.IO.File.WriteAllText(RosterCachePath, content);
            _rosterProvenance = $"{manage.RosterRepoProject}/{manage.RosterRepo} · fetched {DateTime.Now:HH:mm}";

            Dispatcher.Invoke(() =>
            {
                _vm.RosterPathOverride = RosterCachePath;
                ReloadRosterFromDisk();
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Roster fetch failed");
            _rosterProvenance = System.IO.File.Exists(RosterCachePath)
                ? $"cached {System.IO.File.GetLastWriteTime(RosterCachePath):HH:mm} (fetch failed)"
                : "local file (fetch failed)";
            Dispatcher.Invoke(UpdateRosterFooter);
        }
        finally
        {
            _rosterFetchRunning = false;
        }
    }

    private void ReloadRosterFromDisk()
    {
        _vm.LoadRoster();
        _suppressSidebarSelection = true;
        LabsList.ItemsSource = ApplySavedOrder("Labs", _vm.Roster.Labs.Select(r => SidebarRoomVm.From(r, RosterSection.Labs)).ToList());
        KiosksList.ItemsSource = ApplySavedOrder("Kiosks", _vm.Roster.Kiosks.Select(r => SidebarRoomVm.From(r, RosterSection.Kiosks)).ToList());
        StaffList.ItemsSource = ApplySavedOrder("Staff", _vm.Roster.Staff.Select(r => SidebarRoomVm.From(r, RosterSection.Staff)).ToList());
        FacultyList.ItemsSource = ApplySavedOrder("Faculty", _vm.Roster.Faculty.Select(r => SidebarRoomVm.From(r, RosterSection.Faculty)).ToList());
        _suppressSidebarSelection = false;
        NoGroupsText.Visibility = _vm.CustomGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Section badges count machines, not rooms — the Mac sidebar's numbers.
        LabsCountText.Text = _vm.Roster.Labs.Sum(r => r.Count).ToString();
        KiosksCountText.Text = _vm.Roster.Kiosks.Sum(r => r.Count).ToString();
        StaffCountText.Text = _vm.Roster.Staff.Sum(r => r.Count).ToString();
        FacultyCountText.Text = _vm.Roster.Faculty.Sum(r => r.Count).ToString();
        GroupsCountText.Text = _vm.CustomGroups.Sum(g => g.Devices.Count).ToString();

        UpdateRosterFooter();

        ApplySidebarExpandedState();

        var app = Application.Current as App;
        if (!_vm.RosterLoaded)
            EmptyHint.Text = _vm.RosterStatus;
        else if (app?.SecureShellService == null)
            EmptyHint.Text = "Rooms load from the roster. Machine details need the SSH key configured in Settings (Manage).";
        else
            EmptyHint.Text = "Rooms come from the roster configured in Settings.";
        UpdateScanBadge();
    }

    private void OnReloadRosterClicked(object sender, RoutedEventArgs e) => ReloadRoster();

    /// <summary>"22 labs · 445 machines · Devices/Cimian · fetched 14:32".</summary>
    private void UpdateRosterFooter()
    {
        RosterFooterText.Text = _vm.RosterLoaded
            ? $"{_vm.Roster.Labs.Count} labs · {_vm.Roster.Source.Count} machines · {_rosterProvenance}"
            : (_vm.RosterStatus is { Length: > 0 } status ? status : "Check the roster path in Settings › Manage");
    }

    // ── Keyboard ────────────────────────────────────────────────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Shift)) return;
        switch (e.Key)
        {
            case Key.R: ReloadRoster(); e.Handled = true; break;
            case Key.S: OnScanClicked(sender, e); e.Handled = true; break;
            case Key.A: _vm.SelectOnline(); e.Handled = true; break;
        }
    }

    // ── Sidebar ─────────────────────────────────────────────────────────

    private async void OnRoomSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSidebarSelection || sender is not ListBox list || list.SelectedItem is not SidebarRoomVm room) return;
        ClearOtherSidebarSelections(list);
        HideDetail();
        await _vm.SelectRoomAsync(room.Room);
    }

    private async void OnGroupSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSidebarSelection || GroupsList.SelectedItem is not CustomGroup group) return;
        ClearOtherSidebarSelections(GroupsList);
        HideDetail();
        await _vm.SelectGroupAsync(group);
    }

    private void ClearOtherSidebarSelections(ListBox keep)
    {
        _suppressSidebarSelection = true;
        foreach (var list in new[] { LabsList, KiosksList, StaffList, FacultyList, GroupsList })
            if (list != keep) list.SelectedItem = null;
        _suppressSidebarSelection = false;
    }

    private void OnToggleSection(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag?.ToString() is not { } key) return;
        var expanded = !(_sidebarExpanded.TryGetValue(key, out var current) ? current : true);
        _sidebarExpanded[key] = expanded;
        _store.SaveSidebarState(_sidebarExpanded);
        ApplySidebarExpandedState();
    }

    private (ListBox List, Button Toggle)? SectionControls(string key) => key switch
    {
        "Labs" => (LabsList, LabsToggle),
        "Kiosks" => (KiosksList, KiosksToggle),
        "Staff" => (StaffList, StaffToggle),
        "Faculty" => (FacultyList, FacultyToggle),
        _ => null
    };

    private void ApplySidebarExpandedState()
    {
        foreach (var key in new[] { "Labs", "Kiosks", "Staff", "Faculty" })
        {
            if (SectionControls(key) is not { } controls) continue;
            var expanded = !_sidebarExpanded.TryGetValue(key, out var value) || value;
            controls.List.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            controls.Toggle.Content = expanded ? "–" : "+";
        }
    }

    // ── Sidebar row reordering ──────────────────────────────────────────
    // Rows drag onto another row in the same section and take its slot; the
    // order persists per section as ordered row ids (the room's group key).
    // Rows missing from the saved list keep the roster's default order after
    // the arranged ones, so new labs append rather than disappear.

    /// <summary>Saved order first (matching by row id), then the rest in roster order.</summary>
    private List<SidebarRoomVm> ApplySavedOrder(string section, List<SidebarRoomVm> rows)
    {
        if (!_sidebarOrder.TryGetValue(section, out var saved) || saved.Count == 0) return rows;
        var ordered = new List<SidebarRoomVm>();
        foreach (var id in saved)
        {
            var hit = rows.FirstOrDefault(r => r.Title == id && !ordered.Contains(r));
            if (hit != null) ordered.Add(hit);
        }
        ordered.AddRange(rows.Where(r => !ordered.Contains(r)));
        return ordered;
    }

    private static SidebarRoomVm? RowUnderMouse(ListBox list, object originalSource)
    {
        var element = originalSource as DependencyObject;
        while (element != null && element is not ListBoxItem)
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        return (element as ListBoxItem)?.DataContext as SidebarRoomVm;
    }

    private void OnRoomListMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list) return;
        _dragStart = e.GetPosition(list);
        _dragCandidate = RowUnderMouse(list, e.OriginalSource);
        _dragSourceList = list;
    }

    private void OnRoomListMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate == null || _dragSourceList == null || sender != _dragSourceList) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var moved = e.GetPosition(_dragSourceList) - _dragStart;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var dragged = _dragCandidate;
        _dragCandidate = null;
        DragDrop.DoDragDrop(_dragSourceList, new DataObject("FleetMateSidebarRoom", dragged), DragDropEffects.Move);
        ClearDropIndicators();
    }

    private void OnRoomListDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (sender is not ListBox list || !e.Data.GetDataPresent("FleetMateSidebarRoom")
            || list != _dragSourceList)
        {
            // Drops across sections are ignored.
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Move;

        var target = RowUnderMouse(list, e.OriginalSource);
        foreach (var row in list.ItemsSource.Cast<SidebarRoomVm>())
            row.IsDropTarget = row == target && row != e.Data.GetData("FleetMateSidebarRoom");
    }

    private void OnRoomListDragLeave(object sender, DragEventArgs e) => ClearDropIndicators();

    private void OnRoomListDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ClearDropIndicators();
        if (sender is not ListBox list || list != _dragSourceList) return;
        if (e.Data.GetData("FleetMateSidebarRoom") is not SidebarRoomVm dragged) return;
        var target = RowUnderMouse(list, e.OriginalSource);
        if (target == null || target == dragged) return;

        var rows = list.ItemsSource.Cast<SidebarRoomVm>().ToList();
        var targetIndex = rows.IndexOf(target);
        if (targetIndex < 0 || !rows.Remove(dragged)) return;
        // Insert at the target's pre-removal slot: the target moves down when
        // the drag came from below it, up when it came from above.
        rows.Insert(Math.Min(targetIndex, rows.Count), dragged);
        list.ItemsSource = rows;

        var section = list.Tag?.ToString() ?? "";
        _sidebarOrder[section] = rows.Select(r => r.Title).ToList();
        _store.SaveSidebarOrder(_sidebarOrder);
    }

    private void ClearDropIndicators()
    {
        foreach (var list in new[] { LabsList, KiosksList, StaffList, FacultyList })
        {
            if (list.ItemsSource == null) continue;
            foreach (var row in list.ItemsSource.Cast<SidebarRoomVm>())
                row.IsDropTarget = false;
        }
    }

    private void OnResetSectionOrder(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Parent: ContextMenu { PlacementTarget: ListBox list } }) return;
        var section = list.Tag?.ToString() ?? "";
        if (_sidebarOrder.Remove(section))
        {
            _store.SaveSidebarOrder(_sidebarOrder);
            ReloadRoster();
        }
    }

    /// <summary>
    /// The Curriculum header's picker: choose several labs (grouped by area)
    /// and work on their machines as one selection.
    /// </summary>
    private async void OnOpenLabPicker(object sender, RoutedEventArgs e)
    {
        if (_vm.Roster.Labs.Count == 0) return;
        var dialog = new LabPickerDialog(_vm.Roster.Labs) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.SelectedRooms.Count == 0) return;

        var computers = dialog.SelectedRooms
            .SelectMany(r => r.Computers)
            .GroupBy(c => c.Serial)
            .Select(g => g.First())
            .ToList();
        ClearOtherSidebarSelections(SearchList);
        HideDetail();
        var label = dialog.SelectedRooms.Count == 1
            ? dialog.SelectedRooms[0].Name
            : $"{dialog.SelectedRooms.Count} labs";
        await _vm.SelectSearchResultsAsync(computers, label);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text?.Trim() ?? "";
        if (q.Length == 0)
        {
            SearchPanel.Visibility = Visibility.Collapsed;
            SectionsScroll.Visibility = Visibility.Visible;
            return;
        }
        var results = _vm.SearchResults(q);
        SearchList.ItemsSource = results;
        SearchCountText.Text = results.Count == 1 ? "1 match" : $"{results.Count} matches";
        SearchPanel.Visibility = Visibility.Visible;
        SectionsScroll.Visibility = Visibility.Collapsed;
    }

    private void OnSearchSelectAll(object sender, RoutedEventArgs e) => SearchList.SelectAll();

    private async void OnUseSearchSelection(object sender, RoutedEventArgs e)
    {
        var picked = SearchList.SelectedItems.Cast<RosterComputer>().ToList();
        if (picked.Count == 0) picked = SearchList.Items.Cast<RosterComputer>().ToList();
        if (picked.Count == 0) return;
        ClearOtherSidebarSelections(SearchList);
        HideDetail();
        await _vm.SelectSearchResultsAsync(picked, SearchBox.Text);
    }

    // ── Custom groups ───────────────────────────────────────────────────

    private void OnCreateGroup(object sender, RoutedEventArgs e)
    {
        var name = TextPromptDialog.Ask(Window.GetWindow(this), "New custom group", "Group name", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        var group = _vm.CreateGroup(name);
        GroupsList.SelectedItem = group;
    }

    private void OnRenameGroup(object sender, RoutedEventArgs e)
    {
        if (GroupsList.SelectedItem is not CustomGroup group) return;
        var name = TextPromptDialog.Ask(Window.GetWindow(this), "Rename group", "Group name", group.Name);
        if (string.IsNullOrWhiteSpace(name) || name == group.Name) return;
        _vm.RenameGroup(group, name);
    }

    private void OnDeleteGroup(object sender, RoutedEventArgs e)
    {
        if (GroupsList.SelectedItem is not CustomGroup group) return;
        var answer = MessageBox.Show(Window.GetWindow(this), $"Delete the custom group \"{group.Name}\"?", "Delete group",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        _vm.DeleteGroup(group);
    }

    private void OnAddDevicesToGroupMenu(object sender, RoutedEventArgs e)
    {
        if (GroupsList.SelectedItem is not CustomGroup group) return;
        ShowAddDeviceDialog(group, Array.Empty<RosterComputer>());
    }

    private void OnAddDeviceClicked(object sender, RoutedEventArgs e) =>
        ShowAddDeviceDialog(_vm.SelectedGroup, Array.Empty<RosterComputer>());

    private void OnAddSelectedToGroup(object sender, RoutedEventArgs e)
    {
        var picked = _vm.SelectedRows.Select(r => r.Computer).ToList();
        if (picked.Count == 0 && MachinesGrid.SelectedItems.Count > 0)
            picked = MachinesGrid.SelectedItems.Cast<MachineRowViewModel>().Select(r => r.Computer).ToList();
        ShowAddDeviceDialog(null, picked);
    }

    private void ShowAddDeviceDialog(CustomGroup? target, IReadOnlyList<RosterComputer> preselected)
    {
        var dialog = new AddDeviceDialog(_vm, target, preselected) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
        if (dialog.ResultGroup != null && GroupsList.SelectedItem != dialog.ResultGroup)
            GroupsList.SelectedItem = dialog.ResultGroup;
    }

    // ── Scan / probe ────────────────────────────────────────────────────

    private async void OnScanClicked(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasSelection) return;
        var stored = _vm.SelectedGroup?.Devices.Where(d => d.Ip.Length > 0).ToDictionary(d => d.Computer.Serial, d => d.Ip);
        await _vm.ScanAsync(stored);
    }

    private void OnCancelScanClicked(object sender, RoutedEventArgs e)
    {
        _vm.CancelScan();
        _vm.CancelProbe();
    }

    private async void OnFetchInfoClicked(object sender, RoutedEventArgs e)
    {
        if (!_vm.CanProbe)
        {
            MessageBox.Show(Window.GetWindow(this), "Machine details need the fleet SSH key. Set the key path in Settings, Manage tab.",
                "SSH not configured", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await _vm.ProbeAllAsync();
    }

    private async void OnRescanRow(object sender, RoutedEventArgs e) { if (ContextRow() is { } row) await _vm.RescanHostAsync(row); }
    private async void OnRescanRowButton(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } row) await _vm.RescanHostAsync(row); }
    private async void OnProbeRow(object sender, RoutedEventArgs e) { if (ContextRow() is { } row && row.HasAddress) await _vm.ProbeAsync(new[] { row }); }
    private async void OnDetailRescan(object sender, RoutedEventArgs e) { if (_detailRow != null) { await _vm.RescanHostAsync(_detailRow); RefreshDetail(); } }
    private async void OnDetailProbe(object sender, RoutedEventArgs e) { if (_detailRow != null && _detailRow.HasAddress) { await _vm.ProbeAsync(new[] { _detailRow }); RefreshDetail(); } }

    // ── Command runner ──────────────────────────────────────────────────

    private async void OnRun(object sender, RoutedEventArgs e) => await RunResolvedCommandAsync();

    private async void OnCustomCommandKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; await RunResolvedCommandAsync(); }
    }

    private async Task RunResolvedCommandAsync()
    {
        if (!_vm.CanRun)
        {
            MessageBox.Show(Window.GetWindow(this), "Running commands needs the fleet SSH key. Set the key path in Settings, Manage tab.",
                "SSH not configured", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var command = _vm.ResolvedCommandString;
        if (command.Length == 0) return;
        var label = _vm.ResolvedCommandLabel;
        var targets = _vm.OnlineSelectedRows.ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "Check at least one online machine first (Select › Online checks every machine that answered).",
                "No targets", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (PlaceholderTemplate.Detect(label, command) is { } template)
        {
            var prompt = new PlaceholderDialog(template) { Owner = Window.GetWindow(this) };
            if (prompt.ShowDialog() != true || prompt.ResolvedCommand == null) return;
            command = prompt.ResolvedCommand;
        }

        var trust = _vm.EffectiveTrust;
        if (!ConfirmTrust(trust, label, targets.Count)) return;
        await _vm.RunCommandAsync(command, label, recordHistory: true, targets);
    }

    private bool ConfirmTrust(CommandTrustLevel trust, string label, int count)
    {
        if (trust == CommandTrustLevel.Safe) return true;
        var answer = MessageBox.Show(Window.GetWindow(this),
            $"{trust.WarningMessage()}\n\n{label}\nTargets: {count} machine{(count == 1 ? "" : "s")}",
            trust.WarningTitle(), MessageBoxButton.OKCancel, trust == CommandTrustLevel.Destructive ? MessageBoxImage.Warning : MessageBoxImage.Question);
        return answer == MessageBoxResult.OK;
    }

    private void OnKillRun(object sender, RoutedEventArgs e) => _vm.KillRun();

    private void UpdateTrustBadge()
    {
        var trust = _vm.EffectiveTrust;
        TrustText.Text = trust.Label() + (_vm.EffectiveTrustIsInferred ? " (inferred)" : "");
        TrustDot.Fill = trust switch
        {
            CommandTrustLevel.Destructive => Brushes.IndianRed,
            CommandTrustLevel.Caution => Brushes.Goldenrod,
            _ => Brushes.MediumSeaGreen
        };
        TrustBadge.ToolTip = trust.WarningMessage();
    }

    private void OnHistory(object sender, RoutedEventArgs e)
    {
        var dialog = new HistoryDialog(_vm.History) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        switch (dialog.Result)
        {
            case HistoryDialog.Outcome.Cleared:
                _vm.ClearHistory();
                break;
            case HistoryDialog.Outcome.Load when dialog.Chosen != null:
                _vm.CustomCommand = dialog.Chosen.Command;
                break;
            case HistoryDialog.Outcome.Rerun when dialog.Chosen != null:
                _vm.CustomCommand = dialog.Chosen.Command;
                _ = RunResolvedCommandAsync();
                break;
        }
    }

    private void OnAddCommand(object sender, RoutedEventArgs e)
    {
        var dialog = new CommandEditorDialog(_vm.Categories.ToList(), _vm.SelectedCategory, null, _vm.CustomCommand) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Result != CommandEditorDialog.Outcome.Saved) return;
        var category = dialog.ResultCategory ?? _vm.AddCategory(dialog.ResultNewCategoryName);
        _vm.AddCommand(category, dialog.ResultLabel, dialog.ResultCommand, dialog.ResultTrust);
        _vm.CustomCommand = "";
    }

    private void OnEditCommand(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedCategory == null || _vm.SelectedCommand == null) return;
        var category = _vm.SelectedCategory;
        var command = _vm.SelectedCommand;
        var dialog = new CommandEditorDialog(_vm.Categories.ToList(), category, command) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        if (dialog.Result == CommandEditorDialog.Outcome.Deleted)
        {
            _vm.DeleteCommand(category, command);
            return;
        }
        if (dialog.Result != CommandEditorDialog.Outcome.Saved) return;
        var target = dialog.ResultCategory ?? _vm.AddCategory(dialog.ResultNewCategoryName);
        if (target != category)
        {
            _vm.DeleteCommand(category, command);
            _vm.AddCommand(target, dialog.ResultLabel, dialog.ResultCommand, dialog.ResultTrust);
        }
        else
        {
            _vm.EditCommand(category, command, dialog.ResultLabel, dialog.ResultCommand, dialog.ResultTrust);
        }
    }

    // Quick actions: confirmation-gated, run on the checked online machines.
    private void OnQuickRestart(object sender, RoutedEventArgs e) => _ = RunQuickActionAsync(QuickActions.Restart);
    private void OnQuickLogOut(object sender, RoutedEventArgs e) => _ = RunQuickActionAsync(QuickActions.LogOutUser);
    private void OnQuickSleep(object sender, RoutedEventArgs e) => _ = RunQuickActionAsync(QuickActions.Sleep);
    private void OnQuickLock(object sender, RoutedEventArgs e) => _ = RunQuickActionAsync(QuickActions.LockScreen);

    private async Task RunQuickActionAsync(QuickActions.QuickAction action)
    {
        var targets = _vm.OnlineSelectedRows.ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "Check at least one online machine first.", action.Label, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var what = targets.Count == 1 ? targets[0].FriendlyName : $"{targets.Count} machines";
        var answer = MessageBox.Show(Window.GetWindow(this), action.ConfirmMessage, string.Format(action.ConfirmTitle, what),
            MessageBoxButton.OKCancel, action.Trust == CommandTrustLevel.Destructive ? MessageBoxImage.Warning : MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        await _vm.RunCommandAsync(action.Command, action.Label, recordHistory: true, targets);
    }

    // ── Results pane ────────────────────────────────────────────────────

    private void UpdateResultsVisibility()
    {
        var show = _vm.Results.Count > 0;
        ResultsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ResultsSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ResultsSplitterRow.Height = show ? new GridLength(6) : new GridLength(0);
        if (show && ResultsRow.Height.Value == 0) ResultsRow.Height = new GridLength(1.1, GridUnitType.Star);
        if (!show) ResultsRow.Height = new GridLength(0);
        ResultsTitle.Text = _vm.LastRunLabel.Length > 0 ? $"Results: {_vm.LastRunLabel}" : "Results";
    }

    private void UpdateResultCounts()
    {
        ResultsCounts.Text = _vm.Results.Count == 0 ? "" : $"{_vm.ResultSuccessCount} ok · {_vm.ResultFailedCount} failed · {_vm.ResultOfflineCount} offline";
        ResultsTitle.Text = _vm.LastRunLabel.Length > 0 ? $"Results: {_vm.LastRunLabel}" : "Results";
    }

    private void OnResultFilterChanged(object sender, RoutedEventArgs e) => ApplyResultFilter();

    private IEnumerable<CommandResultViewModel> FilteredResults()
    {
        IEnumerable<CommandResultViewModel> all = _vm.Results;
        if (FilterSuccess?.IsChecked == true) return all.Where(r => r.Status == CommandRunStatus.Success);
        if (FilterFailed?.IsChecked == true) return all.Where(r => r.Status is CommandRunStatus.Failed or CommandRunStatus.AuthFailed or CommandRunStatus.Timeout);
        if (FilterOffline?.IsChecked == true) return all.Where(r => r.Status == CommandRunStatus.Offline);
        return all;
    }

    private void ApplyResultFilter()
    {
        if (ResultsList == null) return;
        if (FilterAll?.IsChecked == true) { ResultsList.ItemsSource = _vm.Results; return; }
        ResultsList.ItemsSource = FilteredResults().ToList();
    }

    private void OnClearResults(object sender, RoutedEventArgs e) { _vm.KillRun(); _vm.ClearResults(); }

    private void OnCopyVisibleResults(object sender, RoutedEventArgs e) =>
        CopyLines(FilteredResults().Select(r => r.Formatted()));

    private void OnSelectFailedTargets(object sender, RoutedEventArgs e)
    {
        var failed = _vm.Results.Where(r => r.Status is CommandRunStatus.Failed or CommandRunStatus.AuthFailed or CommandRunStatus.Timeout or CommandRunStatus.Offline)
            .Select(r => r.Serial).ToHashSet();
        foreach (var row in _vm.Rows) row.IsSelected = failed.Contains(row.Serial);
    }

    private CommandResultViewModel? ContextResult() => ResultsList.SelectedItem as CommandResultViewModel;
    private MachineRowViewModel? RowForResult(CommandResultViewModel r) => _vm.Rows.FirstOrDefault(x => x.Serial == r.Serial);

    private void OnResultOpenSsh(object sender, RoutedEventArgs e) { if (ContextResult() is { } r && RowForResult(r) is { } row) _vm.OpenSsh(row); }
    private void OnResultOpenRdp(object sender, RoutedEventArgs e) { if (ContextResult() is { } r && RowForResult(r) is { } row) _vm.OpenRdp(row); }
    private void OnResultOpenBoth(object sender, RoutedEventArgs e) { if (ContextResult() is { } r && RowForResult(r) is { } row) _vm.OpenSshAndRdp(row); }
    private void OnResultCopyHostnameIp(object sender, RoutedEventArgs e) { if (ContextResult() is { } r) CopyText($"{(r.Hostname.Length > 0 ? r.Hostname : r.Name)}	{r.Ip}"); }
    private void OnResultCopyHostname(object sender, RoutedEventArgs e) { if (ContextResult() is { } r) CopyText(r.Hostname.Length > 0 ? r.Hostname : r.Name); }
    private void OnResultCopyIp(object sender, RoutedEventArgs e) { if (ContextResult() is { } r) CopyText(r.Ip); }
    private void OnResultCopyOutput(object sender, RoutedEventArgs e) { if (ContextResult() is { } r) CopyText(r.Output); }
    private void OnResultCopyError(object sender, RoutedEventArgs e) { if (ContextResult() is { } r) CopyText(r.ErrorOutput); }
    private void OnResultCopyFull(object sender, RoutedEventArgs e) { if (ContextResult() is { } r) CopyText(r.Formatted()); }

    /// <summary>When the window is not in front, flash it so the operator notices the batch finished.</summary>
    private void OnRunCompleted(int success, int failed, int offline)
    {
        var window = Window.GetWindow(this);
        if (window == null || window.IsActive) return;
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            var info = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = handle, dwFlags = 0x0E, uCount = 3, dwTimeout = 0 };
            FlashWindowEx(ref info);
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "FlashWindowEx failed");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    // ── Sessions ────────────────────────────────────────────────────────

    private void OnOpenSshRow(object sender, RoutedEventArgs e) { if (ContextRow() is { } r) _vm.OpenSsh(r); }
    private void OnOpenRdpRow(object sender, RoutedEventArgs e) { if (ContextRow() is { } r) _vm.OpenRdp(r); }
    private void OnOpenBothRow(object sender, RoutedEventArgs e) { if (ContextRow() is { } r) _vm.OpenSshAndRdp(r); }
    private void OnOpenSshRowButton(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) _vm.OpenSsh(r); }
    private void OnOpenRdpRowButton(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) _vm.OpenRdp(r); }
    private void OnOpenBothRowButton(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) _vm.OpenSshAndRdp(r); }
    private void OnDetailSsh(object sender, RoutedEventArgs e) { if (_detailRow != null) _vm.OpenSsh(_detailRow); }
    private void OnDetailRdp(object sender, RoutedEventArgs e) { if (_detailRow != null) _vm.OpenRdp(_detailRow); }

    private void OnOpenSshTabs(object sender, RoutedEventArgs e)
    {
        var candidates = _vm.Rows.Where(r => r.IsOnline).ToList();
        if (candidates.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "No machine in this room is online.", "Open SSH tabs", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new SshTabPickerDialog(candidates, RemoteSessionLauncher.WindowsTerminalAvailable) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) _vm.OpenSshTabs(dialog.Chosen);
    }

    // ── Selection ───────────────────────────────────────────────────────

    private void OnSelectAll(object sender, RoutedEventArgs e) => _vm.SelectAll();
    private void OnSelectOnline(object sender, RoutedEventArgs e) => _vm.SelectOnline();
    private void OnSelectNone(object sender, RoutedEventArgs e) => _vm.SelectNone();

    private void OnHeaderCheckBoxClicked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox box && box.IsChecked == true) _vm.SelectAll(); else _vm.SelectNone();
    }

    private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DetailColumn.Width.Value > 0 && MachinesGrid.SelectedItem is MachineRowViewModel row) ShowDetail(row);
    }

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MachinesGrid.SelectedItem is MachineRowViewModel row) ShowDetail(row);
    }

    private void UpdateSelectionCount()
    {
        var selected = _vm.SelectedCount;
        var online = _vm.OnlineCount;
        SelectionCountText.Text = selected == 0 ? $"{online} online" : $"{selected} selected · {online} online";
        HeaderCheckBox.IsChecked = _vm.Rows.Count > 0 && selected == _vm.Rows.Count ? true : selected == 0 ? false : null;
    }

    private void UpdateScanBadge()
    {
        ScanBadgeDot.Fill = _vm.ScanMode switch
        {
            ScanMode.ReportMate => Brushes.MediumSeaGreen,
            ScanMode.DnsOnly => Brushes.Goldenrod,
            ScanMode.Limited => Brushes.IndianRed,
            _ => Brushes.Gray
        };
    }

    private void OnDensityChanged(object sender, RoutedEventArgs e)
    {
        var extended = DensityToggle.IsChecked == true;
        var vis = extended ? Visibility.Visible : Visibility.Collapsed;
        OsColumn.Visibility = vis;
        UptimeColumn.Visibility = vis;
        JoinColumn.Visibility = vis;
        ClientIdColumn.Visibility = vis;
    }

    private void OnActionsClicked(object sender, RoutedEventArgs e)
    {
        ActionsMenu.PlacementTarget = ActionsButton;
        ActionsMenu.IsOpen = true;
    }

    // ── Detail panel ────────────────────────────────────────────────────

    private void OnShowDetailRow(object sender, RoutedEventArgs e) { if (ContextRow() is { } row) ShowDetail(row); }
    private void OnShowDetailRowButton(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } row) ShowDetail(row); }
    private void OnCloseDetail(object sender, RoutedEventArgs e) => HideDetail();
    private void OnDetailCopyAll(object sender, RoutedEventArgs e) { if (_detailRow != null) CopyText(_detailRow.CopyAllInfo()); }

    private void ShowDetail(MachineRowViewModel row)
    {
        if (_detailRow != null) _detailRow.PropertyChanged -= OnDetailRowChanged;
        _detailRow = row;
        row.PropertyChanged += OnDetailRowChanged;
        DetailColumn.Width = new GridLength(340);
        RefreshDetail();
    }

    private void HideDetail()
    {
        if (_detailRow != null) _detailRow.PropertyChanged -= OnDetailRowChanged;
        _detailRow = null;
        DetailColumn.Width = new GridLength(0);
    }

    private void OnDetailRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RefreshDetail();

    private void RefreshDetail()
    {
        if (_detailRow == null) return;
        DetailTitle.Text = _detailRow.FriendlyName + (_detailRow.Computer.HasHostname && _detailRow.Computer.Hostname != _detailRow.FriendlyName ? $"  ·  {_detailRow.Computer.Hostname}" : "");
        DetailStatus.Text = _detailRow.StatusLabel + (_detailRow.HasAddress ? $"  ·  {_detailRow.AddressLabel}" : "");
        DetailBody.Text = _detailRow.CopyAllInfo();
        DetailRemoteHelp.Text = _detailRow.RemoteAccessHelp;
        DetailProbeError.Text = _detailRow.ProbeError ?? "";
    }

    // ── Copy ────────────────────────────────────────────────────────────

    private void OnCopyHostname(object sender, RoutedEventArgs e) { if (ContextRow() is { } r) CopyText(r.Computer.HasHostname ? r.Computer.Hostname : r.Name); }
    private void OnCopyIp(object sender, RoutedEventArgs e) { if (ContextRow() is { } r && r.HasAddress) CopyText(r.Ip); }
    private void OnCopySerial(object sender, RoutedEventArgs e) { if (ContextRow() is { } r) CopyText(r.Serial); }
    private void OnCopyAsset(object sender, RoutedEventArgs e) { if (ContextRow() is { } r) CopyText(r.Computer.Asset); }
    private void OnCopyOs(object sender, RoutedEventArgs e) { if (ContextRow() is { } r) CopyText(r.OsLabel); }
    private void OnCopyAllInfo(object sender, RoutedEventArgs e) { if (ContextRow() is { } r) CopyText(r.CopyAllInfo()); }

    private void OnCopySelectedHostnames(object sender, RoutedEventArgs e) =>
        CopyLines(TargetRows().Select(r => r.Computer.HasHostname ? r.Computer.Hostname : r.Name));
    private void OnCopySelectedAssets(object sender, RoutedEventArgs e) =>
        CopyLines(TargetRows().Select(r => r.Computer.Asset).Where(a => a.Length > 0));
    private void OnCopySelectedInventory(object sender, RoutedEventArgs e) =>
        CopyLines(TargetRows().Select(r => r.CopyLine));

    /// <summary>Checked rows win; otherwise the grid's highlighted rows; otherwise every row.</summary>
    private List<MachineRowViewModel> TargetRows()
    {
        var checkedRows = _vm.SelectedRows.ToList();
        if (checkedRows.Count > 0) return checkedRows;
        if (MachinesGrid.SelectedItems.Count > 0) return MachinesGrid.SelectedItems.Cast<MachineRowViewModel>().ToList();
        return _vm.Rows.ToList();
    }

    private MachineRowViewModel? ContextRow() =>
        MachinesGrid.SelectedItem as MachineRowViewModel ?? MachinesGrid.CurrentItem as MachineRowViewModel;

    private static MachineRowViewModel? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as MachineRowViewModel;

    private static void CopyLines(IEnumerable<string> lines)
    {
        var text = string.Join(Environment.NewLine, lines);
        if (text.Length > 0) CopyText(text);
    }

    private static void CopyText(string text)
    {
        try { Clipboard.SetText(text); }
        catch (Exception ex) { Serilog.Log.Debug(ex, "Clipboard unavailable"); }
    }
}

// ── Converters ──────────────────────────────────────────────────────────

public class StatusKeyToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value?.ToString() switch
    {
        "online" or "success" => Brushes.MediumSeaGreen,
        "unreachable" or "timeout" => Brushes.Goldenrod,
        "scanning" or "running" => Brushes.DodgerBlue,
        "failed" => Brushes.IndianRed,
        _ => Brushes.Gray
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value?.ToString()) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"30 machines", "1 machine" — the sidebar caption's count part.</summary>
public class MachinesCountConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int count ? (count == 1 ? "1 machine" : $"{count} machines") : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// A sidebar room row, macOS density: section icon, title, and an
/// "area · room · N machines" caption. For Curriculum the title is the
/// roster's fleet value (the room number when the row has no fleet); the
/// caption's area is the most common lease area among members and its room
/// the most common location, omitted when it equals the title.
/// </summary>
public class SidebarRoomVm : System.ComponentModel.INotifyPropertyChanged
{
    public RosterRoom Room { get; init; } = new();
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Glyph { get; init; } = "";

    private bool _isDropTarget;

    /// <summary>True while a sidebar row drag hovers this row; the template
    /// paints the 2px accent line on the top edge.</summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set
        {
            if (_isDropTarget == value) return;
            _isDropTarget = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsDropTarget)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public static SidebarRoomVm From(RosterRoom room, RosterSection section)
    {
        // Curriculum titles by the roster's fleet value, falling back to the
        // room number when the row has no fleet — read from the members, since
        // a fleet-less room's DisplayName carries its area, not a fleet.
        var fleet = Dominant(room.Computers.Select(c => c.Fleet));
        var title = section == RosterSection.Labs && fleet is { Length: > 0 }
            ? fleet
            : room.Number;

        var area = Dominant(room.Computers.Select(c => c.Area));
        var location = Dominant(room.Computers.Select(c => c.Location));

        var parts = new List<string>();
        if (area is { Length: > 0 } && !string.Equals(area, title, StringComparison.OrdinalIgnoreCase))
            parts.Add(area);
        if (location is { Length: > 0 } && !string.Equals(location, title, StringComparison.OrdinalIgnoreCase))
            parts.Add(location);
        parts.Add(room.Count == 1 ? "1 machine" : $"{room.Count} machines");

        var glyph = section switch
        {
            RosterSection.Labs => "",
            RosterSection.Kiosks => "",
            RosterSection.Staff => "",
            RosterSection.Faculty => "",
            _ => ""
        };

        return new SidebarRoomVm
        {
            Room = room,
            Title = title,
            Subtitle = string.Join(" · ", parts),
            Glyph = glyph
        };
    }

    private static string? Dominant(IEnumerable<string> values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v))
              .GroupBy(v => v)
              .OrderByDescending(g => g.Count())
              .FirstOrDefault()?.Key;
}

/// <summary>
/// The Curriculum header's lab picker: labs grouped by area, each with a
/// checkbox, and an area-level checkbox that toggles the whole group — the
/// macOS picker's multi-select, with checkboxes standing in for its drag
/// and drop.
/// </summary>
public class LabPickerDialog : Window
{
    private readonly List<(CheckBox Box, RosterRoom Room)> _labBoxes = new();

    public List<RosterRoom> SelectedRooms { get; } = new();

    public LabPickerDialog(IReadOnlyList<RosterRoom> labs)
    {
        Title = "Select labs";
        Width = 420;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel { Margin = new Thickness(16) };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var cancel = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 4, 14, 4) };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var ok = new Button { Content = "Use selected", Padding = new Thickness(14, 4, 14, 4) };
        ok.Click += (_, _) =>
        {
            SelectedRooms.AddRange(_labBoxes.Where(e => e.Box.IsChecked == true).Select(e => e.Room));
            DialogResult = true;
            Close();
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var host = new StackPanel();
        foreach (var areaGroup in labs
            .GroupBy(r => DominantArea(r) ?? "Other")
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var members = new List<CheckBox>();
            var areaBox = new CheckBox
            {
                Content = areaGroup.Key,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 10, 0, 2)
            };
            areaBox.Click += (_, _) =>
            {
                foreach (var member in members) member.IsChecked = areaBox.IsChecked == true;
            };
            host.Children.Add(areaBox);

            foreach (var room in areaGroup.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                var label = room.DisplayName is { Length: > 0 } fleet ? fleet : room.Number;
                var box = new CheckBox
                {
                    Content = $"{label}  ({room.Count})",
                    Margin = new Thickness(20, 2, 0, 2)
                };
                box.Click += (_, _) =>
                {
                    areaBox.IsChecked = members.All(m => m.IsChecked == true)
                        ? true
                        : members.Any(m => m.IsChecked == true) ? null : false;
                };
                members.Add(box);
                _labBoxes.Add((box, room));
                host.Children.Add(box);
            }
        }

        root.Children.Add(new ScrollViewer { Content = host, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }

    private static string? DominantArea(RosterRoom room) =>
        room.Computers
            .Select(c => c.Area)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .GroupBy(a => a)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.Key;
}
