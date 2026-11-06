using FleetMate.Core.Config;
using Serilog;

namespace FleetMate.Core.Services.Tickets;

/// <summary>What one turn at renewing came to.</summary>
public enum TdxRenewalTurn
{
    /// <summary>This process held the lock and its sign-in produced a token.</summary>
    Renewed,

    /// <summary>This process held the lock and its sign-in failed.</summary>
    RenewFailed,

    /// <summary>Another process was renewing; this one took the session it saved.</summary>
    Adopted,

    /// <summary>Another process was renewing and left nothing this one could take.</summary>
    OtherFailed,
}

/// <summary>
/// Keeps the processes that share a TeamDynamix sign-in from running the
/// sign-in chain at the same moment: the window and the inbox helper of each
/// edition, FleetMate and TicketsMate, which adopt each other's saved
/// sessions. The same idea as FleetMate for Mac's renewal lock file.
///
/// The lock is a file opened with no sharing. Only one process can hold it
/// open, it is never waited on synchronously, so the UI thread never blocks,
/// and Windows closes the handle when a process exits or crashes, which
/// releases it. The process that loses the race does not run its own sign-in:
/// it waits for the winner to finish, then takes the session the winner saved.
/// </summary>
public sealed class TdxRenewalLock
{
    /// <summary>How often a process waiting for the lock tries it again.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    private readonly string _path;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="path">The lock file; tests pass their own.</param>
    /// <param name="delay">Waits between tries; tests pass one that returns at once.</param>
    public TdxRenewalLock(string? path = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _path = path ?? DefaultPath;
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, ct));
    }

    /// <summary>
    /// One lock for both editions of this Windows user: they read each other's
    /// saved sessions, so a renewal by either serves both. It lives in
    /// FleetMate's local data folder, whichever edition takes it.
    /// </summary>
    public static string DefaultPath => Path.Combine(AppEdition.FleetMate.LocalAppDataDirectory, "tdx-renewal.lock");

    /// <summary>
    /// Take the lock without waiting. Null when another process holds it.
    /// When the file cannot be opened for any other reason, renewal goes ahead
    /// unlocked, as on the Mac, rather than never renewing.
    /// </summary>
    public IDisposable? TryAcquire()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            return new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1);
        }
        catch (IOException ex) when (IsHeldElsewhere(ex))
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[tdx-sso] The renewal lock could not be opened; renewing without it");
            return Unlocked.Instance;
        }
    }

    /// <summary>
    /// Run <paramref name="renew"/> with the lock held. When another process
    /// holds it, never run <paramref name="renew"/>: wait up to
    /// <paramref name="wait"/> for that process to finish, then run
    /// <paramref name="adopt"/> to take the session it saved, with the lock held
    /// when it could be had. The lock is released before this returns, whatever
    /// the delegates do.
    /// </summary>
    public async Task<TdxRenewalTurn> RenewOrAdoptAsync(Func<Task<bool>> renew, Func<Task<bool>> adopt,
        TimeSpan wait, CancellationToken ct = default)
    {
        var lease = TryAcquire();
        if (lease != null)
        {
            using (lease) return await renew() ? TdxRenewalTurn.Renewed : TdxRenewalTurn.RenewFailed;
        }

        Log.Information("[tdx-sso] Another process is renewing the TeamDynamix sign-in; waiting for its session");
        var tries = Math.Max(1, (int)Math.Ceiling(wait / PollInterval));
        for (var i = 0; i < tries; i++)
        {
            await _delay(PollInterval, ct);
            lease = TryAcquire();
            if (lease == null) continue;
            using (lease) return await adopt() ? TdxRenewalTurn.Adopted : TdxRenewalTurn.OtherFailed;
        }

        Log.Information("[tdx-sso] The other process is still renewing after {Wait}; taking what it saved so far", wait);
        return await adopt() ? TdxRenewalTurn.Adopted : TdxRenewalTurn.OtherFailed;
    }

    private static bool IsHeldElsewhere(IOException ex) =>
        (ex.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation;

    private sealed class Unlocked : IDisposable
    {
        public static readonly Unlocked Instance = new();
        public void Dispose() { }
    }
}
