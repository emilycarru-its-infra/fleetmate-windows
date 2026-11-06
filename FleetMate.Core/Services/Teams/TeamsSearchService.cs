using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FleetMate.Core.Config;
using FleetMate.Core.Services.Activity;

namespace FleetMate.Core.Services.Teams;

// ── Models ───────────────────────────────────────────────────────────────

/// <summary>One hit from <c>GET /v1/teams/search</c>: a thread whose root or replies matched.</summary>
public sealed record TeamsSearchHit(
    string ThreadId,
    string TeamId,
    string TeamName,
    string ChannelId,
    string ChannelName,
    string? Subject,
    string Snippet,
    string? AuthorDisplayName,
    string CreatedAt,
    string LastActivityAt,
    int ReplyCount,
    IReadOnlyList<string> Participants,
    double Score,
    string? WebUrl);

public sealed record TeamsSearchResponse(string Query, string? AsOf, int Total, IReadOnlyList<TeamsSearchHit> Results);

public sealed record TeamsAttachment(string Name, string? Url);

public sealed record TeamsMessage(
    string MessageId,
    string? ReplyToId,
    string? AuthorDisplayName,
    string CreatedAt,
    string? EditedAt,
    string Text,
    IReadOnlyList<TeamsAttachment> Attachments,
    string? WebUrl);

/// <summary><c>GET /v1/teams/threads/{id}</c>: the root message and its replies, oldest first.</summary>
public sealed record TeamsThread(
    string ThreadId,
    string TeamId,
    string TeamName,
    string ChannelId,
    string ChannelName,
    string? Subject,
    string? WebUrl,
    IReadOnlyList<TeamsMessage> Messages);

public sealed record TeamsChannel(
    string TeamId,
    string TeamName,
    string ChannelId,
    string ChannelName,
    string MembershipType,
    int Threads,
    string? LastActivityAt);

public sealed record TeamsChannelsResponse(string? AsOf, IReadOnlyList<TeamsChannel> Channels);

/// <summary>
/// A decoded response with the exact text the API returned, so <c>--json</c>
/// can pass the API's own JSON through unchanged.
/// </summary>
public sealed record TeamsPayload<T>(T Value, string Raw);

/// <summary>The filters <c>fleetmate teams search</c> passes to the API.</summary>
public sealed record TeamsSearchQuery(
    string Text,
    string? Team = null,
    string? Channel = null,
    string? Author = null,
    string? Since = null,
    string? Until = null,
    int? Limit = null);

// ── Errors ───────────────────────────────────────────────────────────────

public enum TeamsSearchFailure
{
    NotConfigured,
    InvalidArgument,
    /// <summary>401: the token was missing, expired or rejected.</summary>
    Unauthorized,
    /// <summary>403: signed in, but the account lacks the API's Teams search role.</summary>
    Forbidden,
    /// <summary>403 <c>purpose_not_granted</c>: the declared purpose is not one the API grants.</summary>
    PurposeNotGranted,
    /// <summary>404: no such thread or channel.</summary>
    NotFound,
    /// <summary>503: the search index has not been built yet.</summary>
    IndexNotReady,
    Http,
    InvalidResponse,
}

public sealed class TeamsSearchException : Exception
{
    public TeamsSearchFailure Failure { get; }
    public int? StatusCode { get; }

    public TeamsSearchException(TeamsSearchFailure failure, string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        Failure = failure;
        StatusCode = statusCode;
    }

    public static TeamsSearchException NotConfigured() => new(TeamsSearchFailure.NotConfigured,
        "Teams search is not configured. Set teams_api_url and teams_api_scope in config.yaml, or TeamsApiUrl and TeamsApiScope in the registry or policy.");

    internal static TeamsSearchException Invalid(string message) => new(TeamsSearchFailure.InvalidArgument, message);

    internal static TeamsSearchException ForStatus(int status, string detail) => status switch
    {
        401 => new(TeamsSearchFailure.Unauthorized,
            "The Teams search API rejected the sign-in token. Run `fleetmate login --check` and try again.", status),
        403 when detail.Contains("purpose_not_granted", StringComparison.Ordinal) => new(TeamsSearchFailure.PurposeNotGranted,
            "The Teams search API does not grant the configured purpose. Check teams_api_purpose.", status),
        403 => new(TeamsSearchFailure.Forbidden,
            "Your account is not authorized for Teams search. Ask your IT administrator for Teams search access.", status),
        404 => new(TeamsSearchFailure.NotFound, "No such Teams thread or channel in the search index.", status),
        503 => new(TeamsSearchFailure.IndexNotReady, "The Teams search index is not ready yet. Try again later.", status),
        _ => new(TeamsSearchFailure.Http,
            $"Teams search API returned HTTP {status}" + (detail.Length == 0 ? "." : $": {detail}"), status),
    };
}

// ── Service ──────────────────────────────────────────────────────────────

/// <summary>
/// Client for the Teams channel-message search API.
///
/// Every call carries a bearer token minted for the configured scope off the
/// operator's own Windows sign-in (<see cref="EntraTokenSource"/> through
/// <see cref="EntraBearerHandler"/>, which retries once on a 401), so the API
/// authorizes the person, not FleetMate; no secret is stored. Base URL and
/// scope come from configuration and are empty by default.
/// </summary>
public sealed class TeamsSearchService : IDisposable
{
    private readonly string _baseUrl;
    private readonly string _scope;
    private readonly string _purpose;

    /// <summary>Sent as <see cref="PurposeHeader"/> on every request unless configured otherwise.</summary>
    public const string DefaultPurpose = "purpose.its_teams_channel_search";
    public const string PurposeHeader = "X-Vantage-Purpose";
    private readonly HttpClient? _client;

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public bool IsConfigured => _baseUrl.Length > 0 && _scope.Length > 0;

    public static TeamsSearchService FromConfig(FleetMateConfig config) =>
        new(config.TeamsApiUrl, config.TeamsApiScope, purpose: config.TeamsApiPurpose);

    /// <param name="httpHandler">Replaces the HTTP transport under the bearer handler, so a test can stub the API.</param>
    /// <param name="tokenSource">Replaces the shared token source, so a test can stand in for the broker.</param>
    public TeamsSearchService(string? baseUrl, string? scope,
        HttpMessageHandler? httpHandler = null, Func<EntraTokenSource?>? tokenSource = null, string? purpose = null)
    {
        _purpose = string.IsNullOrWhiteSpace(purpose) ? DefaultPurpose : purpose.Trim();
        _baseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        _scope = (scope ?? string.Empty).Trim();
        if (!IsConfigured) return;

        var bearer = new EntraBearerHandler(_scope, tokenSource);
        if (httpHandler != null) bearer.InnerHandler = httpHandler;
        _client = new HttpClient(new ActivityLogHandler("Teams", bearer)) { Timeout = TimeSpan.FromSeconds(60) };
    }

    // ── Requests ──

    public Task<TeamsPayload<TeamsSearchResponse>> SearchAsync(TeamsSearchQuery query, CancellationToken ct = default) =>
        GetAsync<TeamsSearchResponse>(BuildSearchUri(query), ct);

    public Task<TeamsPayload<TeamsThread>> GetThreadAsync(string threadId, string channel, CancellationToken ct = default) =>
        GetAsync<TeamsThread>(BuildThreadUri(threadId, channel), ct);

    public Task<TeamsPayload<TeamsChannelsResponse>> GetChannelsAsync(CancellationToken ct = default) =>
        GetAsync<TeamsChannelsResponse>(BuildChannelsUri(), ct);

    // ── URL building ──

    public Uri BuildSearchUri(TeamsSearchQuery query)
    {
        var text = (query.Text ?? string.Empty).Trim();
        if (text.Length == 0) throw TeamsSearchException.Invalid("A search query is required.");
        if (query.Limit is { } limit && (limit < 1 || limit > 100))
            throw TeamsSearchException.Invalid("--limit must be between 1 and 100.");
        foreach (var (flag, value) in new[] { ("--since", query.Since), ("--until", query.Until) })
        {
            if (value != null && !IsDay(value))
                throw TeamsSearchException.Invalid($"{flag} must be a date in YYYY-MM-DD form.");
        }

        var items = new List<(string, string)> { ("q", text) };
        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) items.Add((name, value.Trim()));
        }
        Add("team", query.Team);
        Add("channel", query.Channel);
        Add("author", query.Author);
        Add("since", query.Since);
        Add("until", query.Until);
        if (query.Limit is { } l) items.Add(("limit", l.ToString(CultureInfo.InvariantCulture)));
        return BuildUri("/v1/teams/search", items);
    }

    public Uri BuildThreadUri(string threadId, string channel)
    {
        var id = (threadId ?? string.Empty).Trim();
        var ch = (channel ?? string.Empty).Trim();
        if (id.Length == 0) throw TeamsSearchException.Invalid("A thread id is required.");
        if (ch.Length == 0) throw TeamsSearchException.Invalid("--channel is required.");
        return BuildUri("/v1/teams/threads/" + Uri.EscapeDataString(id), new[] { ("channel", ch) });
    }

    public Uri BuildChannelsUri() => BuildUri("/v1/teams/channels", Array.Empty<(string, string)>());

    private Uri BuildUri(string path, IEnumerable<(string Name, string Value)> query)
    {
        if (!IsConfigured) throw TeamsSearchException.NotConfigured();
        var text = new StringBuilder(_baseUrl).Append(path);
        var first = true;
        foreach (var (name, value) in query)
        {
            text.Append(first ? '?' : '&').Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
            first = false;
        }
        // UriCreationOptions keeps %2F in a path segment escaped rather than unescaping it.
        if (!Uri.TryCreate(text.ToString(), new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true }, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw TeamsSearchException.Invalid("teams_api_url is not a valid http(s) URL.");
        return uri;
    }

    internal static bool IsDay(string value) =>
        value.Length == 10 && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    // ── Transport ──

    private async Task<TeamsPayload<T>> GetAsync<T>(Uri uri, CancellationToken ct)
    {
        if (_client == null) throw TeamsSearchException.NotConfigured();
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation(PurposeHeader, _purpose);
        using var response = await _client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        var status = (int)response.StatusCode;
        if (status < 200 || status >= 300)
            throw TeamsSearchException.ForStatus(status, Detail(body));
        try
        {
            var value = JsonSerializer.Deserialize<T>(body, Json)
                ?? throw new JsonException("the response was null");
            return new TeamsPayload<T>(value, body);
        }
        catch (JsonException ex)
        {
            throw new TeamsSearchException(TeamsSearchFailure.InvalidResponse,
                $"Teams search API returned an unexpected response: {ex.Message}", status, ex);
        }
    }

    /// <summary>
    /// FastAPI's <c>{"detail": "..."}</c>, else the body's first line, kept short
    /// and stripped of control characters because it is server-supplied text.
    /// </summary>
    internal static string Detail(string body)
    {
        string text;
        try
        {
            using var doc = JsonDocument.Parse(body);
            text = doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? string.Empty
                : body.Split('\n', 2)[0];
        }
        catch (JsonException)
        {
            text = body.Split('\n', 2)[0];
        }
        text = TeamsText.TerminalSafe(text).Trim();
        return text.Length > 300 ? text[..300] : text;
    }

    public void Dispose() => _client?.Dispose();
}

// ── Display helpers ──────────────────────────────────────────────────────

/// <summary>
/// Message text, subjects and names come from other people's Teams posts:
/// untrusted data. These helpers make them safe to print in a console and
/// render the API's timestamps in local time.
/// </summary>
public static class TeamsText
{
    /// <summary>
    /// Drops control and format characters (ANSI escapes, carriage returns,
    /// bidi overrides) a message could use to rewrite the console, keeping
    /// newlines and tabs.
    /// </summary>
    public static string TerminalSafe(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is '\n' or '\t') { sb.Append((char)rune.Value); continue; }
            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                    continue;
                default:
                    sb.Append(rune.ToString());
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>One line, whitespace collapsed, safe for a table cell.</summary>
    public static string OneLine(string? text) =>
        string.Join(' ', TerminalSafe(text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Parses the API's ISO 8601 timestamps; one with no zone is taken as UTC.</summary>
    public static DateTimeOffset? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    /// <summary><c>yyyy-MM-dd HH:mm</c> in <paramref name="zone"/> (local by default); the raw value if it does not parse.</summary>
    public static string LocalTime(string? value, TimeZoneInfo? zone = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        if (Parse(value) is not { } parsed) return TerminalSafe(value);
        return TimeZoneInfo.ConvertTime(parsed, zone ?? TimeZoneInfo.Local)
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }
}
