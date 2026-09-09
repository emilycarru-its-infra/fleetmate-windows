using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
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
        };
        _vm.CustomGroups.CollectionChanged += (_, _) => NoGroupsText.Visibility = _vm.CustomGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        Loaded += (_, _) =>
        {
            if (!_rosterLoaded)
            {
                _rosterLoaded = true;
                ReloadRoster();
            }
            Focus();
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
        return new ManageViewModel(config, store, directory, new NetworkReachabilityProbe(), runner);
    }

    public void ReloadRoster()
    {
        _vm.LoadRoster();
        _suppressSidebarSelection = true;
        LabsList.ItemsSource = _vm.Roster.Labs;
        KiosksList.ItemsSource = _vm.Roster.Kiosks;
        StaffList.ItemsSource = _vm.Roster.Staff;
        FacultyList.ItemsSource = _vm.Roster.Faculty;
        _suppressSidebarSelection = false;
        NoGroupsText.Visibility = _vm.CustomGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var app = Application.Current as App;
        if (!_vm.RosterLoaded)
            EmptyHint.Text = _vm.RosterStatus;
        else if (app?.SecureShellService == null)
            EmptyHint.Text = "Rooms load from the roster. Machine details need the SSH key configured in Settings (Manage).";
        else
            EmptyHint.Text = "Rooms come from the roster configured in Settings.";
        UpdateScanBadge();
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
        if (_suppressSidebarSelection || sender is not ListBox list || list.SelectedItem is not RosterRoom room) return;
        ClearOtherSidebarSelections(list);
        HideDetail();
        await _vm.SelectRoomAsync(room);
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
        if (sender is not Button button) return;
        var (list, toggle) = button.Tag?.ToString() switch
        {
            "Labs" => (LabsList, LabsToggle),
            "Kiosks" => (KiosksList, KiosksToggle),
            "Staff" => (StaffList, StaffToggle),
            "Faculty" => (FacultyList, FacultyToggle),
            _ => (null, null)
        };
        if (list == null || toggle == null) return;
        var collapsed = list.Visibility == Visibility.Collapsed;
        list.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        toggle.Content = collapsed ? "–" : "+";
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
        "online" => Brushes.MediumSeaGreen,
        "unreachable" => Brushes.Goldenrod,
        "scanning" => Brushes.DodgerBlue,
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
