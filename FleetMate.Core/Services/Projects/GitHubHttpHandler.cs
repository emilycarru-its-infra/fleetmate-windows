using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace FleetMate.Core.Services.Projects;

/// <summary>
/// The HTTP pipeline every GitHub client sends through. For requests to
/// api.github.com it:
/// - fails fast while the request's budget gate is closed;
/// - sends REST GETs with If-None-Match from the shared ETag cache, and turns
///   a 304 back into the cached 200, because GitHub does not count a 304
///   against the hourly budget;
/// - records every response's rate-limit headers on the gate.
/// Requests to any other host pass straight through.
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
        GitHubRateLimitGate.Check(bucket);

        var conditional = request.Method == HttpMethod.Get && bucket == GitHubRateLimitBucket.Core;
        var key = conditional ? CacheKey(request) : null;
        var cached = key != null ? _cache.Get(key) : null;
        if (cached != null) request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(cached.ETag, cached.IsWeak));

        var response = await base.SendAsync(request, ct);
        if (BucketFor(response, bucket) is { } recorded) GitHubRateLimitGate.Record(response, recorded);

        if (response.StatusCode == HttpStatusCode.NotModified && cached != null)
        {
            response.Dispose();
            return cached.ToResponse(request);
        }

        if (key != null && response.IsSuccessStatusCode && response.Headers.ETag is { } etag)
        {
            var body = await response.Content.ReadAsByteArrayAsync(ct);
            var entry = new GitHubETagCache.Entry(etag.Tag, etag.IsWeak, response.StatusCode, body,
                response.Content.Headers.ContentType?.ToString());
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
/// One in-memory cache of ETagged GitHub REST bodies for the whole process,
/// least recently used first out, capped so a long session cannot grow it
/// without bound.
/// </summary>
public sealed class GitHubETagCache
{
    public const int DefaultCapacity = 600;

    public static GitHubETagCache Shared { get; } = new();

    public sealed record Entry(string ETag, bool IsWeak, HttpStatusCode Status, byte[] Body, string? ContentType)
    {
        public HttpResponseMessage ToResponse(HttpRequestMessage request)
        {
            var content = new ByteArrayContent(Body);
            if (ContentType != null) content.Headers.TryAddWithoutValidation("Content-Type", ContentType);
            return new HttpResponseMessage(Status) { Content = content, RequestMessage = request };
        }
    }

    private readonly int _capacity;
    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, Entry Entry)>> _map = new();
    private readonly LinkedList<(string Key, Entry Entry)> _order = new();

    public GitHubETagCache(int capacity = DefaultCapacity) => _capacity = capacity;

    public int Count { get { lock (_lock) return _map.Count; } }

    public Entry? Get(string key)
    {
        lock (_lock)
        {
            if (!_map.TryGetValue(key, out var node)) return null;
            _order.Remove(node);
            _order.AddFirst(node);
            return node.Value.Entry;
        }
    }

    public void Store(string key, Entry entry)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing)) _order.Remove(existing);
            var node = _order.AddFirst((key, entry));
            _map[key] = node;
            while (_map.Count > _capacity && _order.Last is { } last)
            {
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }
}
