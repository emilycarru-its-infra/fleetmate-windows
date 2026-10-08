using System.Diagnostics;

namespace FleetMate.Core.Services.Activity;

/// <summary>
/// One HTTP request FleetMate made. Only the method, the host, the path and the
/// outcome are kept: never headers, never the query string, never a body.
/// </summary>
public sealed record ActivityRequest(
    DateTimeOffset StartedAt,
    string Method,
    string Host,
    string Path,
    int? Status,
    TimeSpan Duration,
    string? Failure,
    IReadOnlyList<string> Serials)
{
    /// <summary>
    /// A request with no status succeeded when nothing failed: Graph calls made
    /// through an elevation session report only success or failure.
    /// </summary>
    public bool Succeeded => Failure == null && (Status is not { } s || (s >= 200 && s < 400));

    public string StatusText => Status?.ToString() ?? Failure ?? "OK";
}

/// <summary>Something FleetMate was asked to do, and the requests made for it.</summary>
public sealed class ActivityAction
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Title { get; init; }
    public required string Service { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; internal set; }
    public string? Failure { get; internal set; }
    /// <summary>
    /// Requests nobody asked for by name (polling, refreshes) are grouped into
    /// one background row per service.
    /// </summary>
    public bool IsBackground { get; init; }

    internal List<string> SerialList { get; } = new();
    internal List<ActivityRequest> RequestList { get; } = new();

    public IReadOnlyList<string> Serials => SerialList;
    public IReadOnlyList<ActivityRequest> Requests => RequestList;
    public bool IsFinished => FinishedAt != null;

    public string Result
    {
        get
        {
            if (!IsFinished) return "Running";
            if (Failure != null) return $"Failed: {Failure}";
            if (RequestList.Any(r => !r.Succeeded)) return IsBackground ? "Some failed" : "Failed";
            return "OK";
        }
    }

    /// <summary>A copy that later requests will not change, for display.</summary>
    internal ActivityAction Snapshot()
    {
        var copy = new ActivityAction
        {
            Id = Id, Title = Title, Service = Service, StartedAt = StartedAt, IsBackground = IsBackground,
            FinishedAt = FinishedAt, Failure = Failure,
        };
        copy.SerialList.AddRange(SerialList);
        copy.RequestList.AddRange(RequestList);
        return copy;
    }
}

/// <summary>
/// What FleetMate asked each service to do and how it answered, in memory only
/// and capped. Feeds the Activity Log window.
/// </summary>
public sealed class ActivityLog
{
    public static ActivityLog Shared { get; } = new();

    private static readonly AsyncLocal<ActivityAction?> Current = new();
    private static readonly TimeSpan BackgroundWindow = TimeSpan.FromSeconds(10);

    private readonly object _lock = new();
    private readonly List<ActivityAction> _actions = new();
    private readonly Dictionary<string, string> _deviceSerials = new(StringComparer.OrdinalIgnoreCase);

    public ActivityLog(int capacity = 500) => Capacity = capacity;

    public int Capacity { get; }

    /// <summary>Raised on any change, from whichever thread made it.</summary>
    public event EventHandler? Changed;

    /// <summary>Oldest first; each entry is a copy.</summary>
    public IReadOnlyList<ActivityAction> Snapshot()
    {
        lock (_lock) return _actions.Select(a => a.Snapshot()).ToList();
    }

    public void Clear()
    {
        lock (_lock) _actions.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Remember which serial a device id belongs to, so a request that names
    /// only the id can still be found by serial.
    /// </summary>
    public void Remember(string? serial, string? deviceId)
    {
        if (string.IsNullOrEmpty(serial) || string.IsNullOrEmpty(deviceId)) return;
        lock (_lock) _deviceSerials[deviceId] = serial;
    }

    /// <summary>Serials already seen for these device ids.</summary>
    public IReadOnlyList<string> SerialsFor(IEnumerable<string> deviceIds)
    {
        lock (_lock) return deviceIds.Select(id => _deviceSerials.GetValueOrDefault(id)).OfType<string>().ToList();
    }

    /// <summary>
    /// Run <paramref name="work"/> as a named action; requests it makes are
    /// listed under it. Inside another action it simply runs.
    /// </summary>
    public async Task<T> RunAsync<T>(string title, string service, IEnumerable<string>? serials, Func<Task<T>> work)
    {
        if (Current.Value != null) return await work();
        var action = Begin(title, service, serials);
        Current.Value = action;
        try
        {
            var value = await work();
            Finish(action, null);
            return value;
        }
        catch (Exception ex)
        {
            Finish(action, Describe(ex));
            throw;
        }
        finally
        {
            Current.Value = null;
        }
    }

    public ActivityAction Begin(string title, string service, IEnumerable<string>? serials = null)
    {
        var action = new ActivityAction { Title = title, Service = service };
        if (serials != null) action.SerialList.AddRange(serials);
        lock (_lock)
        {
            _actions.Add(action);
            Trim();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return action;
    }

    public void Finish(ActivityAction action, string? failure)
    {
        lock (_lock)
        {
            action.FinishedAt = DateTimeOffset.Now;
            action.Failure = failure;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Record one request. Files it under the current action when there is
    /// one, else the newest running action for the same service, else that
    /// service's background row.
    /// </summary>
    public void Record(string service, string method, Uri? url, int? status, DateTimeOffset startedAt,
                       TimeSpan duration, string? failure = null, ActivityAction? action = null)
    {
        action ??= Current.Value;
        lock (_lock)
        {
            var path = url?.IsAbsoluteUri == true ? url.AbsolutePath : url?.OriginalString.Split('?')[0] ?? "";
            var serials = ActivityMasker.SerialsInQuery(url).ToList();
            foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (_deviceSerials.TryGetValue(segment, out var serial) && !serials.Contains(serial)) serials.Add(serial);
            }
            var entry = new ActivityRequest(startedAt, method.ToUpperInvariant(),
                url?.IsAbsoluteUri == true ? url.Host : "", path, status, duration, failure, serials);

            var target = action != null && _actions.Contains(action)
                ? action
                : _actions.LastOrDefault(a => !a.IsBackground && !a.IsFinished && a.Service == service);
            if (target == null)
            {
                var last = _actions.LastOrDefault(a => a.IsBackground && a.Service == service);
                if (last != null && startedAt - last.StartedAt < BackgroundWindow)
                {
                    target = last;
                }
                else
                {
                    target = new ActivityAction { Title = "Background requests", Service = service, StartedAt = startedAt, IsBackground = true };
                    _actions.Add(target);
                }
            }
            target.RequestList.Add(entry);
            foreach (var serial in serials)
            {
                if (!target.SerialList.Contains(serial)) target.SerialList.Add(serial);
            }
            if (target.IsBackground) target.FinishedAt = DateTimeOffset.Now;
            Trim();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Actions whose title, service, serials or request paths contain the query.</summary>
    public static IEnumerable<ActivityAction> Filter(IEnumerable<ActivityAction> actions, string? query)
    {
        var needle = query?.Trim();
        if (string.IsNullOrEmpty(needle)) return actions;
        return actions.Where(a =>
            a.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || a.Service.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || a.Serials.Any(s => s.Contains(needle, StringComparison.OrdinalIgnoreCase))
            || a.Requests.Any(r => r.Path.Contains(needle, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Caller holds the lock.</summary>
    private void Trim()
    {
        if (_actions.Count > Capacity) _actions.RemoveRange(0, _actions.Count - Capacity);
    }

    internal static string Describe(Exception ex)
    {
        if (ex is HttpRequestException { StatusCode: { } code }) return $"HTTP {(int)code}";
        var text = ex.Message;
        return text.Length > 160 ? text[..160] : text;
    }
}

/// <summary>Records every request through it in the activity log.</summary>
public sealed class ActivityLogHandler : DelegatingHandler
{
    private readonly string _service;
    private readonly ActivityLog _log;

    public ActivityLogHandler(string service, HttpMessageHandler? inner = null, ActivityLog? log = null)
        : base(inner ?? new HttpClientHandler())
    {
        _service = service;
        _log = log ?? ActivityLog.Shared;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.Now;
        var watch = Stopwatch.StartNew();
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            _log.Record(_service, request.Method.Method, request.RequestUri, (int)response.StatusCode, started, watch.Elapsed);
            return response;
        }
        catch (Exception ex)
        {
            _log.Record(_service, request.Method.Method, request.RequestUri, null, started, watch.Elapsed, ActivityLog.Describe(ex));
            throw;
        }
    }
}
