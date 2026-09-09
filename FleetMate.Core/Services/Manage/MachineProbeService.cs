using FleetMate.Core.Models;
using FleetMate.Core.Models.Manage;
using Serilog;

namespace FleetMate.Core.Services.Manage;

/// <summary>Outcome of probing one machine over SSH.</summary>
public class MachineProbeOutcome
{
    public string Serial { get; init; } = "";
    public MachineProbe? Probe { get; init; }
    public SecureShellOutcome Outcome { get; init; }
    public string? Error { get; init; }

    public bool AuthFailed => Outcome == SecureShellOutcome.AuthFailed;
    public bool Succeeded => Probe != null;
}

/// <summary>Runs one SSH command; the fleet service in production, a fake in tests.</summary>
public interface IRemoteRunner
{
    Task<SecureShellResult> RunAsync(string ip, string command, Action<string>? onChunk, CancellationToken cancellationToken, string? username = null, string? deviceName = null);
}

public class SecureShellRemoteRunner : IRemoteRunner
{
    private readonly SecureShellService _ssh;
    public SecureShellRemoteRunner(SecureShellService ssh) => _ssh = ssh;

    public Task<SecureShellResult> RunAsync(string ip, string command, Action<string>? onChunk, CancellationToken cancellationToken, string? username = null, string? deviceName = null) =>
        _ssh.ExecuteStreamingAsync(ip, command, onChunk, cancellationToken, username, deviceName);
}

/// <summary>
/// Fetches live machine facts for a set of online machines by running the
/// probe script over SSH, a bounded number at a time. A machine whose sshd
/// rejects the key is reported as such so the row can show it, distinct from
/// a machine that simply did not answer.
/// </summary>
public class MachineProbeService
{
    private readonly IRemoteRunner _runner;

    public int Concurrency { get; init; } = 12;

    public MachineProbeService(IRemoteRunner runner) => _runner = runner;

    public async Task<MachineProbeOutcome> ProbeAsync(RosterComputer computer, string ip, CancellationToken cancellationToken)
    {
        var command = RemoteScriptEncoder.Wrap(MachineProbe.ProbeScript);
        var result = await _runner.RunAsync(ip, command, null, cancellationToken, deviceName: computer.DisplayName);

        if (result.Outcome == SecureShellOutcome.Success || (result.Connected && result.Stdout.Contains("host=")))
        {
            return new MachineProbeOutcome
            {
                Serial = computer.Serial,
                Probe = MachineProbe.Parse(computer.DisplayName, ip, result.Stdout),
                Outcome = SecureShellOutcome.Success
            };
        }

        Log.Debug("Probe of {Host} ended {Outcome}: {Error}", ip, result.Outcome, result.ErrorMessage ?? result.Stderr);
        return new MachineProbeOutcome
        {
            Serial = computer.Serial,
            Outcome = result.Outcome,
            Error = result.ErrorMessage ?? (string.IsNullOrWhiteSpace(result.Stderr) ? null : result.Stderr)
        };
    }

    /// <summary>Probe many machines; each outcome is delivered as it completes.</summary>
    public async Task ProbeManyAsync(
        IEnumerable<(RosterComputer computer, string ip)> targets,
        Action<MachineProbeOutcome> onOutcome,
        CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, Concurrency));
        var tasks = targets.Select(async t =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var outcome = await ProbeAsync(t.computer, t.ip, cancellationToken);
                onOutcome(outcome);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        await Task.WhenAll(tasks);
    }
}
