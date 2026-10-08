using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Serilog;

namespace FleetMate.Core.Services;

/// <summary>
/// Retry policy for throttled Microsoft Graph requests. Graph throttles per
/// application and answers 429 with a <c>Retry-After</c> header; a bulk action or
/// a whole-tenant read should wait it out rather than fail partway through.
/// The decisions are pure functions so they can be tested without a network.
/// </summary>
public static partial class GraphThrottle
{
    /// <summary>Retries after the first attempt, so a request is tried at most four times.</summary>
    public const int MaxRetries = 3;

    /// <summary>The longest single wait, whatever the server asks for.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    /// <summary>The first wait when the server gives no Retry-After; it doubles: 2, 4, 8 s.</summary>
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A Retry-After value: delta-seconds or an HTTP-date. Null when missing or
    /// unreadable; a date already past is zero.
    /// </summary>
    public static TimeSpan? ParseRetryAfter(string? value, DateTimeOffset now)
    {
        var raw = value?.Trim();
        if (string.IsNullOrEmpty(raw)) return null;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return seconds >= 0 ? TimeSpan.FromSeconds(seconds) : null;
        if (DateTimeOffset.TryParseExact(raw, "r", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var date))
        {
            var wait = date - now;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    /// <summary>Read the wait from a response's Retry-After header, in either form.</summary>
    public static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var header = response.Headers.RetryAfter;
        if (header == null) return null;
        if (header.Delta is { } delta) return delta;
        if (header.Date is { } date)
        {
            var wait = date - now;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    /// <summary>
    /// Whether a status should be retried. A 503 counts only when the server said
    /// when to come back; a bare 503 is an outage, not a throttle.
    /// </summary>
    public static bool IsThrottled(HttpStatusCode status, bool hasRetryAfter) =>
        status == HttpStatusCode.TooManyRequests || (status == HttpStatusCode.ServiceUnavailable && hasRetryAfter);

    /// <summary>
    /// The wait before retry number <paramref name="retry"/> (1-based): the
    /// server's value when it gave one, otherwise 2, 4, 8 s, both capped at 60 s.
    /// </summary>
    public static TimeSpan Delay(int retry, TimeSpan? retryAfter)
    {
        var wait = retryAfter ?? TimeSpan.FromTicks(BaseDelay.Ticks * (1L << Math.Max(0, retry - 1)));
        return wait > MaxDelay ? MaxDelay : wait;
    }

    /// <summary>
    /// <c>az rest</c> reports an HTTP failure as text such as
    /// <c>Too Many Requests({"error":…})</c>. Map that text to the status Graph
    /// sent: 429, or 503 when Graph says it is throttling. Null for anything else.
    /// </summary>
    public static HttpStatusCode? StatusFromAzRestMessage(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var lower = message.ToLowerInvariant();
        if (lower.Contains("too many requests") || lower.Contains("toomanyrequests")) return HttpStatusCode.TooManyRequests;
        if (lower.Contains("service unavailable") && lower.Contains("throttl")) return HttpStatusCode.ServiceUnavailable;
        return null;
    }

    /// <summary>A Retry-After carried in az rest's error text, when it prints one.</summary>
    public static TimeSpan? RetryAfterFromAzRestMessage(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var m = RetryAfterText().Match(message);
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
            ? TimeSpan.FromSeconds(s)
            : null;
    }

    [GeneratedRegex(@"retry-?after[""']?\s*[:=]\s*[""']?(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex RetryAfterText();
}

/// <summary>
/// Waits out Graph throttling for every request on the GraphService client, on
/// both the direct and the elevation transport: a 429, or a 503 that carries
/// Retry-After, is retried after the server's wait (or 2, 4, 8 s), each wait
/// capped at 60 s, at most three times. After that the last response is
/// returned to the caller unchanged.
/// </summary>
public sealed class GraphThrottlingHandler : DelegatingHandler
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _now;

    public GraphThrottlingHandler(HttpMessageHandler inner,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateTimeOffset>? now = null)
        : base(inner)
    {
        _delay = delay ?? Task.Delay;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // A body is read once by the transport; buffer it so a retry sends it again intact.
        if (request.Content != null) await request.Content.LoadIntoBufferAsync(cancellationToken);

        for (var retry = 0; ; retry++)
        {
            var response = await base.SendAsync(request, cancellationToken);
            var retryAfter = GraphThrottle.RetryAfter(response, _now());
            if (!GraphThrottle.IsThrottled(response.StatusCode, retryAfter != null) || retry >= GraphThrottle.MaxRetries)
                return response;

            var wait = GraphThrottle.Delay(retry + 1, retryAfter);
            Log.Information("Graph throttled {Method} {Path} (HTTP {Status}); retry {Retry}/{Max} in {Seconds}s",
                request.Method.Method, request.RequestUri?.AbsolutePath, (int)response.StatusCode,
                retry + 1, GraphThrottle.MaxRetries, Math.Round(wait.TotalSeconds, 1));
            response.Dispose();
            await _delay(wait, cancellationToken);
        }
    }
}
