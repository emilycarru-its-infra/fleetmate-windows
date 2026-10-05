using System.Globalization;
using Serilog;

namespace FleetMate.Core.Services.Projects;

/// <summary>GitHub's separate hourly budgets.</summary>
public enum GitHubRateLimitBucket
{
    /// <summary>REST v3 ("core"): 5,000 requests an hour per user.</summary>
    Core,
    /// <summary>GraphQL: 5,000 points an hour per user, counted apart from REST.</summary>
    GraphQL,
}

/// <summary>What GitHub last reported for one budget.</summary>
public sealed record GitHubRateLimitStatus(
    int? Remaining, int? Limit, DateTimeOffset? ResetsAt, DateTimeOffset? BlockedUntil);

/// <summary>
/// Process-wide gates shared by every GitHub client instance.
///
/// The dashboard queue, the Development tab, the Projects provider and the
/// pull request viewer each construct their own client, but they all drain
/// the same per-user budgets, and so does every other tool signed in as the
/// same person. GitHub keeps REST ("core") and GraphQL apart, so each has its
/// own gate: spending GraphQL must not stop REST calls that still have budget.
///
/// A gate closes only when GitHub says so:
/// - X-RateLimit-Remaining reaches 0, until X-RateLimit-Reset;
/// - a 403 or 429 carries Retry-After, for that long (a secondary limit);
/// - a 429 without Retry-After, for a minute;
/// - a GraphQL error body names the rate limit, until the reported reset or a minute.
/// A plain 403 is a permission error and never closes a gate.
/// While closed, calls fail fast with <see cref="GitHubRateLimitException"/>
/// rather than spending budget on a request that will be rejected.
/// </summary>
public static class GitHubRateLimitGate
{
    /// <summary>The wait when GitHub limits us without saying for how long.</summary>
    public static readonly TimeSpan UnspecifiedWait = TimeSpan.FromSeconds(60);

    private static readonly object Lock = new();
    private static readonly Dictionary<GitHubRateLimitBucket, DateTimeOffset> Blocked = new();
    private static readonly Dictionary<GitHubRateLimitBucket, (int Remaining, int Limit, DateTimeOffset Reset)> Latest = new();

    /// <summary>For tests: the clock the gates read.</summary>
    internal static Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>True while a gate is holding calls back.</summary>
    public static bool IsTripped(GitHubRateLimitBucket bucket) => OpenAt(bucket) != null;

    /// <summary>When a gate lifts, or null when it is open.</summary>
    public static DateTimeOffset? OpenAt(GitHubRateLimitBucket bucket)
    {
        lock (Lock)
            return Blocked.TryGetValue(bucket, out var until) && until > Now() ? until : null;
    }

    /// <summary>The last budget GitHub reported for a bucket.</summary>
    public static GitHubRateLimitStatus Status(GitHubRateLimitBucket bucket)
    {
        lock (Lock)
        {
            var hasLatest = Latest.TryGetValue(bucket, out var latest);
            DateTimeOffset? blocked = Blocked.TryGetValue(bucket, out var until) && until > Now() ? until : null;
            return new GitHubRateLimitStatus(
                hasLatest ? latest.Remaining : null,
                hasLatest ? latest.Limit : null,
                hasLatest ? latest.Reset : null,
                blocked);
        }
    }

    /// <summary>
    /// Throw if the bucket's gate is closed. The message carries the phrase
    /// "rate limit" on purpose: callers that match on it show a quiet
    /// cached-results note instead of an error.
    /// </summary>
    /// <exception cref="GitHubRateLimitException">The gate is closed.</exception>
    public static void Check(GitHubRateLimitBucket bucket)
    {
        DateTimeOffset until;
        lock (Lock)
        {
            if (!Blocked.TryGetValue(bucket, out until) || until <= Now())
            {
                Blocked.Remove(bucket);
                return;
            }
        }
        throw new GitHubRateLimitException(
            $"API rate limit exceeded — backing off until {until.ToLocalTime():t}", until, bucket);
    }

    /// <summary>
    /// Read GitHub's rate-limit headers from any response, and close the gate
    /// when they say the budget is spent or a wait is required.
    /// </summary>
    public static void Record(HttpResponseMessage response, GitHubRateLimitBucket bucket)
    {
        var remaining = IntHeader(response, "X-RateLimit-Remaining");
        var limit = IntHeader(response, "X-RateLimit-Limit");
        var resetSeconds = IntHeader(response, "X-RateLimit-Reset");
        DateTimeOffset? reset = resetSeconds is { } r ? DateTimeOffset.FromUnixTimeSeconds(r) : null;
        var retryAfter = RetryAfter(response);

        lock (Lock)
        {
            if (remaining is { } rem && limit is { } lim && reset is { } at) Latest[bucket] = (rem, lim, at);
        }

        var status = (int)response.StatusCode;
        var limited = status is 403 or 429;
        if (limited && retryAfter is { } wait)
            Close(bucket, Now() + wait, "Retry-After");
        else if (remaining == 0 && reset is { } resetAt)
            Close(bucket, resetAt, "budget spent");
        else if (status == 429)
            Close(bucket, Now() + UnspecifiedWait, "429 without Retry-After");
    }

    /// <summary>
    /// A GraphQL error body that names the rate limit (GraphQL reports it as a
    /// 200). Waits for the reset GitHub last reported, or a minute.
    /// </summary>
    public static void TripIfRateLimit(string? message, GitHubRateLimitBucket bucket)
    {
        if (!LooksLikeRateLimit(message)) return;
        DateTimeOffset? reset;
        lock (Lock) reset = Latest.TryGetValue(bucket, out var latest) && latest.Reset > Now() ? latest.Reset : null;
        Close(bucket, reset ?? Now() + UnspecifiedWait, "error body names the rate limit");
    }

    /// <summary>
    /// How long a poller should wait before its next attempt: its normal
    /// interval, or longer while the gate is closed, so it wakes when GitHub's
    /// reset arrives instead of retrying on a fixed timer.
    /// </summary>
    public static TimeSpan NextPollDelay(GitHubRateLimitBucket bucket, TimeSpan normal)
    {
        var openAt = OpenAt(bucket);
        if (openAt is not { } at) return normal;
        var untilOpen = at - Now() + TimeSpan.FromSeconds(1);
        return untilOpen > normal ? untilOpen : normal;
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

    /// <summary>Close a gate. Never shortens an existing, longer wait.</summary>
    internal static void Close(GitHubRateLimitBucket bucket, DateTimeOffset until, string reason)
    {
        lock (Lock)
        {
            if (Blocked.TryGetValue(bucket, out var existing) && existing >= until) return;
            Blocked[bucket] = until;
        }
        Log.Warning("[github] {Bucket} gate closed until {Until:t} ({Reason})", bucket, until.ToLocalTime(), reason);
    }

    /// <summary>Reopen every gate and forget the budgets. For tests and an explicit retry.</summary>
    public static void Reset()
    {
        lock (Lock)
        {
            Blocked.Clear();
            Latest.Clear();
        }
    }

    private static int? IntHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
        && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value : null;

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date) return date - DateTimeOffset.UtcNow;
        return null;
    }
}

/// <summary>
/// GitHub is throttling this bucket. Carries the phrase "rate limit" in its
/// message so call sites that match on it keep behaving the same way.
/// </summary>
public sealed class GitHubRateLimitException : Exception
{
    public DateTimeOffset RetryAt { get; }
    public GitHubRateLimitBucket Bucket { get; }

    public GitHubRateLimitException(string message, DateTimeOffset retryAt, GitHubRateLimitBucket bucket) : base(message)
    {
        RetryAt = retryAt;
        Bucket = bucket;
    }
}
