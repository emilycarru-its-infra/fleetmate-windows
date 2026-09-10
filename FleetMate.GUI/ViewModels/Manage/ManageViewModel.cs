using System.IO;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FleetMate.Core.Config;
using FleetMate.Core.Models;
using FleetMate.Core.Models.Manage;
using FleetMate.Core.Services.Manage;
using Serilog;

namespace FleetMate.GUI.ViewModels.Manage;

internal static class ObjectExtensions
{
    public static void Let<T>(this T value, Action<T> action) where T : class => action(value);
}

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
    private readonly RemoteSessionLauncher? _launcher;
    private readonly CommandRunner? _commandRunner;
    private readonly SynchronizationContext? _ui;

    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _probeCts;
    private CancellationTokenSource? _runCts;

    public FleetRoster Roster { get; private set; } = FleetRoster.Empty;
    public ObservableCollection<CustomGroup> CustomGroups { get; } = new();
    public ObservableCollection<MachineRowViewModel> Rows { get; } = new();
    public List<CommandHistoryEntry> History { get; private set; } = new();
    public ObservableCollection<CommandCategory> Categories { get; } = new();
    public ObservableCollection<CommandResultViewModel> Results { get; } = new();

    [ObservableProperty] private CommandCategory? _selectedCategory;
    [ObservableProperty] private ManagedCommand? _selectedCommand;
    [ObservableProperty] private string _customCommand = "";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _runStatus = "";
    [ObservableProperty] private string _lastRunLabel = "";

    /// <summary>Raised on the UI thread when a fleet run finishes, with success, failed and offline counts.</summary>
    public event Action<int, int, int>? RunCompleted;

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
        IRemoteRunner? runner,
        RemoteSessionLauncher? launcher = null)
    {
        _config = config;
        _store = store;
        _launcher = launcher;
        _scanner = new HostScanner(directory, probe);
        _prober = runner == null ? null : new MachineProbeService(runner) { Concurrency = Math.Max(1, config.ProbeConcurrency) };
        _commandRunner = runner == null ? null : new CommandRunner(runner) { Concurrency = Math.Max(1, config.ProbeConcurrency) };
        _ui = SynchronizationContext.Current;
    }

    public bool CanRun => _commandRunner != null;

    public bool CanProbe => _prober != null;
    public bool CanLaunch => _launcher != null;
    public string ScanModeLabel => ScanMode.Label();
    public bool HasSelection => SelectedRoom != null || SelectedGroup != null;
    public IEnumerable<MachineRowViewModel> SelectedRows => Rows.Where(r => r.IsSelected);
    public int SelectedCount => Rows.Count(r => r.IsSelected);
    public int OnlineCount => Rows.Count(r => r.IsOnline);
    public IEnumerable<MachineRowViewModel> OnlineSelectedRows => Rows.Where(r => r.IsSelected && r.IsOnline);

    // ── Command library ──────────────────────────────────────────────────

    public string CommandsPath => _config.ResolvedCommandsPath;

    /// <summary>
    /// Load the library, seeding the per-user file from the built-in set when
    /// it does not exist yet, and adding any built-in command the file lacks.
    /// </summary>
    public void LoadCommandLibrary()
    {
        var path = CommandsPath;
        List<CommandCategory> categories;
        var bundled = CommandLibrary.LoadBundled();
        if (File.Exists(path))
        {
            categories = CommandLibrary.Load(path);
            if (CommandLibrary.MergeMissing(categories, bundled))
                TrySaveLibrary(categories);
        }
        else
        {
            categories = bundled;
            TrySaveLibrary(categories);
        }

        var previousCategory = SelectedCategory?.Name;
        var previousCommand = SelectedCommand?.Label;
        Categories.Clear();
        foreach (var c in categories) Categories.Add(c);
        SelectedCategory = Categories.FirstOrDefault(c => c.Name == previousCategory) ?? Categories.FirstOrDefault();
        SelectedCommand = SelectedCategory?.Commands.FirstOrDefault(c => c.Label == previousCommand);
    }

    private void TrySaveLibrary(IEnumerable<CommandCategory> categories)
    {
        try { CommandLibrary.Save(categories, CommandsPath); }
        catch (Exception ex) { Log.Warning(ex, "Could not save the command library to {Path}", CommandsPath); }
    }

    public void SaveLibrary() => TrySaveLibrary(Categories);

    public CommandCategory AddCategory(string name)
    {
        var category = new CommandCategory(name.Trim());
        Categories.Add(category);
        SaveLibrary();
        return category;
    }

    public ManagedCommand AddCommand(CommandCategory category, string label, string command, CommandTrustLevel trust)
    {
        var cmd = new ManagedCommand(label, command, trust);
        category.Commands.Add(cmd);
        SaveLibrary();
        SelectedCategory = category;
        SelectedCommand = cmd;
        return cmd;
    }

    public void EditCommand(CommandCategory category, ManagedCommand command, string label, string text, CommandTrustLevel trust)
    {
        command.Label = label;
        command.Command = text;
        command.TrustLevel = trust;
        command.TrustWasExplicit = true;
        SaveLibrary();
        var idx = category.Commands.IndexOf(command);
        SelectedCategory = category;
        SelectedCommand = null;
        SelectedCommand = command;
    }

    public void DeleteCommand(CommandCategory category, ManagedCommand command)
    {
        category.Commands.Remove(command);
        if (SelectedCommand == command) SelectedCommand = null;
        SaveLibrary();
    }

    /// <summary>The command that Run would send: the custom box wins when it has text.</summary>
    public string ResolvedCommandString => CustomCommand.Trim().Length > 0 ? CustomCommand.Trim() : SelectedCommand?.Command ?? "";

    public string ResolvedCommandLabel => CustomCommand.Trim().Length > 0 ? "Custom command" : SelectedCommand?.Label ?? "";

    /// <summary>Trust for what Run would send: stated for library commands, inferred for custom text.</summary>
    public CommandTrustLevel EffectiveTrust =>
        CustomCommand.Trim().Length > 0 ? TrustInference.Infer(CustomCommand) : SelectedCommand?.TrustLevel ?? CommandTrustLevel.Safe;

    public bool EffectiveTrustIsInferred => CustomCommand.Trim().Length > 0 || (SelectedCommand != null && !SelectedCommand.TrustWasExplicit);

    partial void OnCustomCommandChanged(string value) => RaiseCommandDerived();
    partial void OnSelectedCommandChanged(ManagedCommand? value) => RaiseCommandDerived();

    private void RaiseCommandDerived()
    {
        OnPropertyChanged(nameof(ResolvedCommandString));
        OnPropertyChanged(nameof(ResolvedCommandLabel));
        OnPropertyChanged(nameof(EffectiveTrust));
        OnPropertyChanged(nameof(EffectiveTrustIsInferred));
    }

    // ── Running ──────────────────────────────────────────────────────────

    public int ResultSuccessCount => Results.Count(r => r.Status == CommandRunStatus.Success);
    public int ResultFailedCount => Results.Count(r => r.Status is CommandRunStatus.Failed or CommandRunStatus.AuthFailed or CommandRunStatus.Timeout);
    public int ResultOfflineCount => Results.Count(r => r.Status == CommandRunStatus.Offline);

    /// <summary>
    /// Run a command on the checked online machines (or the given rows).
    /// Results replace the previous run's; each row streams as it arrives.
    /// </summary>
    public async Task RunCommandAsync(string command, string label, bool recordHistory = true, IReadOnlyList<MachineRowViewModel>? targets = null)
    {
        if (_commandRunner == null || string.IsNullOrWhiteSpace(command)) return;
        var rows = (targets ?? OnlineSelectedRows.ToList()).Where(r => r.IsOnline).ToList();
        if (rows.Count == 0) return;

        KillRun();
        var cts = new CancellationTokenSource();
        _runCts = cts;

        if (recordHistory) History = _store.AddHistory(History, label, command);
        LastRunLabel = label;

        Results.Clear();
        var bySerial = new Dictionary<string, CommandResultViewModel>();
        foreach (var row in rows)
        {
            var result = new CommandResultViewModel(row.Computer, row.Ip);
            bySerial[row.Serial] = result;
            Results.Add(result);
            row.LastRunStatus = CommandRunStatus.Pending;
        }
        IsRunning = true;
        RunStatus = $"Running on {rows.Count} machines...";

        var observer = new RunObserver(this, bySerial);
        try
        {
            await _commandRunner.RunAsync(rows.Select(r => new RunTarget(r.Computer, r.Ip)).ToList(), command, observer, cts.Token);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Fleet run failed");
        }
        finally
        {
            Post(() =>
            {
                if (_runCts == cts) { IsRunning = false; _runCts = null; }
                foreach (var r in Results.Where(r => !r.IsTerminal))
                {
                    r.Status = CommandRunStatus.Cancelled;
                    r.EndTime = DateTime.Now;
                }
                RaiseResultCounts();
                RunStatus = $"{ResultSuccessCount} succeeded, {ResultFailedCount} failed, {ResultOfflineCount} offline";
                RunCompleted?.Invoke(ResultSuccessCount, ResultFailedCount, ResultOfflineCount);
            });
        }
    }

    public void KillRun()
    {
        var cts = _runCts;
        _runCts = null;
        cts?.Cancel();
        foreach (var r in Results.Where(r => !r.IsTerminal))
        {
            r.Status = CommandRunStatus.Cancelled;
            r.EndTime = DateTime.Now;
        }
        IsRunning = false;
    }

    public void ClearResults()
    {
        Results.Clear();
        RunStatus = "";
        RaiseResultCounts();
    }

    private void RaiseResultCounts()
    {
        OnPropertyChanged(nameof(ResultSuccessCount));
        OnPropertyChanged(nameof(ResultFailedCount));
        OnPropertyChanged(nameof(ResultOfflineCount));
    }

    private sealed class RunObserver : IRunObserver
    {
        private readonly ManageViewModel _vm;
        private readonly Dictionary<string, CommandResultViewModel> _results;

        public RunObserver(ManageViewModel vm, Dictionary<string, CommandResultViewModel> results)
        {
            _vm = vm;
            _results = results;
        }

        public void Started(string serial) => _vm.Post(() =>
        {
            if (!_results.TryGetValue(serial, out var r)) return;
            r.Status = CommandRunStatus.Running;
            Row(serial)?.Let(row => row.LastRunStatus = CommandRunStatus.Running);
        });

        public void Output(string serial, string chunk) => _vm.Post(() =>
        {
            if (_results.TryGetValue(serial, out var r) && r.Status != CommandRunStatus.Cancelled) r.AppendOutput(chunk);
        });

        public void Finished(string serial, CommandRunStatus status, int? exitCode, string stderr, string? error) => _vm.Post(() =>
        {
            if (!_results.TryGetValue(serial, out var r)) return;
            if (r.Status == CommandRunStatus.Cancelled && status != CommandRunStatus.Cancelled) return;
            r.ExitCode = exitCode;
            r.ErrorOutput = string.IsNullOrWhiteSpace(stderr) ? (error ?? "") : (error == null ? stderr : stderr + Environment.NewLine + error);
            r.EndTime = DateTime.Now;
            r.Status = status;
            Row(serial)?.Let(row => row.LastRunStatus = status);
            _vm.RaiseResultCounts();
            _vm.RunStatus = $"{_vm.Results.Count(x => x.IsTerminal)}/{_vm.Results.Count} done";
        });

        private MachineRowViewModel? Row(string serial) => _vm.Rows.FirstOrDefault(x => x.Serial == serial);
    }

    // ── History ──────────────────────────────────────────────────────────

    public void ClearHistory()
    {
        History = new List<CommandHistoryEntry>();
        _store.ClearHistory();
    }

    // ── Roster ───────────────────────────────────────────────────────────

    /// <summary>
    /// When set, the roster loads from here instead of the configured path —
    /// the page points this at the cached repository fetch, keeping the
    /// configured RosterPath as the explicit local override/fallback.
    /// </summary>
    public string? RosterPathOverride { get; set; }

    public void LoadRoster()
    {
        LoadCommandLibrary();
        var loader = new RosterLoader { IncludeRetired = _config.IncludeRetired, IncludeProvisioning = _config.IncludeProvisioning };
        var path = RosterPathOverride ?? ManageConfig.ExpandHome(_config.RosterPath);
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
        KillRun();
        Results.Clear();
        RunStatus = "";
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

    // ── Sessions ─────────────────────────────────────────────────────────

    /// <summary>Tab title: friendly name, with the hostname when it differs.</summary>
    public static string SessionTitle(MachineRowViewModel row) =>
        row.Computer.HasHostname && row.Computer.Hostname != row.FriendlyName
            ? $"{row.FriendlyName} ({row.Computer.Hostname})"
            : row.FriendlyName;

    public void OpenSsh(MachineRowViewModel row)
    {
        if (_launcher == null || !row.HasAddress) return;
        _launcher.OpenSsh(row.Ip, SessionTitle(row));
    }

    public void OpenRdp(MachineRowViewModel row)
    {
        if (_launcher == null || !row.HasAddress) return;
        _launcher.OpenRdp(row.Ip);
    }

    public void OpenSshAndRdp(MachineRowViewModel row)
    {
        if (_launcher == null || !row.HasAddress) return;
        _launcher.OpenSshAndRdp(row.Ip, SessionTitle(row));
    }

    public void OpenSshTabs(IReadOnlyList<SshSession> sessions)
    {
        if (_launcher == null) return;
        _launcher.OpenSshTabs(sessions);
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
    partial void OnSelectedCategoryChanged(CommandCategory? value)
    {
        if (value != null && SelectedCommand != null && !value.Commands.Contains(SelectedCommand)) SelectedCommand = null;
    }
    partial void OnSelectedRoomChanged(RosterRoom? value) => OnPropertyChanged(nameof(HasSelection));
    partial void OnSelectedGroupChanged(CustomGroup? value) => OnPropertyChanged(nameof(HasSelection));
}
