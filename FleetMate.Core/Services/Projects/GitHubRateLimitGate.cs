using Serilog;

namespace FleetMate.Core.Services.Projects;

/// <summary>
/// Process-wide latch shared by every GitHub client instance.
///
/// The dashboard queue, the issues table, the Projects provider and the pull
/// request viewer each construct their own client, but they all drain ONE
/// hourly quota. Once GitHub says rate limited, every call from every instance
/// fails fast until the window ends rather than burning more budget — a
/// rejected call still costs points, so silence is the fastest way out.
/// </summary>
public static class GitHubRateLimitGate
{
    private static readonly object Lock = new();
    private static DateTimeOffset? _until;

    /// <summary>How long to stay quiet once tripped.</summary>
    public static readonly TimeSpan DefaultBackoff = TimeSpan.FromMinutes(15);

    /// <summary>True while the gate is holding calls back.</summary>
    public static bool IsTripped
    {
        get
        {
            lock (Lock) return _until is { } until && until > DateTimeOffset.UtcNow;
        }
    }

    /// <summary>When the gate lifts, or null when it is open.</summary>
    public static DateTimeOffset? OpenAt
    {
        get
        {
            lock (Lock) return _until is { } until && until > DateTimeOffset.UtcNow ? until : null;
        }
    }

    /// <summary>
    /// Throw if the gate is closed.
    ///
    /// The message carries the phrase "rate limit" deliberately: callers that
    /// already special-case rate limiting match on it to show a quiet
    /// cached-results note instead of an error.
    /// </summary>
    /// <exception cref="GitHubRateLimitException">The gate is closed.</exception>
    public static void Check()
    {
        DateTimeOffset until;

        lock (Lock)
        {
            if (_until is not { } candidate || candidate <= DateTimeOffset.UtcNow)
            {
                _until = null;
                return;
            }

            until = candidate;
        }

        throw new GitHubRateLimitException(
            $"API rate limit exceeded — backing off until {until.ToLocalTime():t}", until);
    }

    /// <summary>Close the gate. Never shortens an existing, longer backoff.</summary>
    public static void Trip(TimeSpan? backoff = null)
    {
        var window = backoff ?? DefaultBackoff;
        var candidate = DateTimeOffset.UtcNow.Add(window);

        lock (Lock)
        {
            if (_until is null || candidate > _until) _until = candidate;
        }

        Log.Warning("[github] Rate limit tripped — gating all clients for {Seconds}s",
            (int)window.TotalSeconds);
    }

    /// <summary>Trip when an upstream message looks like a rate limit.</summary>
    public static void TripIfRateLimit(string? message)
    {
        if (LooksLikeRateLimit(message)) Trip();
    }

    /// <summary>
    /// GitHub reports throttling in several shapes: a GraphQL error naming the
    /// rate limit, a secondary-limit message, or an abuse-detection notice.
    /// </summary>
    internal static bool LooksLikeRateLimit(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;

        return message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || message.Contains("secondary rate", StringComparison.OrdinalIgnoreCase)
            || message.Contains("abuse detection", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// REST answers 403 or 429 when throttling. 403 is ambiguous — it is also
    /// plain forbidden — so it only trips the gate when the body says so;
    /// latching on every permission error would silence a client that is
    /// working fine but lacks access to one repository.
    /// </summary>
    public static void TripIfRateLimitStatus(int statusCode, string? body = null)
    {
        if (statusCode == 429)
        {
            Trip();
            return;
        }

        if (statusCode == 403 && LooksLikeRateLimit(body)) Trip();
    }

    /// <summary>Reopen the gate. For tests and an explicit operator retry.</summary>
    public static void Reset()
    {
        lock (Lock) _until = null;
    }
}

/// <summary>
/// GitHub is throttling. Carries the phrase "rate limit" in its message so
/// existing call sites that match on it keep behaving the same way.
/// </summary>
public sealed class GitHubRateLimitException : Exception
{
    public DateTimeOffset RetryAt { get; }

    public GitHubRateLimitException(string message, DateTimeOffset retryAt) : base(message)
    {
        RetryAt = retryAt;
    }
}
