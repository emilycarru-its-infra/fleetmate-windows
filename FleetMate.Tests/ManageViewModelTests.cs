using FleetMate.Core.Config;
using FleetMate.Core.Models;
using FleetMate.Core.Models.Manage;
using FleetMate.Core.Services.Manage;
using FleetMate.GUI.ViewModels.Manage;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The Manage tab's state machine, driven without WPF: roster sections,
/// room selection with a scan, machine rows, selection helpers, probes and
/// custom groups. Fakes replace inventory, the network and SSH.
/// </summary>
public class ManageViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fleetmate-vm-" + Guid.NewGuid().ToString("N"));
    private readonly string _rosterPath;

    private const string Roster =
        "serial,catalog,area,location,asset,usage,status,allocation,username,platform,fleet,hostname\n" +
        "S1,Curriculum,Studio,R101,A1,Shared,Active,Studio 01,,Windows,Studio Lab,LAB-01\n" +
        "S2,Curriculum,Studio,R101,A2,Shared,Active,Studio 02,,Windows,Studio Lab,LAB-02\n" +
        "S3,Staff,IT,R301,A3,Assigned,Active,Pat Lee,plee,Windows,,PLEE\n";

    public ManageViewModelTests()
    {
        Directory.CreateDirectory(_root);
        _rosterPath = Path.Combine(_root, "computers.csv");
        File.WriteAllText(_rosterPath, Roster);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeProbe : IReachabilityProbe
    {
        public Dictionary<string, string> Dns { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Open { get; } = new();
        public Task<string?> ResolveAsync(string hostname, CancellationToken ct) => Task.FromResult(Dns.TryGetValue(hostname, out var ip) ? ip : null);
        public Task<bool> IsTcpOpenAsync(string ip, int port, CancellationToken ct) => Task.FromResult(Open.Contains(ip));
    }

    private sealed class FakeRunner : IRemoteRunner
    {
        public Dictionary<string, string> Output { get; } = new();
        public HashSet<string> RejectKey { get; } = new();
        public int Calls;

        public Task<SecureShellResult> RunAsync(string ip, string command, Action<string>? onChunk, CancellationToken ct, string? username = null, string? deviceName = null)
        {
            Interlocked.Increment(ref Calls);
            if (RejectKey.Contains(ip))
                return Task.FromResult(new SecureShellResult { Host = ip, Outcome = SecureShellOutcome.AuthFailed, Error = new Exception("Permission denied (publickey)") });
            var stdout = Output.TryGetValue(ip, out var o) ? o : "host=X\nos=Windows 11\nuser=EXAMPLE\\someone\nrdp=enabled\nrdp_port=listening\nssh_port=listening\n";
            onChunk?.Invoke(stdout);
            return Task.FromResult(new SecureShellResult { Host = ip, Connected = true, ExitCode = 0, Stdout = stdout, Outcome = SecureShellOutcome.Success });
        }
    }

    private (ManageViewModel vm, FakeProbe probe, FakeRunner runner) Build(bool withSsh = true)
    {
        var config = new ManageConfig { RosterPath = _rosterPath };
        var store = new ManageStateStore(Path.Combine(_root, "state"));
        var probe = new FakeProbe();
        var runner = new FakeRunner();
        var vm = new ManageViewModel(config, store, null, probe, withSsh ? runner : null);
        vm.LoadRoster();
        return (vm, probe, runner);
    }

    [Fact]
    public void LoadRoster_PopulatesSectionsAndStatus()
    {
        var (vm, _, _) = Build();
        Assert.True(vm.RosterLoaded);
        Assert.Single(vm.Roster.Labs);
        Assert.Single(vm.Roster.Staff);
        Assert.Contains("3 machines", vm.RosterStatus);
    }

    [Fact]
    public void LoadRoster_MissingFileExplainsItself()
    {
        var vm = new ManageViewModel(new ManageConfig { RosterPath = Path.Combine(_root, "nope.csv") }, new ManageStateStore(Path.Combine(_root, "s")), null, new FakeProbe(), null);
        vm.LoadRoster();
        Assert.False(vm.RosterLoaded);
        Assert.Contains("not found", vm.RosterStatus);
    }

    [Fact]
    public async Task SelectRoom_ScansAndProbesOnlineMachines()
    {
        var (vm, probe, runner) = Build();
        probe.Dns["LAB-01"] = "10.0.0.1";
        probe.Dns["LAB-02"] = "10.0.0.2";
        probe.Open.Add("10.0.0.1");

        await vm.SelectRoomAsync(vm.Roster.Labs[0]);

        Assert.Equal("R101 · Studio Lab", vm.SelectionTitle);
        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal(ScanMode.DnsOnly, vm.ScanMode);
        Assert.Equal(1, vm.OnlineCount);

        var online = vm.Rows.Single(r => r.Serial == "S1");
        Assert.True(online.IsOnline);
        Assert.NotNull(online.Probe);
        Assert.Equal("someone", online.UserLabel);
        Assert.Contains("RDP ready", online.RemoteAccessLabel);

        var unreachable = vm.Rows.Single(r => r.Serial == "S2");
        Assert.True(unreachable.IsUnreachable);
        Assert.Null(unreachable.Probe);
        Assert.Equal(1, runner.Calls);
        Assert.False(vm.IsScanning);
        Assert.False(vm.IsProbing);
    }

    [Fact]
    public async Task Probe_AuthFailure_IsShownOnTheRow()
    {
        var (vm, probe, runner) = Build();
        probe.Dns["LAB-01"] = "10.0.0.1";
        probe.Open.Add("10.0.0.1");
        runner.RejectKey.Add("10.0.0.1");

        await vm.SelectRoomAsync(vm.Roster.Labs[0]);

        var row = vm.Rows.Single(r => r.Serial == "S1");
        Assert.True(row.SshAuthFailed);
        Assert.Equal("SSH key rejected", row.UserLabel);
        Assert.Contains("key rejected", row.RemoteAccessLabel);
        Assert.Contains("rejected the key", row.RemoteAccessHelp);
    }

    [Fact]
    public async Task WithoutSsh_ScanStillWorksAndCanProbeIsFalse()
    {
        var (vm, probe, _) = Build(withSsh: false);
        probe.Dns["LAB-01"] = "10.0.0.1";
        probe.Open.Add("10.0.0.1");

        await vm.SelectRoomAsync(vm.Roster.Labs[0]);

        Assert.False(vm.CanProbe);
        Assert.Equal(1, vm.OnlineCount);
        Assert.Null(vm.Rows.Single(r => r.Serial == "S1").Probe);
    }

    [Fact]
    public async Task SelectionHelpers_AllOnlineNone()
    {
        var (vm, probe, _) = Build(withSsh: false);
        probe.Dns["LAB-01"] = "10.0.0.1";
        probe.Open.Add("10.0.0.1");
        await vm.SelectRoomAsync(vm.Roster.Labs[0]);

        vm.SelectAll();
        Assert.Equal(2, vm.SelectedCount);
        vm.SelectOnline();
        Assert.Equal(1, vm.SelectedCount);
        Assert.Single(vm.OnlineSelectedRows);
        vm.SelectNone();
        Assert.Equal(0, vm.SelectedCount);
    }

    [Fact]
    public async Task SearchResults_BecomeATargetSetSelectedByDefault()
    {
        var (vm, probe, _) = Build(withSsh: false);
        probe.Dns["PLEE"] = "10.0.0.3";
        var hits = vm.SearchResults("lee");
        Assert.Single(hits);

        await vm.SelectSearchResultsAsync(hits, "lee");

        Assert.Equal("Search: lee", vm.SelectionTitle);
        Assert.Single(vm.Rows);
        Assert.Equal(1, vm.SelectedCount);
    }

    [Fact]
    public void Search_MatchesEveryUsefulField()
    {
        var (vm, _, _) = Build();
        Assert.Single(vm.SearchResults("R301"));
        Assert.Single(vm.SearchResults("plee"));
        Assert.Equal(2, vm.SearchResults("Studio Lab").Count);
        Assert.Equal(3, vm.SearchResults("S").Count);
        Assert.Empty(vm.SearchResults(""));
    }

    [Fact]
    public async Task CustomGroup_CrudPersistsAndScansStoredAddresses()
    {
        var (vm, probe, _) = Build(withSsh: false);
        probe.Open.Add("10.0.0.50");

        var group = vm.CreateGroup("Front desk");
        Assert.True(vm.AddDevice(group, "DESK-01", "10.0.0.50"));
        Assert.False(vm.AddDevice(group, "desk-01", "10.0.0.50"));
        Assert.Equal(2, vm.AddRosterDevices(group, vm.Roster.Labs[0].Computers));
        Assert.Equal(0, vm.AddRosterDevices(group, vm.Roster.Labs[0].Computers));

        await vm.SelectGroupAsync(group);
        Assert.Equal(3, vm.Rows.Count);
        var desk = vm.Rows.Single(r => r.Name == "DESK-01");
        Assert.True(desk.IsOnline);
        Assert.Equal(AddressSource.Stored, desk.Scan.Source);

        vm.RenameGroup(group, "Reception");
        Assert.Equal("Reception", vm.SelectionTitle);

        var reloaded = new ManageStateStore(Path.Combine(_root, "state")).LoadCustomGroups();
        Assert.Single(reloaded);
        Assert.Equal("Reception", reloaded[0].Name);
        Assert.Equal(3, reloaded[0].Devices.Count);

        vm.RemoveDevice(group, group.Devices[0]);
        Assert.Equal(2, vm.Rows.Count);

        vm.DeleteGroup(group);
        Assert.Empty(vm.CustomGroups);
        Assert.False(vm.HasSelection);
    }

    [Fact]
    public async Task CustomGroup_RemembersAddressLearnedByScan()
    {
        var (vm, probe, _) = Build(withSsh: false);
        probe.Dns["LAB-01"] = "10.0.0.1";
        probe.Open.Add("10.0.0.1");
        var group = vm.CreateGroup("g");
        vm.AddRosterDevices(group, new[] { vm.Roster.Labs[0].Computers[0] });

        await vm.SelectGroupAsync(group);

        Assert.Equal("10.0.0.1", group.Devices[0].Ip);
        var reloaded = new ManageStateStore(Path.Combine(_root, "state")).LoadCustomGroups();
        Assert.Equal("10.0.0.1", reloaded[0].Devices[0].Ip);
    }

    [Fact]
    public void ParseDeviceLines_HandlesEveryShape()
    {
        var parsed = ManageViewModel.ParseDeviceLines("HOST-A\n10.0.0.1\nHOST-B,10.0.0.2\n# comment\n\n10.0.0.3;HOST-C\n");
        Assert.Equal(new[] { ("HOST-A", ""), ("", "10.0.0.1"), ("HOST-B", "10.0.0.2"), ("HOST-C", "10.0.0.3") }, parsed.ToArray());
    }

    [Fact]
    public void Row_DisplayStrings()
    {
        var row = new MachineRowViewModel(new RosterComputer { Serial = "S1", Hostname = "LAB-01", Asset = "A1", Location = "R101", Status = "Active" });
        Assert.Equal("Offline", row.StatusLabel);
        Assert.Equal("", row.RemoteAccessLabel);

        row.Scan = new HostScanResult { Serial = "S1", Ip = "10.0.0.1", Source = AddressSource.ReportMate, AddressCollectedAt = DateTime.UtcNow.AddDays(-3), SshOpen = true };
        Assert.Equal("online", row.StatusKey);
        Assert.Contains("3 d old", row.AddressLabel);
        Assert.Equal("ReportMate (stale)", row.SourceLabel);
        Assert.Equal("SSH ready  ·  RDP closed", row.RemoteAccessLabel);
        Assert.Contains("may belong to another machine", row.RemoteAccessHelp);

        row.Probe = MachineProbe.Parse("LAB-01", "10.0.0.1", "user=\nos=Windows 11\nrdp=enabled\nnla=yes\nrdp_port=listening\njoin=entra\n");
        Assert.Equal("Sign-in screen", row.UserLabel);
        Assert.Equal("Entra joined", row.JoinLabel);
        Assert.Equal("SSH ready  ·  RDP ready (NLA)", row.RemoteAccessLabel);
        Assert.Contains("OS: Windows 11", row.CopyAllInfo());
        Assert.Equal("LAB-01  10.0.0.1  S1  A1  R101  Windows 11", row.CopyLine);
    }

    [Fact]
    public void ManageConfig_DefaultsAndExpansion()
    {
        var c = new ManageConfig();
        Assert.EndsWith(Path.Combine(".ssh", "id_rsa.winadmins"), c.ResolvedSshKeyPath);
        Assert.Equal("winadmins", c.ResolvedSshUser);
        Assert.Equal("winadmins", c.ResolvedRdpUser);
        c.SshUser = "  ops  ";
        Assert.Equal("ops", c.ResolvedSshUser);
        Assert.Equal("ops", c.ResolvedRdpUser);
        c.RdpUser = "rdpops";
        Assert.Equal("rdpops", c.ResolvedRdpUser);
        var ssh = c.ToSecureShellConfig();
        Assert.Equal("ops", ssh.DefaultUsername);
        Assert.True(ssh.AcceptAllHostKeys);
        Assert.Null(ssh.PrivateKeyEnvVar);
        Assert.EndsWith(Path.Combine("FleetMate", "manage", "commands.yaml"), c.ResolvedCommandsPath);
    }
}
