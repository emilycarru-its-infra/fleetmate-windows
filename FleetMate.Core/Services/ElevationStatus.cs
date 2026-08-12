namespace FleetMate.Core.Services;

/// <summary>
/// Records whether elevated Graph calls actually reached Graph.
///
/// Callers turn a failed HTTP response into null or an empty list — "user not
/// found", "no devices". That is right for a genuine 404 and badly wrong for a
/// call that never completed: an operator whose elevation session cannot start
/// sees an empty fleet rather than an error, and on a destructive command that
/// reads as "nothing to clean up" instead of "I could not look".
///
/// One of these is shared between the transport and the service, so a command
/// can ask whether the answer it is about to print was actually observed.
/// Thread-safe: Graph calls are issued concurrently.
/// </summary>
public sealed class ElevationStatus
{
    private readonly object _gate = new();
    private int _failures;
    private string? _lastError;

    /// <summary>How many elevated calls failed to complete in this session.</summary>
    public int Failures
    {
        get { lock (_gate) return _failures; }
    }

    /// <summary>True when at least one elevated call never reached Graph.</summary>
    public bool HasFailed
    {
        get { lock (_gate) return _failures > 0; }
    }

    /// <summary>The most recent failure reason, for the operator-facing message.</summary>
    public string? LastError
    {
        get { lock (_gate) return _lastError; }
    }

    public void RecordFailure(string reason)
    {
        lock (_gate)
        {
            _failures++;
            _lastError = reason;
        }
    }

    /// <summary>
    /// Failure count at a point in time, so a caller can tell whether its own
    /// sequence of calls failed rather than something earlier in the session.
    /// </summary>
    public int Snapshot() => Failures;

    /// <summary>True when a failure was recorded since <paramref name="snapshot"/>.</summary>
    public bool FailedSince(int snapshot) => Failures > snapshot;
}
