using FleetMate.Core.Config;
using FleetMate.Core.Manage;
using FleetMate.Core.Models;
using FleetMate.Core.Models.Manage;
using FleetMate.Core.Services.Manage;
using Xunit;

namespace FleetMate.Tests;

/// <summary>The Manage tab's shared wording and decisions, which the WPF and WinUI apps both draw.</summary>
public class ManageSharedTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fm-manage-shared-" + Guid.NewGuid().ToString("N"));

    private const string Roster =
        "serial,catalog,area,location,asset,usage,status,allocation,username,platform,fleet,hostname\n" +
        "S1,Curriculum,Studio,R101,A1,Shared,Active,Studio 01,,Windows,Studio Lab,LAB-01\n" +
        "S2,Curriculum,Studio,R101,A2,Shared,Active,Studio 02,,Windows,Studio Lab,LAB-02\n" +
        "S3,Curriculum,Print,R102,A3,Shared,Active,Print 01,,Windows,Print Lab,PRN-01\n" +
        "S4,Staff,IT,R301,A4,Assigned,Active,Pat Lee,plee,Windows,,PLEE\n";

    public ManageSharedTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class NoProbe : IReachabilityProbe
    {
        public Task<string?> ResolveAsync(string hostname, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<bool> IsTcpOpenAsync(string ip, int port, CancellationToken ct) => Task.FromResult(false);
    }

    private ManageViewModel Build()
    {
        var path = Path.Combine(_root, "computers.csv");
        File.WriteAllText(path, Roster);
        var vm = new ManageViewModel(new ManageConfig { RosterPath = path, CommandsPath = Path.Combine(_root, "commands.yaml") },
            new ManageStateStore(Path.Combine(_root, "state")), null, new NoProbe(), null);
        vm.LoadRoster();
        return vm;
    }

    [Fact]
    public void SidebarRoom_TitlesLabsByFleetAndCountsMachines()
    {
        var vm = Build();
        var row = SidebarRoom.From(vm.Roster.Labs.First(r => r.Computers.Any(c => c.Fleet == "Studio Lab")), RosterSection.Labs);
        Assert.Equal("Studio Lab", row.Title);
        Assert.Equal("Studio · R101 · 2 machines", row.Subtitle);
        Assert.Equal("", row.Glyph);
    }

    [Fact]
    public void SidebarOrder_KeepsSavedOrderThenAppendsNewRows()
    {
        var rows = new[] { "A", "B", "C" }.Select(t => new SidebarRoom { Title = t }).ToList();
        var ordered = SidebarOrder.Apply(new[] { "C", "Gone", "A" }, rows);
        Assert.Equal(new[] { "C", "A", "B" }, ordered.Select(r => r.Title));
        Assert.Equal(new[] { "A", "B", "C" }, SidebarOrder.Apply(null, rows).Select(r => r.Title));
    }

    [Fact]
    public void SidebarOrder_MoveTakesTheTargetsSlot()
    {
        var rows = new[] { "A", "B", "C" }.Select(t => new SidebarRoom { Title = t }).ToList();
        Assert.Equal(new[] { "B", "C", "A" }, SidebarOrder.Move(rows, rows[0], rows[2])!.Select(r => r.Title));
        Assert.Equal(new[] { "C", "A", "B" }, SidebarOrder.Move(rows, rows[2], rows[0])!.Select(r => r.Title));
        Assert.Null(SidebarOrder.Move(rows, rows[1], rows[1]));
    }

    [Fact]
    public void ManageText_WordsCountsAndFooter()
    {
        Assert.Equal("1 machine", ManageText.Machines(1));
        Assert.Equal("3 machines", ManageText.Machines(3));
        Assert.Equal("4 online", ManageText.SelectionCount(0, 4));
        Assert.Equal("2 selected · 4 online", ManageText.SelectionCount(2, 4));
        Assert.Equal("Results", ManageText.ResultsTitle(""));
        Assert.Equal("Results: Restart", ManageText.ResultsTitle("Restart"));
        Assert.Equal("Caution (inferred)", ManageText.TrustLabel(CommandTrustLevel.Caution, true));
        var vm = Build();
        Assert.Equal("2 labs · 4 machines · demo", ManageText.RosterFooter(vm, "demo"));
        Assert.Contains("SSH key", ManageText.EmptyHint(vm, sshConfigured: false));
    }

    [Fact]
    public void ManageText_FiltersResultsAndPicksFailedSerials()
    {
        var c1 = new RosterComputer { Serial = "S1", Hostname = "A" };
        var c2 = new RosterComputer { Serial = "S2", Hostname = "B" };
        var c3 = new RosterComputer { Serial = "S3", Hostname = "C" };
        var results = new[]
        {
            new CommandResultViewModel(c1, "192.0.2.1") { Status = CommandRunStatus.Success },
            new CommandResultViewModel(c2, "192.0.2.2") { Status = CommandRunStatus.Timeout },
            new CommandResultViewModel(c3, "192.0.2.3") { Status = CommandRunStatus.Offline },
        };
        Assert.Single(ManageText.Filter(results, ResultFilter.Success));
        Assert.Equal("S2", ManageText.Filter(results, ResultFilter.Failed).Single().Serial);
        Assert.Equal(3, ManageText.Filter(results, ResultFilter.All).Count());
        Assert.Equal(new HashSet<string> { "S2", "S3" }, ManageText.FailedSerials(results));
    }

    [Fact]
    public async Task ManageText_TargetRowsPreferCheckedThenHighlighted()
    {
        var vm = Build();
        await vm.SelectRoomAsync(vm.Roster.Labs.First(r => r.Count == 2));
        Assert.Equal(2, ManageText.TargetRows(vm, Array.Empty<MachineRowViewModel>()).Count);
        Assert.Single(ManageText.TargetRows(vm, vm.Rows.Take(1)));
        vm.Rows[1].IsSelected = true;
        Assert.Same(vm.Rows[1], ManageText.TargetRows(vm, vm.Rows.Take(1)).Single());
    }

    [Fact]
    public void ExtractDeviceLines_ReadsRosterShapedCsvAndPassesOthersThrough()
    {
        Assert.Equal("LAB-01" + Environment.NewLine + "S9",
            ManageText.ExtractDeviceLines("serial,hostname\nS1,LAB-01\nS9,\n"));
        Assert.Equal("LAB-01\nLAB-02", ManageText.ExtractDeviceLines("LAB-01\nLAB-02"));
    }

    [Fact]
    public void ImportDevices_MatchesTheRosterAndSkipsDuplicates()
    {
        var vm = Build();
        var group = vm.CreateGroup("Spare");
        var (added, skipped) = ManageText.ImportDevices(vm, group, "lab-01\nS3\n192.0.2.40\nLAB-01\n");
        Assert.Equal(3, added);
        Assert.Equal(1, skipped);
        Assert.Contains(group.Devices, d => d.Serial == "S1" && d.Hostname == "LAB-01");
        Assert.Equal("Added 3 to \"Spare\", 1 already there or invalid", ManageText.Added(added, skipped, group.Name));
    }

    [Fact]
    public void MachineDetail_ListsOnlyWhatIsKnown()
    {
        var row = new MachineRowViewModel(new RosterComputer { Serial = "S1", Hostname = "LAB-01", Asset = "A1", Status = "Active" });
        var unscanned = MachineDetail.Sections(row, scanned: false);
        Assert.Equal(new[] { "Roster", "Network" }, unscanned.Select(s => s.Title));
        Assert.Equal("Not scanned yet", unscanned[1].Text);
        Assert.DoesNotContain(unscanned[0].Fields, f => f.Label == "Location");

        row.Scan = new HostScanResult { Serial = "S1", Ip = "192.0.2.5", SshOpen = true, Source = AddressSource.Dns };
        row.Probe = MachineProbe.Parse("LAB-01", "192.0.2.5", "user=\nos=Windows 11\nrdp=enabled\nrdp_port=listening\nssh_port=listening\napps=explorer,msedge\n");
        var sections = MachineDetail.Sections(row, scanned: true);
        Assert.Equal(new[] { "Roster", "Network", "Session", "Remote access", "Running applications" }, sections.Select(s => s.Title));
        Assert.Equal("Sign-in screen", sections[2].Fields.Single(f => f.Label == "Console user").Value);
        Assert.Equal("explorer, msedge", sections[4].Text);
    }

    [Fact]
    public void MachineSort_OrdersAddressesNumerically()
    {
        MachineRowViewModel Row(string serial, string ip) =>
            new(new RosterComputer { Serial = serial, Hostname = serial }) { Scan = new HostScanResult { Serial = serial, Ip = ip } };
        var rows = new[] { Row("A", "192.0.2.10"), Row("B", "192.0.2.9"), Row("C", "192.0.2.100") };
        Assert.Equal(new[] { "B", "A", "C" }, MachineSort.Apply(rows, MachineColumn.Address, true).Select(r => r.Serial));
        Assert.Equal(new[] { "C", "A", "B" }, MachineSort.Apply(rows, MachineColumn.Address, false).Select(r => r.Serial));
        Assert.Same(rows, MachineSort.Apply(rows, null, true));
    }

    /// <summary>WinUI's dispatcher context has no Send; updates from a worker must still land.</summary>
    [Fact]
    public async Task ViewModel_UpdatesFromAWorkerUnderAPostOnlyContext()
    {
        var context = new PostOnlyContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        ManageViewModel vm;
        try { vm = Build(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }

        await vm.SelectRoomAsync(vm.Roster.Labs[0]);
        context.Drain();
        Assert.NotNull(vm.LastScan);
        vm.CancelScan();

        await Task.Run(() => vm.ScanAsync(null));
        context.Drain();
        Assert.False(vm.IsScanning);
        Assert.NotNull(vm.LastScan);
    }

    private sealed class PostOnlyContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback, object?)> _queue = new();
        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();
        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));
        public void Drain() { while (_queue.TryDequeue(out var item)) item.Item1(item.Item2); }
    }
}
