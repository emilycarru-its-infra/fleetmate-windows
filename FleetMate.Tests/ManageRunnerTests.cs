using FleetMate.Core.Config;
using FleetMate.Core.Models;
using FleetMate.Core.Models.Manage;
using FleetMate.Core.Services.Manage;
using FleetMate.GUI.ViewModels.Manage;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The Manage view model's command runner: library seeding and CRUD, running
/// on the checked online machines with streamed results, history, kill, and
/// the derived trust of what Run would send.
/// </summary>
public class ManageRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fleetmate-runner-" + Guid.NewGuid().ToString("N"));
    private readonly string _rosterPath;

    private const string Roster =
        "serial,catalog,area,location,asset,usage,status,allocation,username,platform,fleet,hostname\n" +
        "S1,Curriculum,Studio,R101,A1,Shared,Active,Studio 01,,Windows,Studio Lab,LAB-01\n" +
        "S2,Curriculum,Studio,R101,A2,Shared,Active,Studio 02,,Windows,Studio Lab,LAB-02\n" +
        "S3,Curriculum,Studio,R101,A3,Shared,Active,Studio 03,,Windows,Studio Lab,LAB-03\n";

    public ManageRunnerTests()
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
        public Dictionary<string, SecureShellOutcome> Outcomes { get; } = new();
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;
        public List<string> Decoded { get; } = new();

        public async Task<SecureShellResult> RunAsync(string ip, string command, Action<string>? onChunk, CancellationToken ct, string? username = null, string? deviceName = null)
        {
            var script = command.StartsWith(RemoteScriptEncoder.Prefix) ? RemoteScriptEncoder.Decode(command[RemoteScriptEncoder.Prefix.Length..]) : command;
            lock (Decoded) Decoded.Add(script);
            if (script == MachineProbe.ProbeScript)
                return new SecureShellResult { Host = ip, Connected = true, ExitCode = 0, Stdout = "host=X\nuser=\nrdp=enabled\nrdp_port=listening\nssh_port=listening\n", Outcome = SecureShellOutcome.Success };

            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            var outcome = Outcomes.TryGetValue(ip, out var o) ? o : SecureShellOutcome.Success;
            if (outcome == SecureShellOutcome.Success) { onChunk?.Invoke("out-"); onChunk?.Invoke(ip); }
            return new SecureShellResult
            {
                Host = ip, Connected = outcome != SecureShellOutcome.Unreachable,
                ExitCode = outcome == SecureShellOutcome.Success ? 0 : 1,
                Stdout = outcome == SecureShellOutcome.Success ? "out-" + ip : "",
                Stderr = outcome == SecureShellOutcome.CommandFailed ? "bad" : "",
                Outcome = outcome
            };
        }
    }

    private async Task<(ManageViewModel vm, FakeRunner runner)> BuildScannedAsync(bool withSsh = true)
    {
        var config = new ManageConfig { RosterPath = _rosterPath, CommandsPath = Path.Combine(_root, "commands.yaml") };
        var store = new ManageStateStore(Path.Combine(_root, "state"));
        var probe = new FakeProbe();
        probe.Dns["LAB-01"] = "10.0.0.1"; probe.Dns["LAB-02"] = "10.0.0.2"; probe.Dns["LAB-03"] = "10.0.0.3";
        probe.Open.Add("10.0.0.1"); probe.Open.Add("10.0.0.2");
        var runner = new FakeRunner();
        var vm = new ManageViewModel(config, store, null, probe, withSsh ? runner : null);
        vm.LoadRoster();
        await vm.SelectRoomAsync(vm.Roster.Labs[0]);
        return (vm, runner);
    }

    [Fact]
    public async Task LoadRoster_SeedsTheLibraryFileAndSelectsFirstCategory()
    {
        var (vm, _) = await BuildScannedAsync();
        Assert.True(File.Exists(vm.CommandsPath));
        Assert.NotEmpty(vm.Categories);
        Assert.Equal(vm.Categories[0], vm.SelectedCategory);
        Assert.True(vm.CanRun);
    }

    [Fact]
    public async Task Run_TargetsCheckedOnlineMachinesAndStreamsResults()
    {
        var (vm, runner) = await BuildScannedAsync();
        runner.Outcomes["10.0.0.2"] = SecureShellOutcome.CommandFailed;
        vm.SelectAll();

        await vm.RunCommandAsync("hostname", "Hostname");

        Assert.Equal(2, vm.Results.Count);
        var r1 = vm.Results.Single(r => r.Serial == "S1");
        Assert.Equal(CommandRunStatus.Success, r1.Status);
        Assert.Equal("out-10.0.0.1", r1.Output);
        var r2 = vm.Results.Single(r => r.Serial == "S2");
        Assert.Equal(CommandRunStatus.Failed, r2.Status);
        Assert.Equal("bad", r2.ErrorOutput);
        Assert.Equal(1, vm.ResultSuccessCount);
        Assert.Equal(1, vm.ResultFailedCount);
        Assert.False(vm.IsRunning);
        Assert.Contains("1 succeeded", vm.RunStatus);
        Assert.Equal(CommandRunStatus.Success, vm.Rows.Single(r => r.Serial == "S1").LastRunStatus);
        Assert.Contains("hostname", runner.Decoded);
        Assert.Single(vm.History);
        Assert.Equal("Hostname", vm.History[0].Label);
    }

    [Fact]
    public async Task Run_WithoutSelectionDoesNothing()
    {
        var (vm, runner) = await BuildScannedAsync();
        await vm.RunCommandAsync("hostname", "Hostname");
        Assert.Empty(vm.Results);
        Assert.DoesNotContain("hostname", runner.Decoded);
    }

    [Fact]
    public async Task Run_UnreachableIsOffline()
    {
        var (vm, runner) = await BuildScannedAsync();
        runner.Outcomes["10.0.0.1"] = SecureShellOutcome.Unreachable;
        vm.SelectOnline();
        await vm.RunCommandAsync("hostname", "Hostname");
        Assert.Equal(CommandRunStatus.Offline, vm.Results.Single(r => r.Serial == "S1").Status);
        Assert.Equal(1, vm.ResultOfflineCount);
    }

    [Fact]
    public async Task Kill_MarksPendingAsCancelled()
    {
        var (vm, runner) = await BuildScannedAsync();
        runner.Delay = TimeSpan.FromSeconds(5);
        vm.SelectOnline();
        var run = vm.RunCommandAsync("Start-Sleep 5", "Sleep");
        await Task.Delay(100);
        Assert.True(vm.IsRunning);
        vm.KillRun();
        await run;
        Assert.False(vm.IsRunning);
        Assert.All(vm.Results, r => Assert.Equal(CommandRunStatus.Cancelled, r.Status));
    }

    [Fact]
    public async Task RunCompleted_ReportsCounts()
    {
        var (vm, runner) = await BuildScannedAsync();
        runner.Outcomes["10.0.0.2"] = SecureShellOutcome.Unreachable;
        vm.SelectOnline();
        (int, int, int)? counts = null;
        vm.RunCompleted += (s, f, o) => counts = (s, f, o);
        await vm.RunCommandAsync("hostname", "Hostname");
        Assert.Equal((1, 0, 1), counts);
    }

    [Fact]
    public async Task EffectiveTrust_FollowsCustomTextThenLibrary()
    {
        var (vm, _) = await BuildScannedAsync();
        var category = vm.Categories.First(c => c.Name == "Cimian Operations");
        vm.SelectedCategory = category;
        vm.SelectedCommand = category.Commands.First(c => c.Label == "Check and install everything pending");
        Assert.Equal(CommandTrustLevel.Caution, vm.EffectiveTrust);
        Assert.False(vm.EffectiveTrustIsInferred);
        Assert.StartsWith("managedsoftwareupdate --auto", vm.ResolvedCommandString);

        vm.CustomCommand = "Restart-Computer -Force";
        Assert.Equal(CommandTrustLevel.Destructive, vm.EffectiveTrust);
        Assert.True(vm.EffectiveTrustIsInferred);
        Assert.Equal("Custom command", vm.ResolvedCommandLabel);

        vm.CustomCommand = "";
        Assert.Equal("Check and install everything pending", vm.ResolvedCommandLabel);
    }

    [Fact]
    public async Task Library_CrudPersistsToYaml()
    {
        var (vm, _) = await BuildScannedAsync();
        var category = vm.AddCategory("Custom checks");
        var cmd = vm.AddCommand(category, "List printers", "Get-Printer | Select-Object Name", CommandTrustLevel.Safe);
        Assert.Equal(cmd, vm.SelectedCommand);

        vm.EditCommand(category, cmd, "Printers", "Get-Printer", CommandTrustLevel.Safe);
        var reloaded = CommandLibrary.Load(vm.CommandsPath);
        var printing = reloaded.Single(c => c.Name == "Custom checks");
        Assert.Single(printing.Commands);
        Assert.Equal("Printers", printing.Commands[0].Label);
        Assert.Equal("Get-Printer", printing.Commands[0].Command);

        vm.DeleteCommand(category, cmd);
        Assert.Null(vm.SelectedCommand);
        Assert.Empty(CommandLibrary.Load(vm.CommandsPath).Single(c => c.Name == "Custom checks").Commands);
    }

    [Fact]
    public async Task ChangingRoom_ClearsResults()
    {
        var (vm, _) = await BuildScannedAsync();
        vm.SelectOnline();
        await vm.RunCommandAsync("hostname", "Hostname");
        Assert.NotEmpty(vm.Results);
        await vm.SelectRoomAsync(vm.Roster.Labs[0]);
        Assert.Empty(vm.Results);
    }

    [Fact]
    public async Task History_ClearEmptiesStore()
    {
        var (vm, _) = await BuildScannedAsync();
        vm.SelectOnline();
        await vm.RunCommandAsync("hostname", "Hostname");
        Assert.Single(vm.History);
        vm.ClearHistory();
        Assert.Empty(vm.History);
        Assert.Empty(new ManageStateStore(Path.Combine(_root, "state")).LoadHistory());
    }

    [Fact]
    public void ResultViewModel_PreviewAndFormatting()
    {
        var r = new CommandResultViewModel(new RosterComputer { Serial = "S1", Hostname = "LAB-01", Allocation = "Studio 01", Status = "Active" }, "10.0.0.1");
        Assert.Equal("Studio 01", r.Name);
        Assert.Equal("pending", r.StatusKey);
        r.AppendOutput("\r\n");
        r.AppendOutput("first line\r\nsecond");
        Assert.Equal("first line", r.Preview);
        r.Status = CommandRunStatus.Failed;
        r.ExitCode = 3;
        r.ErrorOutput = "oops";
        Assert.Equal("Exit 3", r.StatusLabel);
        Assert.Equal("failed", r.StatusKey);
        var text = r.Formatted();
        Assert.StartsWith("Studio 01 (10.0.0.1) - exit 3", text);
        Assert.Contains("stderr:", text);
        Assert.Contains("oops", text);
    }
}
