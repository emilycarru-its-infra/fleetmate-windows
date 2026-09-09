using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FleetMate.Core.Config;
using FleetMate.Core.Models;
using FleetMate.Core.Models.Manage;
using FleetMate.Core.Services.Manage;
using Serilog;

namespace FleetMate.GUI.ViewModels.Manage;

/// <summary>
/// State of the Manage tab: the roster, the selected room or group, the
/// machine rows with their scan and probe results, and the custom groups.
/// Services are injected as interfaces so the whole flow runs in tests
/// against fakes. Updates from background work are marshalled through the
/// synchronization context captured at construction (the UI thread in the
/// app, inline in tests).
/// </summary>
public partial class ManageViewModel : ObservableObject
{
    private readonly ManageConfig _config;
    private readonly ManageStateStore _store;
    private readonly HostScanner _scanner;
    private readonly MachineProbeService? _prober;
    private readonly SynchronizationContext? _ui;

    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _probeCts;

    public FleetRoster Roster { get; private set; } = FleetRoster.Empty;
    public ObservableCollection<CustomGroup> CustomGroups { get; } = new();
    public ObservableCollection<MachineRowViewModel> Rows { get; } = new();
    public List<CommandHistoryEntry> History { get; private set; } = new();

    [ObservableProperty] private RosterRoom? _selectedRoom;
    [ObservableProperty] private CustomGroup? _selectedGroup;
    [ObservableProperty] private string _selectionTitle = "";
    [ObservableProperty] private string _selectionSubtitle = "";
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isProbing;
    [ObservableProperty] private string _scanStatus = "";
    [ObservableProperty] private ScanMode _scanMode = ScanMode.Unknown;
    [ObservableProperty] private ScanSummary? _lastScan;
    [ObservableProperty] private string _rosterStatus = "";
    [ObservableProperty] private bool _rosterLoaded;
    [ObservableProperty] private string _searchText = "";

    public ManageViewModel(
        ManageConfig config,
        ManageStateStore store,
        IDeviceDirectory? directory,
        IReachabilityProbe? probe,
        IRemoteRunner? runner)
    {
        _config = config;
        _store = store;
        _scanner = new HostScanner(directory, probe);
        _prober = runner == null ? null : new MachineProbeService(runner) { Concurrency = Math.Max(1, config.ProbeConcurrency) };
        _ui = SynchronizationContext.Current;
    }

    public bool CanProbe => _prober != null;
    public string ScanModeLabel => ScanMode.Label();
    public bool HasSelection => SelectedRoom != null || SelectedGroup != null;
    public IEnumerable<MachineRowViewModel> SelectedRows => Rows.Where(r => r.IsSelected);
    public int SelectedCount => Rows.Count(r => r.IsSelected);
    public int OnlineCount => Rows.Count(r => r.IsOnline);
    public IEnumerable<MachineRowViewModel> OnlineSelectedRows => Rows.Where(r => r.IsSelected && r.IsOnline);

    // ── Roster ───────────────────────────────────────────────────────────

    public void LoadRoster()
    {
        var loader = new RosterLoader { IncludeRetired = _config.IncludeRetired, IncludeProvisioning = _config.IncludeProvisioning };
        var path = ManageConfig.ExpandHome(_config.RosterPath);
        Roster = loader.Load(path);
        RosterLoaded = Roster.Source.Count > 0;
        RosterStatus = !RosterLoaded
            ? (string.IsNullOrWhiteSpace(_config.RosterPath) ? "No roster configured. Set the roster path in Settings." : $"Roster not found or empty: {path}")
            : $"{Roster.Source.Count} machines" + (Roster.RetiredCount > 0 ? $", {Roster.RetiredCount} retired hidden" : "");

        CustomGroups.Clear();
        foreach (var g in _store.LoadCustomGroups()) CustomGroups.Add(g);
        History = _store.LoadHistory();
        OnPropertyChanged(nameof(Roster));
    }

    /// <summary>Machines matching the search text, across every section, unique by serial.</summary>
    public List<RosterComputer> SearchResults(string text)
    {
        var q = (text ?? "").Trim();
        if (q.Length == 0) return new List<RosterComputer>();
        var seen = new HashSet<string>();
        var hits = new List<RosterComputer>();
        foreach (var c in Roster.Source)
        {
            if (!seen.Add(c.Serial)) continue;
            if (Matches(c, q)) hits.Add(c);
        }
        return hits;
    }

    public static bool Matches(RosterComputer c, string q) =>
        c.Hostname.Contains(q, StringComparison.OrdinalIgnoreCase)
        || c.Serial.Contains(q, StringComparison.OrdinalIgnoreCase)
        || c.Allocation.Contains(q, StringComparison.OrdinalIgnoreCase)
        || c.Username.Contains(q, StringComparison.OrdinalIgnoreCase)
        || c.Asset.Contains(q, StringComparison.OrdinalIgnoreCase)
        || c.Location.Contains(q, StringComparison.OrdinalIgnoreCase)
        || c.Fleet.Contains(q, StringComparison.OrdinalIgnoreCase)
        || c.Area.Contains(q, StringComparison.OrdinalIgnoreCase);

    public static bool RoomMatches(RosterRoom room, string q) =>
        room.Number.Contains(q, StringComparison.OrdinalIgnoreCase)
        || (room.DisplayName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
        || room.Computers.Any(c => Matches(c, q));

    // ── Selection of a room / group / search set ─────────────────────────

    public Task SelectRoomAsync(RosterRoom room)
    {
        SelectedRoom = room;
        SelectedGroup = null;
        SelectionTitle = room.Name;
        SelectionSubtitle = $"{room.Count} machines";
        ReplaceRows(room.Computers, selectAll: false);
        return ScanAsync(null);
    }

    public Task SelectGroupAsync(CustomGroup group)
    {
        SelectedGroup = group;
        SelectedRoom = null;
        SelectionTitle = group.Name;
        SelectionSubtitle = $"Custom group, {group.Devices.Count} machines";
        ReplaceRows(group.Devices.Select(d => d.Computer).ToList(), selectAll: false);
        var stored = group.Devices.Where(d => d.Ip.Length > 0).ToDictionary(d => d.Computer.Serial, d => d.Ip);
        return ScanAsync(stored);
    }

    public Task SelectSearchResultsAsync(IReadOnlyList<RosterComputer> computers, string label)
    {
        var unique = computers.GroupBy(c => c.Serial).Select(g => g.First()).ToList();
        var room = new RosterRoom { Number = string.IsNullOrWhiteSpace(label) ? "Search results" : $"Search: {label.Trim()}", Computers = unique };
        SelectedRoom = room;
        SelectedGroup = null;
        SelectionTitle = room.Number;
        SelectionSubtitle = $"{unique.Count} machines";
        ReplaceRows(unique, selectAll: true);
        return ScanAsync(null);
    }

    private void ReplaceRows(IReadOnlyList<RosterComputer> computers, bool selectAll)
    {
        CancelScan();
        CancelProbe();
        Rows.Clear();
        foreach (var c in computers)
        {
            var row = new MachineRowViewModel(c) { IsSelected = selectAll };
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(MachineRowViewModel.IsSelected) or nameof(MachineRowViewModel.Scan))
                    OnPropertyChanged(nameof(SelectedCount));
                if (e.PropertyName == nameof(MachineRowViewModel.Scan)) OnPropertyChanged(nameof(OnlineCount));
            };
            Rows.Add(row);
        }
        ScanMode = ScanMode.Unknown;
        LastScan = null;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(OnlineCount));
    }

    public void SelectAll() { foreach (var r in Rows) r.IsSelected = true; }
    public void SelectOnline() { foreach (var r in Rows) r.IsSelected = r.IsOnline; }
    public void SelectNone() { foreach (var r in Rows) r.IsSelected = false; }

    // ── Scanning ─────────────────────────────────────────────────────────

    public async Task ScanAsync(IReadOnlyDictionary<string, string>? storedAddresses)
    {
        CancelScan();
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        var computers = Rows.Select(r => r.Computer).ToList();
        if (computers.Count == 0) return;

        IsScanning = true;
        ScanStatus = "Scanning...";
        var progress = new Progress<string>(s => Post(() => ScanStatus = s));

        try
        {
            var (results, summary) = await _scanner.ScanAsync(computers, storedAddresses, progress, cts.Token);
            if (cts.IsCancellationRequested) return;

            Post(() =>
            {
                foreach (var row in Rows)
                {
                    if (results.TryGetValue(row.Serial, out var r)) row.Scan = r;
                }
                LastScan = summary;
                ScanMode = summary.Mode;
                ScanStatus = "";
                IsScanning = false;
                RememberGroupAddresses(results);
                OnPropertyChanged(nameof(OnlineCount));
                OnPropertyChanged(nameof(ScanModeLabel));
            });

            if (CanProbe) await ProbeAllAsync();
        }
        catch (OperationCanceledException)
        {
            Post(() => { IsScanning = false; ScanStatus = ""; });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Scan failed");
            Post(() => { IsScanning = false; ScanStatus = $"Scan failed: {ex.Message}"; ScanMode = ScanMode.Limited; });
        }
    }

    public void CancelScan()
    {
        _scanCts?.Cancel();
        _scanCts = null;
        IsScanning = false;
        ScanStatus = "";
    }

    /// <summary>Custom groups remember the last good address so they open online next time.</summary>
    private void RememberGroupAddresses(Dictionary<string, HostScanResult> results)
    {
        if (SelectedGroup == null) return;
        var changed = false;
        foreach (var device in SelectedGroup.Devices)
        {
            if (results.TryGetValue(device.Computer.Serial, out var r) && r.HasAddress && r.State == HostState.Online && device.Ip != r.Ip)
            {
                device.Ip = r.Ip;
                changed = true;
            }
        }
        if (changed) _store.SaveCustomGroups(CustomGroups);
    }

    public async Task RescanHostAsync(MachineRowViewModel row)
    {
        if (row.IsRescanning) return;
        row.IsRescanning = true;
        try
        {
            var known = row.Scan.Source == AddressSource.Stored ? row.Scan.Ip : null;
            var result = await _scanner.RescanAsync(row.Computer, known, CancellationToken.None);
            Post(() =>
            {
                row.Scan = result.HasAddress ? result : (row.Scan.HasAddress && row.Scan.Source == AddressSource.ReportMate ? row.Scan : result);
                row.IsRescanning = false;
                OnPropertyChanged(nameof(OnlineCount));
            });
            if (row.IsOnline && row.Probe == null && CanProbe) await ProbeAsync(new[] { row });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Rescan of {Host} failed", row.Name);
            Post(() => row.IsRescanning = false);
        }
    }

    // ── Probing ──────────────────────────────────────────────────────────

    public Task ProbeAllAsync() => ProbeAsync(Rows.Where(r => r.IsOnline && r.SshOpen).ToList());

    public async Task ProbeAsync(IReadOnlyList<MachineRowViewModel> rows)
    {
        if (_prober == null || rows.Count == 0) return;
        CancelProbe();
        var cts = new CancellationTokenSource();
        _probeCts = cts;
        IsProbing = true;
        foreach (var r in rows) r.IsProbing = true;

        try
        {
            await _prober.ProbeManyAsync(
                rows.Select(r => (r.Computer, r.Ip)),
                outcome => Post(() =>
                {
                    var row = Rows.FirstOrDefault(r => r.Serial == outcome.Serial);
                    if (row == null) return;
                    row.IsProbing = false;
                    row.ProbeOutcome = outcome.Outcome;
                    row.ProbeError = outcome.Error;
                    if (outcome.Probe != null) row.Probe = outcome.Probe;
                }),
                cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "Probe run failed");
        }
        finally
        {
            Post(() =>
            {
                foreach (var r in rows) r.IsProbing = false;
                IsProbing = false;
            });
        }
    }

    public void CancelProbe()
    {
        _probeCts?.Cancel();
        _probeCts = null;
        IsProbing = false;
    }

    // ── Custom groups ────────────────────────────────────────────────────

    public CustomGroup CreateGroup(string name)
    {
        var group = new CustomGroup(name);
        CustomGroups.Add(group);
        _store.SaveCustomGroups(CustomGroups);
        return group;
    }

    public void RenameGroup(CustomGroup group, string newName)
    {
        group.Name = newName.Trim();
        _store.SaveCustomGroups(CustomGroups);
        var idx = CustomGroups.IndexOf(group);
        if (idx >= 0) { CustomGroups.RemoveAt(idx); CustomGroups.Insert(idx, group); }
        if (SelectedGroup == group) SelectionTitle = group.Name;
    }

    public void DeleteGroup(CustomGroup group)
    {
        CustomGroups.Remove(group);
        _store.SaveCustomGroups(CustomGroups);
        if (SelectedGroup == group)
        {
            SelectedGroup = null;
            SelectionTitle = "";
            SelectionSubtitle = "";
            ReplaceRows(Array.Empty<RosterComputer>(), false);
        }
    }

    /// <summary>Add a device; duplicates by hostname or serial are ignored. Returns true when added.</summary>
    public bool AddDevice(CustomGroup group, string hostname, string ip, string serial = "")
    {
        var device = new AdhocDevice(hostname, ip, serial);
        if (device.Hostname.Length == 0) return false;
        if (group.Devices.Any(d =>
                (serial.Length > 0 && d.Serial.Equals(serial, StringComparison.OrdinalIgnoreCase)) ||
                d.Hostname.Equals(device.Hostname, StringComparison.OrdinalIgnoreCase)))
            return false;
        group.Devices.Add(device);
        _store.SaveCustomGroups(CustomGroups);
        if (SelectedGroup == group)
        {
            var row = new MachineRowViewModel(device.Computer);
            if (device.Ip.Length > 0)
                row.Scan = new HostScanResult { Serial = device.Computer.Serial, Ip = device.Ip, Source = AddressSource.Stored };
            Rows.Add(row);
            SelectionSubtitle = $"Custom group, {group.Devices.Count} machines";
        }
        return true;
    }

    public int AddRosterDevices(CustomGroup group, IEnumerable<RosterComputer> computers)
    {
        var added = 0;
        foreach (var c in computers)
        {
            if (AddDevice(group, c.HasHostname ? c.Hostname : c.DisplayName, "", c.Serial)) added++;
        }
        return added;
    }

    public void RemoveDevice(CustomGroup group, AdhocDevice device)
    {
        group.Devices.Remove(device);
        _store.SaveCustomGroups(CustomGroups);
        if (SelectedGroup == group)
        {
            var row = Rows.FirstOrDefault(r => r.Serial == device.Computer.Serial);
            if (row != null) Rows.Remove(row);
            SelectionSubtitle = $"Custom group, {group.Devices.Count} machines";
        }
    }

    /// <summary>Parse pasted or imported lines of "hostname[,ip]" or bare IPs into devices.</summary>
    public static List<(string hostname, string ip)> ParseDeviceLines(string text)
    {
        var result = new List<(string, string)>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split(new[] { ',', '\t', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) continue;
            if (parts.Length == 1)
            {
                var single = parts[0];
                if (System.Net.IPAddress.TryParse(single, out _)) result.Add(("", single));
                else result.Add((single, ""));
            }
            else
            {
                var ipPart = parts.FirstOrDefault(p => System.Net.IPAddress.TryParse(p, out _)) ?? "";
                var namePart = parts.First(p => p != ipPart);
                result.Add((namePart, ipPart));
            }
        }
        return result;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Run on the UI thread and wait for it. Send rather than Post: WPF hands
    /// out a fresh DispatcherSynchronizationContext per continuation, so a
    /// reference comparison cannot tell "already on the UI thread", and a
    /// posted update would land after the code that expects to read it.
    /// Send on the dispatcher's own thread executes inline, so it never deadlocks.
    /// </summary>
    private void Post(Action action)
    {
        if (_ui == null) action();
        else _ui.Send(_ => action(), null);
    }

    partial void OnScanModeChanged(ScanMode value) => OnPropertyChanged(nameof(ScanModeLabel));
    partial void OnSelectedRoomChanged(RosterRoom? value) => OnPropertyChanged(nameof(HasSelection));
    partial void OnSelectedGroupChanged(CustomGroup? value) => OnPropertyChanged(nameof(HasSelection));
}
