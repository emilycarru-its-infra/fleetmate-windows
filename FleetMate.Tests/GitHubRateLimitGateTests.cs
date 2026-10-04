using System.Net;
using System.Text;
using FleetMate.Core.Services.Projects;
using Xunit;

namespace FleetMate.Tests;

// The gates are process-wide, so these tests must not run beside each other.
[Collection("GitHubRateLimitGate")]
public class GitHubRateLimitGateTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private DateTimeOffset _now = T0;

    public GitHubRateLimitGateTests()
    {
        GitHubRateLimitGate.Reset();
        GitHubRateLimitGate.Now = () => _now;
    }

    public void Dispose()
    {
        GitHubRateLimitGate.Reset();
        GitHubRateLimitGate.Now = () => DateTimeOffset.UtcNow;
    }

    private static HttpResponseMessage Response(HttpStatusCode status, int? remaining = null,
        DateTimeOffset? reset = null, int? retryAfter = null, string? resource = null)
    {
        var r = new HttpResponseMessage(status) { Content = new StringContent("{}") };
        if (remaining is { } rem)
        {
            r.Headers.Add("X-RateLimit-Remaining", rem.ToString());
            r.Headers.Add("X-RateLimit-Limit", "5000");
        }
        if (reset is { } at) r.Headers.Add("X-RateLimit-Reset", at.ToUnixTimeSeconds().ToString());
        if (retryAfter is { } s) r.Headers.Add("Retry-After", s.ToString());
        if (resource != null) r.Headers.Add("X-RateLimit-Resource", resource);
        return r;
    }

    [Fact]
    public void OpenGate_PassesAndRecordsTheBudget()
    {
        GitHubRateLimitGate.Record(Response(HttpStatusCode.OK, remaining: 4321, reset: T0.AddMinutes(30)), GitHubRateLimitBucket.Core);

        GitHubRateLimitGate.Check(GitHubRateLimitBucket.Core);
        var status = GitHubRateLimitGate.Status(GitHubRateLimitBucket.Core);
        Assert.Equal(4321, status.Remaining);
        Assert.Equal(T0.AddMinutes(30), status.ResetsAt);
        Assert.Null(status.BlockedUntil);
    }

    [Fact]
    public void SpentBudget_ClosesUntilReset_ThenReopens()
    {
        GitHubRateLimitGate.Record(Response(HttpStatusCode.OK, remaining: 0, reset: T0.AddMinutes(20)), GitHubRateLimitBucket.Core);

        var ex = Assert.Throws<GitHubRateLimitException>(() => GitHubRateLimitGate.Check(GitHubRateLimitBucket.Core));
        Assert.Equal(T0.AddMinutes(20), ex.RetryAt);
        Assert.Contains("rate limit", ex.Message);

        _now = T0.AddMinutes(21);
        GitHubRateLimitGate.Check(GitHubRateLimitBucket.Core);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public void RetryAfter_On403Or429_ClosesForThatLong(HttpStatusCode status)
    {
        GitHubRateLimitGate.Record(Response(status, remaining: 100, reset: T0.AddHours(1), retryAfter: 90), GitHubRateLimitBucket.Core);
        Assert.Equal(T0.AddSeconds(90), GitHubRateLimitGate.OpenAt(GitHubRateLimitBucket.Core));
    }

    [Fact]
    public void TooManyRequests_WithoutRetryAfter_ClosesForAMinute()
    {
        GitHubRateLimitGate.Record(Response(HttpStatusCode.TooManyRequests), GitHubRateLimitBucket.Core);
        Assert.Equal(T0.AddSeconds(60), GitHubRateLimitGate.OpenAt(GitHubRateLimitBucket.Core));
    }

    [Fact]
    public void PlainForbidden_LeavesTheGateOpen()
    {
        GitHubRateLimitGate.Record(Response(HttpStatusCode.Forbidden, remaining: 4000, reset: T0.AddHours(1)), GitHubRateLimitBucket.Core);
        GitHubRateLimitGate.Record(Response(HttpStatusCode.Forbidden), GitHubRateLimitBucket.Core);

        Assert.False(GitHubRateLimitGate.IsTripped(GitHubRateLimitBucket.Core));
    }

    [Fact]
    public void Buckets_AreIndependent()
    {
        GitHubRateLimitGate.Record(Response(HttpStatusCode.OK, remaining: 0, reset: T0.AddMinutes(40)), GitHubRateLimitBucket.GraphQL);

        Assert.Throws<GitHubRateLimitException>(() => GitHubRateLimitGate.Check(GitHubRateLimitBucket.GraphQL));
        GitHubRateLimitGate.Check(GitHubRateLimitBucket.Core);
    }

    [Fact]
    public void GraphQLErrorBody_WaitsForTheReportedReset_OrAMinute()
    {
        GitHubRateLimitGate.TripIfRateLimit("API rate limit exceeded for user", GitHubRateLimitBucket.GraphQL);
        Assert.Equal(T0.AddSeconds(60), GitHubRateLimitGate.OpenAt(GitHubRateLimitBucket.GraphQL));

        GitHubRateLimitGate.Reset();
        GitHubRateLimitGate.Record(Response(HttpStatusCode.OK, remaining: 3, reset: T0.AddMinutes(25)), GitHubRateLimitBucket.GraphQL);
        GitHubRateLimitGate.TripIfRateLimit("API rate limit exceeded for user", GitHubRateLimitBucket.GraphQL);
        Assert.Equal(T0.AddMinutes(25), GitHubRateLimitGate.OpenAt(GitHubRateLimitBucket.GraphQL));
    }

    [Theory]
    [InlineData("Resource limits for this query exceeded")]
    [InlineData("Resource not accessible by integration")]
    [InlineData("Could not resolve to a Repository")]
    public void OtherErrors_DoNotClose(string message)
    {
        GitHubRateLimitGate.TripIfRateLimit(message, GitHubRateLimitBucket.GraphQL);
        Assert.False(GitHubRateLimitGate.IsTripped(GitHubRateLimitBucket.GraphQL));
    }

    [Fact]
    public void Close_NeverShortensALongerWait()
    {
        GitHubRateLimitGate.Record(Response(HttpStatusCode.OK, remaining: 0, reset: T0.AddMinutes(30)), GitHubRateLimitBucket.Core);
        GitHubRateLimitGate.Record(Response(HttpStatusCode.TooManyRequests), GitHubRateLimitBucket.Core);

        Assert.Equal(T0.AddMinutes(30), GitHubRateLimitGate.OpenAt(GitHubRateLimitBucket.Core));
    }

    [Fact]
    public void NextPollDelay_WaitsUntilTheGateReopens()
    {
        var normal = TimeSpan.FromMinutes(5);
        Assert.Equal(normal, GitHubRateLimitGate.NextPollDelay(GitHubRateLimitBucket.Core, normal));

        GitHubRateLimitGate.Record(Response(HttpStatusCode.OK, remaining: 0, reset: T0.AddMinutes(42)), GitHubRateLimitBucket.Core);
        Assert.Equal(TimeSpan.FromMinutes(42) + TimeSpan.FromSeconds(1),
            GitHubRateLimitGate.NextPollDelay(GitHubRateLimitBucket.Core, normal));

        GitHubRateLimitGate.Reset();
        GitHubRateLimitGate.Record(Response(HttpStatusCode.TooManyRequests, retryAfter: 30), GitHubRateLimitBucket.Core);
        Assert.Equal(normal, GitHubRateLimitGate.NextPollDelay(GitHubRateLimitBucket.Core, normal));
    }
}

[Collection("GitHubRateLimitGate")]
public class GitHubHttpHandlerTests : IDisposable
{
    public GitHubHttpHandlerTests() => GitHubRateLimitGate.Reset();
    public void Dispose() => GitHubRateLimitGate.Reset();

    /// <summary>Answers each request from a script and records what it was sent.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(Respond(request));
        }
    }

    private static HttpResponseMessage WithETag(string body, string etag)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        return r;
    }

    [Fact]
    public async Task NotModified_ReturnsTheCachedBodyAsOk()
    {
        var fake = new FakeGitHub();
        var cache = new GitHubETagCache();
        using var client = new HttpClient(new GitHubHttpHandler(cache, fake));
        client.DefaultRequestHeaders.Authorization = new("Bearer", "token-a");

        fake.Respond = _ => WithETag("[1,2,3]", "\"v1\"");
        Assert.Equal("[1,2,3]", await client.GetStringAsync("https://api.github.com/notifications"));

        fake.Respond = req =>
        {
            Assert.Contains(req.Headers.IfNoneMatch, t => t.Tag == "\"v1\"");
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        };
        var second = await client.GetAsync("https://api.github.com/notifications");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("[1,2,3]", await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Cache_IsKeyedByWhoAsked()
    {
        var fake = new FakeGitHub { Respond = _ => WithETag("[]", "\"v1\"") };
        var cache = new GitHubETagCache();
        using var a = new HttpClient(new GitHubHttpHandler(cache, fake));
        a.DefaultRequestHeaders.Authorization = new("Bearer", "token-a");
        using var b = new HttpClient(new GitHubHttpHandler(cache, fake));
        b.DefaultRequestHeaders.Authorization = new("Bearer", "token-b");

        await a.GetStringAsync("https://api.github.com/user");
        await b.GetStringAsync("https://api.github.com/user");

        Assert.Empty(fake.Requests[1].Headers.IfNoneMatch);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public async Task GraphQLAndWrites_AreNeverConditional()
    {
        var fake = new FakeGitHub { Respond = _ => WithETag("{}", "\"v1\"") };
        var cache = new GitHubETagCache();
        using var client = new HttpClient(new GitHubHttpHandler(cache, fake));

        await client.PostAsync("https://api.github.com/graphql", new StringContent("{}"));
        await client.PostAsync("https://api.github.com/repos/o/r/issues", new StringContent("{}"));

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Cache_EvictsTheLeastRecentlyUsed()
    {
        var cache = new GitHubETagCache(capacity: 3);
        GitHubETagCache.Entry E(string tag) => new(tag, false, HttpStatusCode.OK, Array.Empty<byte>(), null);
        cache.Store("a", E("1"));
        cache.Store("b", E("2"));
        cache.Store("c", E("3"));
        Assert.NotNull(cache.Get("a"));     // a is now the most recent
        cache.Store("d", E("4"));           // evicts b

        Assert.Null(cache.Get("b"));
        Assert.NotNull(cache.Get("a"));
        Assert.Equal(3, cache.Count);
        Assert.Equal(GitHubETagCache.DefaultCapacity, 600);
    }

    [Fact]
    public async Task ClosedGate_ShortCircuitsWithoutCallingGitHub()
    {
        var fake = new FakeGitHub
        {
            Respond = _ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.OK);
                r.Headers.Add("X-RateLimit-Remaining", "0");
                r.Headers.Add("X-RateLimit-Limit", "5000");
                r.Headers.Add("X-RateLimit-Reset", DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds().ToString());
                return r;
            }
        };
        using var client = new HttpClient(new GitHubHttpHandler(new GitHubETagCache(), fake));

        await client.GetAsync("https://api.github.com/user");
        await Assert.ThrowsAsync<GitHubRateLimitException>(() => client.GetAsync("https://api.github.com/user"));
        Assert.Single(fake.Requests);

        // GraphQL has its own budget and still goes through.
        await client.PostAsync("https://api.github.com/graphql", new StringContent("{}"));
        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task SearchBudget_DoesNotCloseCore()
    {
        var fake = new FakeGitHub
        {
            Respond = _ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.Forbidden);
                r.Headers.Add("X-RateLimit-Remaining", "0");
                r.Headers.Add("X-RateLimit-Limit", "30");
                r.Headers.Add("X-RateLimit-Reset", DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds().ToString());
                r.Headers.Add("X-RateLimit-Resource", "search");
                return r;
            }
        };
        using var client = new HttpClient(new GitHubHttpHandler(new GitHubETagCache(), fake));

        await client.GetAsync("https://api.github.com/search/issues?q=x");
        Assert.False(GitHubRateLimitGate.IsTripped(GitHubRateLimitBucket.Core));
    }

    [Fact]
    public async Task OtherHosts_PassStraightThrough()
    {
        var fake = new FakeGitHub();
        using var client = new HttpClient(new GitHubHttpHandler(new GitHubETagCache(), fake));
        await client.GetAsync("https://example.com/whatever");
        Assert.Single(fake.Requests);
        Assert.Empty(fake.Requests[0].Headers.IfNoneMatch);
    }
}
