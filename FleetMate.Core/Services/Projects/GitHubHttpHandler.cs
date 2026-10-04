using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FleetMate.Core.Services.Projects;

/// <summary>
/// The HTTP pipeline every GitHub client sends through. For requests to
/// api.github.com it:
/// - fails fast while the request's budget gate is closed;
/// - answers a REST GET from the shared cache, without calling GitHub,
///   while the X-Poll-Interval GitHub last gave for it has not run out;
/// - otherwise sends it with If-None-Match / If-Modified-Since from the
///   stored validators, and turns a 304 back into the cached 200, because
///   GitHub does not count a 304 against the hourly budget;
/// - records every response's rate-limit headers on the gate.
/// A Refresh (<see cref="GitHubSync.RequestFullResync"/>) skips the
/// poll-interval shortcut for anything stored before it. Requests to any
/// other host pass straight through.
/// </summary>
public sealed class GitHubHttpHandler : DelegatingHandler
{
    private const string ApiHost = "api.github.com";
    private readonly GitHubETagCache _cache;

    public GitHubHttpHandler(GitHubETagCache? cache = null, HttpMessageHandler? inner = null)
        : base(inner ?? new HttpClientHandler())
    {
        _cache = cache ?? GitHubETagCache.Shared;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!string.Equals(request.RequestUri?.Host, ApiHost, StringComparison.OrdinalIgnoreCase))
            return await base.SendAsync(request, ct);

        var bucket = BucketFor(request.RequestUri!);
        var conditional = request.Method == HttpMethod.Get && bucket == GitHubRateLimitBucket.Core;
        var key = conditional ? CacheKey(request) : null;
        var cached = key != null ? _cache.Get(key) : null;
        var now = GitHubSync.Now();

        // Inside GitHub's poll interval the answer cannot have changed.
        if (cached is { PollUntil: { } pollUntil } && pollUntil > now && !GitHubSync.IsStale(cached.StoredAt))
            return cached.ToResponse(request);

        GitHubRateLimitGate.Check(bucket);

        if (cached?.ETag is { } tag) request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(tag, cached.IsWeak));
        else if (cached?.LastModified is { } since) request.Headers.IfModifiedSince = since;

        var response = await base.SendAsync(request, ct);
        if (BucketFor(response, bucket) is { } recorded) GitHubRateLimitGate.Record(response, recorded);

        if (response.StatusCode == HttpStatusCode.NotModified && cached != null)
        {
            var refreshed = cached with { StoredAt = now, PollUntil = PollUntil(response, now) };
            _cache.Store(key!, refreshed);
            response.Dispose();
            return refreshed.ToResponse(request);
        }

        if (key != null && response.IsSuccessStatusCode
            && (response.Headers.ETag != null || response.Content.Headers.LastModified != null))
        {
            var body = await response.Content.ReadAsByteArrayAsync(ct);
            var entry = new GitHubETagCache.Entry(
                response.Headers.ETag?.Tag, response.Headers.ETag?.IsWeak ?? false,
                response.Content.Headers.LastModified, response.StatusCode, body,
                response.Content.Headers.ContentType?.ToString(), now, PollUntil(response, now));
            _cache.Store(key, entry);
            response.Dispose();
            return entry.ToResponse(request);
        }

        return response;
    }

    /// <summary>GraphQL has its own budget; every other api.github.com path is REST ("core").</summary>
    internal static GitHubRateLimitBucket BucketFor(Uri uri) =>
        uri.AbsolutePath.Equals("/graphql", StringComparison.OrdinalIgnoreCase)
            ? GitHubRateLimitBucket.GraphQL : GitHubRateLimitBucket.Core;

    /// <summary>
    /// GitHub names the budget a response drew on. Search and the other small
    /// budgets are counted apart from core and are not recorded, so a spent
    /// search budget does not close every REST call. Null means "not ours".
    /// </summary>
    internal static GitHubRateLimitBucket? BucketFor(HttpResponseMessage response, GitHubRateLimitBucket fallback)
    {
        if (!response.Headers.TryGetValues("X-RateLimit-Resource", out var values)) return fallback;
        return values.FirstOrDefault()?.ToLowerInvariant() switch
        {
            "graphql" => GitHubRateLimitBucket.GraphQL,
            "core" => GitHubRateLimitBucket.Core,
            _ => null,
        };
    }

    private static DateTimeOffset? PollUntil(HttpResponseMessage response, DateTimeOffset now) =>
        response.Headers.TryGetValues("X-Poll-Interval", out var values)
        && int.TryParse(values.FirstOrDefault(), out var seconds) && seconds > 0
            ? now.AddSeconds(seconds) : null;

    /// <summary>
    /// The URL plus who asked: two accounts can see different bodies at the
    /// same URL. The token is hashed so the cache never holds it.
    /// </summary>
    private static string CacheKey(HttpRequestMessage request)
    {
        var auth = request.Headers.Authorization?.ToString() ?? "";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(auth)), 0, 8);
        return $"{hash} {request.RequestUri!.AbsoluteUri}";
    }
}

/// <summary>
/// The process's GitHub REST response cache, shared by every tab and kept on
/// disk so it survives a relaunch: %LOCALAPPDATA%\FleetMate\github-cache\http,
/// which is per user. Memory holds the ~600 most recently used entries; disk
/// holds up to <see cref="DefaultDiskCapacity"/>, oldest written first out.
/// Files are written beside and swapped in, so a reader never sees half of one.
/// </summary>
public sealed class GitHubETagCache
{
    public const int DefaultCapacity = 600;
    public const int DefaultDiskCapacity = 2000;

    public static GitHubETagCache Shared { get; } = new(directory: Path.Combine(GitHubSync.CacheRoot, "http"));

    public sealed record Entry(
        string? ETag, bool IsWeak, DateTimeOffset? LastModified, HttpStatusCode Status, byte[] Body,
        string? ContentType, DateTimeOffset StoredAt, DateTimeOffset? PollUntil)
    {
        public HttpResponseMessage ToResponse(HttpRequestMessage request)
        {
            var content = new ByteArrayContent(Body);
            if (ContentType != null) content.Headers.TryAddWithoutValidation("Content-Type", ContentType);
            return new HttpResponseMessage(Status) { Content = content, RequestMessage = request };
        }
    }

    private readonly int _capacity;
    private readonly int _diskCapacity;
    private readonly string? _directory;
    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, Entry Entry)>> _map = new();
    private readonly LinkedList<(string Key, Entry Entry)> _order = new();
    private int _writesSincePrune;

    /// <param name="directory">Where to keep entries on disk; null keeps them in memory only.</param>
    public GitHubETagCache(int capacity = DefaultCapacity, string? directory = null, int diskCapacity = DefaultDiskCapacity)
    {
        _capacity = capacity;
        _directory = directory;
        _diskCapacity = diskCapacity;
    }

    public int Count { get { lock (_lock) return _map.Count; } }

    public Entry? Get(string key)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                return node.Value.Entry;
            }
        }

        if (ReadDisk(key) is not { } fromDisk) return null;
        lock (_lock) Remember(key, fromDisk);
        return fromDisk;
    }

    public void Store(string key, Entry entry)
    {
        lock (_lock) Remember(key, entry);
        WriteDisk(key, entry);
    }

    private void Remember(string key, Entry entry)
    {
        if (_map.TryGetValue(key, out var existing)) _order.Remove(existing);
        _map[key] = _order.AddFirst((key, entry));
        while (_map.Count > _capacity && _order.Last is { } last)
        {
            _order.RemoveLast();
            _map.Remove(last.Value.Key);
        }
    }

    private sealed record DiskEntry(
        string Key, string? ETag, bool IsWeak, DateTimeOffset? LastModified, int Status, string Body,
        string? ContentType, DateTimeOffset StoredAt, DateTimeOffset? PollUntil);

    private string? PathFor(string key) => _directory == null ? null : Path.Combine(_directory, GitHubSync.Hash(key) + ".json");

    private Entry? ReadDisk(string key)
    {
        if (PathFor(key) is not { } path) return null;
        var disk = GitHubSync.ReadJson<DiskEntry>(path);
        if (disk == null || disk.Key != key) return null;
        return new Entry(disk.ETag, disk.IsWeak, disk.LastModified, (HttpStatusCode)disk.Status,
            Convert.FromBase64String(disk.Body), disk.ContentType, disk.StoredAt, disk.PollUntil);
    }

    private void WriteDisk(string key, Entry entry)
    {
        if (PathFor(key) is not { } path) return;
        try
        {
            var disk = new DiskEntry(key, entry.ETag, entry.IsWeak, entry.LastModified, (int)entry.Status,
                Convert.ToBase64String(entry.Body), entry.ContentType, entry.StoredAt, entry.PollUntil);
            GitHubSync.WriteAtomically(path, JsonSerializer.Serialize(disk));
            if (Interlocked.Increment(ref _writesSincePrune) >= 25)
            {
                Interlocked.Exchange(ref _writesSincePrune, 0);
                PruneDisk();
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "[github] could not write the response cache");
        }
    }

    /// <summary>Keep the disk cache under its cap, oldest written first out.</summary>
    internal void PruneDisk()
    {
        if (_directory == null || !Directory.Exists(_directory)) return;
        var files = new DirectoryInfo(_directory).GetFiles("*.json");
        if (files.Length <= _diskCapacity) return;
        foreach (var old in files.OrderBy(f => f.LastWriteTimeUtc).Take(files.Length - _diskCapacity))
        {
            try { old.Delete(); } catch (IOException) { }
        }
    }
}
