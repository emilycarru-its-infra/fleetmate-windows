using System.Net;
using System.Text;
using System.Text.Json;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using Xunit;

namespace FleetMate.Tests;

[Collection("GitHubRateLimitGate")]
public class GitHubSyncTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private DateTimeOffset _now = T0;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fm-gh-" + Guid.NewGuid().ToString("N")[..8]);

    public GitHubSyncTests()
    {
        GitHubRateLimitGate.Reset();
        GitHubSync.ResetForTests();
        GitHubSync.Now = () => _now;
    }

    public void Dispose()
    {
        GitHubSync.Now = () => DateTimeOffset.UtcNow;
        GitHubSync.ResetForTests();
        GitHubRateLimitGate.Reset();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ── HTTP cache on disk ──────────────────────────────────────────────

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

    private static HttpResponseMessage Ok(string body, string? etag = null, DateTimeOffset? lastModified = null, int? pollInterval = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (etag != null) r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        if (lastModified != null) r.Content.Headers.LastModified = lastModified;
        if (pollInterval != null) r.Headers.Add("X-Poll-Interval", pollInterval.ToString());
        return r;
    }

    [Fact]
    public async Task DiskCache_SurvivesARelaunch_AndSendsTheStoredValidator()
    {
        var fake = new FakeGitHub { Respond = _ => Ok("[42]", etag: "\"abc\"") };
        using (var first = new HttpClient(new GitHubHttpHandler(new GitHubETagCache(directory: _dir), fake)))
            Assert.Equal("[42]", await first.GetStringAsync("https://api.github.com/user/repos"));

        // A new cache over the same folder is a relaunch.
        fake.Respond = req =>
        {
            Assert.Contains(req.Headers.IfNoneMatch, t => t.Tag == "\"abc\"");
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        };
        using var second = new HttpClient(new GitHubHttpHandler(new GitHubETagCache(directory: _dir), fake));
        var response = await second.GetAsync("https://api.github.com/user/repos");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[42]", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LastModified_IsSentAsIfModifiedSince()
    {
        var stamp = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        var fake = new FakeGitHub { Respond = _ => Ok("[]", lastModified: stamp) };
        using var client = new HttpClient(new GitHubHttpHandler(new GitHubETagCache(directory: _dir), fake));
        await client.GetStringAsync("https://api.github.com/notifications");

        fake.Respond = req =>
        {
            Assert.Equal(stamp, req.Headers.IfModifiedSince);
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        };
        await client.GetStringAsync("https://api.github.com/notifications");
        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task PollInterval_AnswersFromTheCache_UntilItRunsOut_OrRefresh()
    {
        var fake = new FakeGitHub { Respond = _ => Ok("[1]", etag: "\"v1\"", pollInterval: 60) };
        using var client = new HttpClient(new GitHubHttpHandler(new GitHubETagCache(directory: _dir), fake));

        await client.GetStringAsync("https://api.github.com/notifications");
        _now = T0.AddSeconds(30);
        Assert.Equal("[1]", await client.GetStringAsync("https://api.github.com/notifications"));
        Assert.Single(fake.Requests);

        GitHubSync.RequestFullResync();
        fake.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotModified);
        await client.GetStringAsync("https://api.github.com/notifications");
        Assert.Equal(2, fake.Requests.Count);

        _now = T0.AddSeconds(200);
        await client.GetStringAsync("https://api.github.com/notifications");
        Assert.Equal(3, fake.Requests.Count);
    }

    [Fact]
    public void DiskCache_IsCappedOldestFirst()
    {
        var cache = new GitHubETagCache(capacity: 2, directory: _dir, diskCapacity: 3);
        GitHubETagCache.Entry E(string tag) =>
            new(tag, false, null, HttpStatusCode.OK, Encoding.UTF8.GetBytes(tag), null, T0, null);
        for (var i = 0; i < 6; i++)
        {
            cache.Store($"k{i}", E($"\"{i}\""));
            File.SetLastWriteTimeUtc(Directory.GetFiles(_dir).OrderBy(File.GetLastWriteTimeUtc).Last(), T0.UtcDateTime.AddMinutes(i));
        }
        cache.PruneDisk();

        Assert.Equal(3, Directory.GetFiles(_dir, "*.json").Length);
        Assert.Equal(2, cache.Count);
        var reopened = new GitHubETagCache(directory: _dir);
        Assert.NotNull(reopened.Get("k5"));
        Assert.Null(reopened.Get("k0"));
    }

    // ── Pull request searches ───────────────────────────────────────────

    [Fact]
    public void IncrementalQuery_DropsIsOpen_AndAddsUpdatedSinceLessTwoMinutes()
    {
        var q = GitHubSearchSync.IncrementalQuery("is:pr is:open archived:false author:@me sort:updated-desc", T0);
        Assert.Equal("is:pr archived:false author:@me sort:updated-desc updated:>=2026-10-04T11:58:00Z", q);
    }

    private static JsonElement Section(params (string Url, string State, string Updated)[] rows)
    {
        var nodes = rows.Select(r => new { url = r.Url, state = r.State, updatedAt = r.Updated, number = 1 });
        return JsonDocument.Parse(JsonSerializer.Serialize(new { nodes })).RootElement.Clone();
    }

    private static List<string> Urls(JsonElement section) =>
        section.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("url").GetString()!).ToList();

    [Fact]
    public void Search_FirstRunIsFull_ThenIncremental_MergingByUrl()
    {
        var sync = new GitHubSearchSync(_dir);
        const string query = "is:pr is:open author:@me";

        var first = sync.Plan("k", query);
        Assert.True(first.IsFull);
        Assert.Equal(query, first.Query);
        sync.MergeAndSave(first, Section(("u/1", "OPEN", "2026-10-04T10:00:00Z"), ("u/2", "OPEN", "2026-10-04T11:00:00Z")));

        _now = T0.AddMinutes(5);
        var second = sync.Plan("k", query);
        Assert.False(second.IsFull);
        Assert.Contains("updated:>=2026-10-04T11:58:00Z", second.Query);
        Assert.DoesNotContain("is:open", second.Query);

        var merged = sync.MergeAndSave(second, Section(
            ("u/1", "MERGED", "2026-10-04T12:03:00Z"),
            ("u/3", "OPEN", "2026-10-04T12:04:00Z"),
            ("u/2", "OPEN", "2026-10-04T12:02:00Z")));

        Assert.Equal(new[] { "u/3", "u/2" }, Urls(merged));
    }

    [Fact]
    public void Search_ClosedRowsAreRemoved()
    {
        var stored = GitHubSearchSync.Merge(null, Section(("a", "OPEN", "x"), ("b", "OPEN", "x")).GetProperty("nodes").EnumerateArray());
        var after = GitHubSearchSync.Merge(stored, Section(("b", "CLOSED", "y")).GetProperty("nodes").EnumerateArray());
        Assert.Equal(new[] { "a" }, after.Rows.Keys);
    }

    [Fact]
    public void Search_RebuildsInFull_DailyOrAfterRefresh()
    {
        var sync = new GitHubSearchSync(_dir);
        sync.MergeAndSave(sync.Plan("k", "is:open q"), Section(("a", "OPEN", "x")));

        _now = T0.AddHours(2);
        Assert.False(sync.Plan("k", "is:open q").IsFull);

        GitHubSync.RequestFullResync();
        Assert.True(sync.Plan("k", "is:open q").IsFull);

        GitHubSync.ResetForTests();
        _now = T0.AddDays(1);
        Assert.True(sync.Plan("k", "is:open q").IsFull);
    }

    [Fact]
    public void Search_FullResyncReplacesTheStoredRows()
    {
        var sync = new GitHubSearchSync(_dir);
        sync.MergeAndSave(sync.Plan("k", "is:open q"), Section(("a", "OPEN", "x"), ("b", "OPEN", "x")));
        _now = T0.AddMinutes(1);
        GitHubSync.RequestFullResync();
        var merged = sync.MergeAndSave(sync.Plan("k", "is:open q"), Section(("c", "OPEN", "x")));
        Assert.Equal(new[] { "c" }, Urls(merged));
    }

    // ── Recent commits ─────────────────────────────────────────────────

    private static RepositoryCommits Repo(string name, params (string Oid, int MinutesAgo)[] commits) => new()
    {
        Source = PullRequestSource.GitHub,
        Container = "octo",
        Repository = name,
        WebUrl = "https://example.com/" + name,
        Commits = commits.Select(c => new PullRequestCommit { Id = c.Oid, Date = T0.UtcDateTime.AddMinutes(-c.MinutesAgo) }).ToList(),
    };

    [Fact]
    public void Commits_MergeByOid_NewestFirst_WithinTheWindow_Capped()
    {
        var window = T0.UtcDateTime.AddHours(-1);
        var stored = new[] { Repo("app", ("a1", 50), ("a2", 40)), Repo("old", ("o1", 30)) };
        var fetched = new[] { Repo("app", ("a3", 5), ("a2", 40), ("a0", 90)) };

        var merged = GitHubCommitSync.Merge(stored, fetched, window, perRepo: 2);

        Assert.Equal(new[] { "octo/app", "octo/old" }, merged.Select(r => r.DisplayName));
        Assert.Equal(new[] { "a3", "a2" }, merged[0].Commits.Select(c => c.Id));
    }

    [Fact]
    public void Commits_PlanAsksOnlySinceTheLastSync_UntilTheDailyRebuild()
    {
        var window = T0.UtcDateTime.AddDays(-7);
        Assert.Equal((window, true), GitHubCommitSync.Plan(null, window, T0));

        var stored = new GitHubCommitSync.StoredCommits { LastSync = T0, LastFull = T0 };
        var (since, full) = GitHubCommitSync.Plan(stored, window, T0.AddMinutes(10));
        Assert.False(full);
        Assert.Equal(T0.UtcDateTime.AddMinutes(-2), since);

        Assert.True(GitHubCommitSync.Plan(stored, window, T0.AddDays(1)).IsFull);
        _now = T0.AddMinutes(1);
        GitHubSync.RequestFullResync();
        Assert.True(GitHubCommitSync.Plan(stored, window, T0.AddMinutes(1)).IsFull);
    }
}
