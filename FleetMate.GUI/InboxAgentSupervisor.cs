using System.Diagnostics;
using System.Threading;
using FleetMate.Core.Config;
using Serilog;

namespace FleetMate.GUI;

/// <summary>
/// Keeps the inbox helper running. The installer's Run entry starts the exe
/// with <c>--inbox-agent</c>, which lands here; this starts the helper itself
/// (<see cref="InboxAgent"/>, with <c>--inbox-agent-worker</c>) as a child and
/// starts it again whenever it exits, for any reason.
///
/// Windows has no launchd KeepAlive, and a Run entry fires once per sign-in,
/// so without this a helper that crashed or quit stayed gone until the next
/// sign-in, and the saved TeamDynamix session went stale with it. Starts are
/// at least a minute apart, as launchd's ThrottleInterval keeps them on Mac.
/// </summary>
internal sealed class InboxAgentSupervisor
{
    public const string Argument = "--inbox-agent";

    /// <summary>The least time between two starts of the helper.</summary>
    public static readonly TimeSpan Throttle = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The wait after the helper left because another one was already running
    /// (one started by hand, or by an older version): an hour, not a minute.
    /// </summary>
    public static readonly TimeSpan AnotherRunningWait = TimeSpan.FromHours(1);

    private readonly Func<CancellationToken, Task<int>> _runWorker;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTime> _now;

    public InboxAgentSupervisor(
        Func<CancellationToken, Task<int>> runWorker,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTime>? now = null)
    {
        _runWorker = runWorker;
        _delay = delay ?? Task.Delay;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>The supervisor starts with the Run entry's switch and without the worker's.</summary>
    public static bool IsRequested(IEnumerable<string> args)
    {
        var list = args.ToList();
        return list.Contains(Argument, StringComparer.OrdinalIgnoreCase) && !InboxAgent.IsRequested(list);
    }

    /// <summary>How long to wait before starting the helper again.</summary>
    public static TimeSpan RestartDelay(TimeSpan ranFor, int exitCode)
    {
        if (exitCode == InboxAgent.AnotherRunningExitCode) return AnotherRunningWait;
        return ranFor >= Throttle ? TimeSpan.Zero : Throttle - ranFor;
    }

    /// <summary>Run the helper and start it again after every exit, until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var started = _now();
            int exitCode;
            try
            {
                exitCode = await _runWorker(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[inbox-agent] Could not run the helper");
                exitCode = -1;
            }

            var wait = RestartDelay(_now() - started, exitCode);
            Log.Information("[inbox-agent] Helper exited with {Code}; starting it again in {Wait}", exitCode, wait);
            try
            {
                if (wait > TimeSpan.Zero) await _delay(wait, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>The Run entry's start: one supervisor per person and edition, for as long as they are signed in.</summary>
    public static async Task RunForAppAsync(App app)
    {
        using var single = new Mutex(true, $@"Local\{AppEdition.Current.Name}.InboxAgentSupervisor", out var first);
        if (!first)
        {
            Log.Information("[inbox-agent] Exiting: another supervisor is already running");
            app.Shutdown(0);
            return;
        }

        Log.Information("[inbox-agent] Supervisor started (pid {Pid})", Environment.ProcessId);
        await new InboxAgentSupervisor(StartWorkerAsync).RunAsync(CancellationToken.None);
    }

    private static async Task<int> StartWorkerAsync(CancellationToken ct)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("The app's own path is unknown.");
        using var worker = Process.Start(new ProcessStartInfo(exe, InboxAgent.Argument) { UseShellExecute = false })
            ?? throw new InvalidOperationException("The helper did not start.");
        await worker.WaitForExitAsync(ct);
        return worker.ExitCode;
    }
}
