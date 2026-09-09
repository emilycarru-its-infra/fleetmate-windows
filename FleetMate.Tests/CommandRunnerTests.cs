using FleetMate.Core.Models;
using FleetMate.Core.Models.Manage;
using FleetMate.Core.Services.Manage;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The fleet runner streams per-host output, maps SSH outcomes to result
/// statuses, bounds concurrency, and cancels cleanly. A fake remote runner
/// stands in for SSH.
/// </summary>
public class CommandRunnerTests
{
    private sealed class FakeRemote : IRemoteRunner
    {
        public Dictionary<string, SecureShellOutcome> Outcomes { get; } = new();
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;
        public int MaxConcurrent;
        private int _current;
        public List<string> Commands { get; } = new();

        public async Task<SecureShellResult> RunAsync(string ip, string command, Action<string>? onChunk, CancellationToken ct, string? username = null, string? deviceName = null)
        {
            lock (Commands) Commands.Add(command);
            var now = Interlocked.Increment(ref _current);
            lock (Commands) MaxConcurrent = Math.Max(MaxConcurrent, now);
            try
            {
                if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
                var outcome = Outcomes.TryGetValue(ip, out var o) ? o : SecureShellOutcome.Success;
                if (outcome == SecureShellOutcome.Success)
                {
                    onChunk?.Invoke("line 1\n");
                    onChunk?.Invoke("line 2\n");
                }
                return new SecureShellResult
                {
                    Host = ip,
                    Connected = outcome is SecureShellOutcome.Success or SecureShellOutcome.CommandFailed or SecureShellOutcome.Timeout,
                    ExitCode = outcome == SecureShellOutcome.Success ? 0 : outcome == SecureShellOutcome.CommandFailed ? 3 : -1,
                    Stdout = outcome == SecureShellOutcome.Success ? "line 1\nline 2\n" : "",
                    Stderr = outcome == SecureShellOutcome.CommandFailed ? "boom" : "",
                    Outcome = outcome,
                    Error = outcome is SecureShellOutcome.Unreachable ? new Exception("No route to host") : null
                };
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }
    }

    private sealed class Recorder : IRunObserver
    {
        public List<string> StartedSerials { get; } = new();
        public Dictionary<string, string> Outputs { get; } = new();
        public Dictionary<string, (CommandRunStatus status, int? exit, string stderr, string? error)> Results { get; } = new();
        private readonly object _lock = new();

        public void Started(string serial) { lock (_lock) StartedSerials.Add(serial); }
        public void Output(string serial, string chunk) { lock (_lock) Outputs[serial] = (Outputs.TryGetValue(serial, out var s) ? s : "") + chunk; }
        public void Finished(string serial, CommandRunStatus status, int? exitCode, string stderr, string? error) { lock (_lock) Results[serial] = (status, exitCode, stderr, error); }
    }

    private static RunTarget Target(string serial, string ip) =>
        new(new RosterComputer { Serial = serial, Hostname = "H-" + serial, Status = "Active" }, ip);

    [Fact]
    public async Task Run_StreamsOutputAndMapsOutcomes()
    {
        var remote = new FakeRemote();
        remote.Outcomes["10.0.0.2"] = SecureShellOutcome.CommandFailed;
        remote.Outcomes["10.0.0.3"] = SecureShellOutcome.Unreachable;
        remote.Outcomes["10.0.0.4"] = SecureShellOutcome.AuthFailed;
        remote.Outcomes["10.0.0.5"] = SecureShellOutcome.Timeout;
        var recorder = new Recorder();

        await new CommandRunner(remote).RunAsync(
            new[] { Target("S1", "10.0.0.1"), Target("S2", "10.0.0.2"), Target("S3", "10.0.0.3"), Target("S4", "10.0.0.4"), Target("S5", "10.0.0.5") },
            "hostname", recorder, CancellationToken.None);

        Assert.Equal(5, recorder.StartedSerials.Count);
        Assert.Equal("line 1\nline 2\n", recorder.Outputs["S1"]);
        Assert.Equal((CommandRunStatus.Success, (int?)0, "", (string?)null), recorder.Results["S1"]);
        Assert.Equal(CommandRunStatus.Failed, recorder.Results["S2"].status);
        Assert.Equal(3, recorder.Results["S2"].exit);
        Assert.Equal("boom", recorder.Results["S2"].stderr);
        Assert.Equal(CommandRunStatus.Offline, recorder.Results["S3"].status);
        Assert.Equal("No route to host", recorder.Results["S3"].error);
        Assert.Equal(CommandRunStatus.AuthFailed, recorder.Results["S4"].status);
        Assert.Equal(CommandRunStatus.Timeout, recorder.Results["S5"].status);
    }

    [Fact]
    public async Task Run_SendsTheCommandEncoded()
    {
        var remote = new FakeRemote();
        await new CommandRunner(remote).RunAsync(new[] { Target("S1", "10.0.0.1") }, "Get-Service sshd", new Recorder(), CancellationToken.None);
        Assert.StartsWith(RemoteScriptEncoder.Prefix, remote.Commands[0]);
        Assert.Equal("Get-Service sshd", RemoteScriptEncoder.Decode(remote.Commands[0][RemoteScriptEncoder.Prefix.Length..]));
    }

    [Fact]
    public async Task Run_BoundsConcurrency()
    {
        var remote = new FakeRemote { Delay = TimeSpan.FromMilliseconds(60) };
        var targets = Enumerable.Range(1, 12).Select(i => Target($"S{i}", $"10.0.0.{i}")).ToList();
        await new CommandRunner(remote) { Concurrency = 3 }.RunAsync(targets, "hostname", new Recorder(), CancellationToken.None);
        Assert.True(remote.MaxConcurrent <= 3, $"max concurrent was {remote.MaxConcurrent}");
        Assert.True(remote.MaxConcurrent >= 2, "expected some parallelism");
    }

    [Fact]
    public async Task Run_CancellationMarksUnfinishedAsCancelled()
    {
        var remote = new FakeRemote { Delay = TimeSpan.FromSeconds(5) };
        var recorder = new Recorder();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var targets = Enumerable.Range(1, 6).Select(i => Target($"S{i}", $"10.0.0.{i}")).ToList();

        await new CommandRunner(remote) { Concurrency = 2 }.RunAsync(targets, "Start-Sleep 5", recorder, cts.Token);

        Assert.Equal(6, recorder.Results.Count);
        Assert.All(recorder.Results.Values, f => Assert.Equal(CommandRunStatus.Cancelled, f.status));
    }

    [Fact]
    public async Task Run_NoTargetsIsANoOp()
    {
        var recorder = new Recorder();
        await new CommandRunner(new FakeRemote()).RunAsync(Array.Empty<RunTarget>(), "hostname", recorder, CancellationToken.None);
        Assert.Empty(recorder.StartedSerials);
    }

    [Fact]
    public void QuickActions_AreTrustedAndFitTheCommandLine()
    {
        Assert.Equal(4, QuickActions.All.Count);
        foreach (var action in QuickActions.All)
        {
            Assert.True(RemoteScriptEncoder.Fits(action.Command), $"{action.Label} does not fit");
            Assert.NotEqual(CommandTrustLevel.Safe, action.Trust);
            Assert.Contains("{0}", action.ConfirmTitle);
        }
        Assert.Equal(CommandTrustLevel.Destructive, QuickActions.Restart.Trust);
        Assert.Equal(CommandTrustLevel.Caution, QuickActions.LockScreen.Trust);
        Assert.Contains("shutdown.exe /r", QuickActions.Restart.Command);
        Assert.Contains("logoff", QuickActions.LogOutUser.Command);
        Assert.Contains("tsdiscon", QuickActions.LockScreen.Command);
    }

    [Fact]
    public void QuickActions_AreAtLeastAsTrustedAsInference()
    {
        foreach (var action in QuickActions.All)
            Assert.True(action.Trust >= TrustInference.Infer(action.Command), $"{action.Label}: inference says {TrustInference.Infer(action.Command)}");
    }
}
