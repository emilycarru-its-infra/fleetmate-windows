using FleetMate.Core.Models;
using FleetMate.Core.Models.Manage;
using Serilog;

namespace FleetMate.Core.Services.Manage;

/// <summary>A machine to run against: identity plus the address the scan produced.</summary>
public record RunTarget(RosterComputer Computer, string Ip);

/// <summary>Progress events from a fleet run, delivered on a thread-pool thread.</summary>
public interface IRunObserver
{
    void Started(string serial);
    void Output(string serial, string chunk);
    void Finished(string serial, CommandRunStatus status, int? exitCode, string stderr, string? error);
}

/// <summary>
/// Runs one command on many machines at once, streaming each machine's
/// output as it arrives and mapping SSH outcomes onto the statuses the
/// results view shows. Commands are PowerShell and travel encoded, so the
/// remote shell never matters. Cancellation marks every unfinished machine
/// as cancelled and stops opening new connections.
/// </summary>
public class CommandRunner
{
    private readonly IRemoteRunner _runner;

    public int Concurrency { get; init; } = 12;

    public CommandRunner(IRemoteRunner runner) => _runner = runner;

    /// <summary>The remote command line for a PowerShell script or one-liner.</summary>
    public static string Wrap(string command) => RemoteScriptEncoder.Wrap(command);

    public async Task RunAsync(IReadOnlyList<RunTarget> targets, string command, IRunObserver observer, CancellationToken cancellationToken)
    {
        if (targets.Count == 0) return;
        var wrapped = Wrap(command);
        using var gate = new SemaphoreSlim(Math.Max(1, Concurrency));

        var tasks = targets.Select(async target =>
        {
            var serial = target.Computer.Serial;
            try
            {
                await gate.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                observer.Finished(serial, CommandRunStatus.Cancelled, null, "", null);
                return;
            }

            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    observer.Finished(serial, CommandRunStatus.Cancelled, null, "", null);
                    return;
                }

                observer.Started(serial);
                var result = await _runner.RunAsync(target.Ip, wrapped, chunk => observer.Output(serial, chunk), cancellationToken,
                    deviceName: target.Computer.DisplayName);
                observer.Finished(serial, MapStatus(result.Outcome), result.ExitCode, result.Stderr, result.ErrorMessage);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Run on {Host} threw", target.Ip);
                observer.Finished(serial, cancellationToken.IsCancellationRequested ? CommandRunStatus.Cancelled : CommandRunStatus.Failed, null, "", ex.Message);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);
    }

    public static CommandRunStatus MapStatus(SecureShellOutcome outcome) => outcome switch
    {
        SecureShellOutcome.Success => CommandRunStatus.Success,
        SecureShellOutcome.CommandFailed => CommandRunStatus.Failed,
        SecureShellOutcome.Unreachable => CommandRunStatus.Offline,
        SecureShellOutcome.Timeout => CommandRunStatus.Timeout,
        SecureShellOutcome.AuthFailed => CommandRunStatus.AuthFailed,
        SecureShellOutcome.HostKeyRejected => CommandRunStatus.AuthFailed,
        SecureShellOutcome.Cancelled => CommandRunStatus.Cancelled,
        _ => CommandRunStatus.Failed
    };
}

/// <summary>
/// The confirmation-gated quick actions from the machine list. Each is a
/// PowerShell one-liner that reports what it did, written so the SSH
/// session (session 0, no desktop) can still act on the console session.
/// </summary>
public static class QuickActions
{
    public record QuickAction(string Label, string Command, CommandTrustLevel Trust, string ConfirmTitle, string ConfirmMessage);

    /// <summary>Console session id, or empty when nobody is signed in.</summary>
    private const string ConsoleSession =
        "$s=(quser 2>$null | Select-String '^\\s*>?\\S+\\s+console\\s+(\\d+)' | ForEach-Object { $_.Matches[0].Groups[1].Value } | Select-Object -First 1); if(-not $s){$s=(Get-Process explorer -ErrorAction SilentlyContinue | Select-Object -First 1).SessionId}";

    public static readonly QuickAction Restart = new(
        "Restart",
        "shutdown.exe /r /t 5 /f /c 'Restart requested by FleetMate'; 'Restart scheduled in 5 seconds'",
        CommandTrustLevel.Destructive,
        "Restart {0}?",
        "This will restart every online target machine within a few seconds. Unsaved work will be lost.");

    public static readonly QuickAction LogOutUser = new(
        "Log out user",
        ConsoleSession + "; if($s){ logoff $s; \"Logged off session $s\" } else { 'No console session to log off' }",
        CommandTrustLevel.Destructive,
        "Log out the signed-in user on {0}?",
        "This will immediately sign out whoever is at the console on each machine.");

    public static readonly QuickAction Sleep = new(
        "Sleep",
        "$hib=(powercfg /a) -match 'Hibernate' -and -not ((powercfg /a) -match 'Hibernation has not been enabled'); if($hib){ powercfg /h off | Out-Null }; rundll32.exe powrprof.dll,SetSuspendState 0,1,0; 'Sleep requested'",
        CommandTrustLevel.Destructive,
        "Sleep {0}?",
        "This will put every online target machine to sleep.");

    public static readonly QuickAction LockScreen = new(
        "Lock screen",
        ConsoleSession + "; if($s){ tsdiscon $s 2>$null; \"Console session $s locked\" } else { 'No console session to lock' }",
        CommandTrustLevel.Caution,
        "Lock the screen on {0}?",
        "This will lock the console session on every online target machine.");

    public static IReadOnlyList<QuickAction> All => new[] { Restart, LogOutUser, Sleep, LockScreen };
}
