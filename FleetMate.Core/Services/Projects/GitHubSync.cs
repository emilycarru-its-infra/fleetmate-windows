using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FleetMate.Core.Models.Projects;
using Serilog;

namespace FleetMate.Core.Services.Projects;

/// <summary>
/// When GitHub data may be reused and when it must be fetched in full.
///
/// Incremental syncs (conditional REST requests, PR searches narrowed to what
/// changed, commits since the last sync) are the normal path; a full resync
/// happens once a day per search, or when the person presses Refresh, which
/// calls <see cref="RequestFullResync"/>. Everything stored before that moment
/// is treated as stale.
/// </summary>
public static class GitHubSync
{
    /// <summary>How often each search is rebuilt from scratch.</summary>
    public static readonly TimeSpan FullResyncEvery = TimeSpan.FromDays(1);

    /// <summary>
    /// Slack on every "since the last sync" window. GitHub's search index and
    /// commit dates lag; re-reading two minutes of overlap costs little and
    /// keeps an edit made during the last sync from being missed.
    /// </summary>
    public static readonly TimeSpan Skew = TimeSpan.FromMinutes(2);

    private static long _forcedAtTicks = DateTimeOffset.MinValue.UtcTicks;

    /// <summary>For tests: the clock every sync reads.</summary>
    internal static Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>The per-user cache folder: %LOCALAPPDATA%\FleetMate\github-cache.</summary>
    public static string CacheRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FleetMate", "github-cache");

    /// <summary>The person asked for fresh data: everything stored before now is stale.</summary>
    public static void RequestFullResync()
    {
        Interlocked.Exchange(ref _forcedAtTicks, Now().UtcTicks);
        Log.Information("[github] full resync requested");
    }

    /// <summary>True when data stored at <paramref name="storedAt"/> predates the last Refresh.</summary>
    public static bool IsStale(DateTimeOffset storedAt) =>
        storedAt.UtcTicks < Interlocked.Read(ref _forcedAtTicks);

    /// <summary>For tests: forget any requested resync.</summary>
    internal static void ResetForTests() => Interlocked.Exchange(ref _forcedAtTicks, DateTimeOffset.MinValue.UtcTicks);

    /// <summary>A short, file-safe hash of a cache key.</summary>
    internal static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];

    /// <summary>Write a file so a reader never sees half of it: write beside, then swap.</summary>
    internal static void WriteAtomically(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Read a stored JSON file, or null when it is missing or unreadable.</summary>
    internal static T? ReadJson<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[github] unreadable cache file {Path}", path);
            return null;
        }
    }
}

/// <summary>
/// One stored pull request search: its rows by URL, when it last synced and
/// when it was last rebuilt in full.
/// </summary>
public sealed class StoredSearch
{
    public DateTimeOffset LastSync { get; set; }
    public DateTimeOffset LastFull { get; set; }
    /// <summary>The raw GraphQL node of each open pull request, by its URL.</summary>
    public Dictionary<string, string> Rows { get; set; } = new();
}

/// <summary>What to send for one search on this poll.</summary>
public sealed record SearchPlan(string Key, string Query, bool IsFull, StoredSearch? Stored);

/// <summary>
/// Pull request searches that fetch only what changed.
///
/// A search's first run, its daily rebuild and any run after Refresh send the
/// query as written (it carries is:open). Every other run drops is:open and
/// adds updated:&gt;= the last sync less two minutes, so the answer is just
/// what changed, closed and merged pull requests included. The changes merge
/// into the stored rows by URL: an open row is added or replaced, anything
/// else is removed.
/// </summary>
public sealed class GitHubSearchSync
{
    private static readonly Regex IsOpen = new(@"(^|\s)is:open(?=\s|$)", RegexOptions.IgnoreCase);
    private static readonly Regex Spaces = new(@"\s{2,}");

    private readonly string _directory;

    public static GitHubSearchSync Shared { get; } = new(Path.Combine(GitHubSync.CacheRoot, "search"));

    public GitHubSearchSync(string directory) => _directory = directory;

    /// <summary>The query that asks only for what changed since <paramref name="lastSync"/>.</summary>
    public static string IncrementalQuery(string query, DateTimeOffset lastSync)
    {
        var since = (lastSync - GitHubSync.Skew).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var withoutOpen = Spaces.Replace(IsOpen.Replace(query, " "), " ").Trim();
        return $"{withoutOpen} updated:>={since}";
    }

    public static bool NeedsFullResync(StoredSearch? stored, DateTimeOffset now) =>
        stored == null
        || now - stored.LastFull >= GitHubSync.FullResyncEvery
        || GitHubSync.IsStale(stored.LastSync);

    public SearchPlan Plan(string key, string query)
    {
        var stored = GitHubSync.ReadJson<StoredSearch>(PathFor(key));
        return NeedsFullResync(stored, GitHubSync.Now())
            ? new SearchPlan(key, query, true, stored)
            : new SearchPlan(key, IncrementalQuery(query, stored!.LastSync), false, stored);
    }

    /// <summary>
    /// Merge one search's answer into its stored rows, save them, and return
    /// the full set as a search result section (<c>{ "nodes": [...] }</c>) for
    /// the existing parsers.
    /// </summary>
    public JsonElement MergeAndSave(SearchPlan plan, JsonElement section, DateTimeOffset? startedAt = null)
    {
        var now = startedAt ?? GitHubSync.Now();
        var merged = Merge(plan.IsFull ? null : plan.Stored, Nodes(section));
        merged.LastSync = now;
        merged.LastFull = plan.IsFull ? now : plan.Stored!.LastFull;
        try
        {
            GitHubSync.WriteAtomically(PathFor(plan.Key), JsonSerializer.Serialize(merged));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[github] could not store search {Key}", plan.Key);
        }
        return AsSection(merged);
    }

    /// <summary>Upsert open pull requests by URL; remove anything closed or merged.</summary>
    public static StoredSearch Merge(StoredSearch? stored, IEnumerable<JsonElement> changed)
    {
        var rows = stored == null ? new Dictionary<string, string>() : new Dictionary<string, string>(stored.Rows);
        foreach (var node in changed)
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            if (!node.TryGetProperty("url", out var u) || u.GetString() is not { Length: > 0 } url) continue;
            var state = node.TryGetProperty("state", out var s) ? s.GetString() : null;
            if (string.Equals(state, "OPEN", StringComparison.OrdinalIgnoreCase)) rows[url] = node.GetRawText();
            else rows.Remove(url);
        }
        return new StoredSearch { Rows = rows };
    }

    /// <summary>Stored rows as a search result section, newest update first.</summary>
    public static JsonElement AsSection(StoredSearch stored)
    {
        var nodes = new JsonArray();
        foreach (var raw in stored.Rows.Values
                     .Select(r => JsonNode.Parse(r)!)
                     .OrderByDescending(n => n["updatedAt"]?.GetValue<string>() ?? "", StringComparer.Ordinal))
            nodes.Add(raw);
        using var doc = JsonDocument.Parse(new JsonObject { ["nodes"] = nodes }.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static IEnumerable<JsonElement> Nodes(JsonElement section) =>
        section.ValueKind == JsonValueKind.Object
        && section.TryGetProperty("nodes", out var nodes)
        && nodes.ValueKind == JsonValueKind.Array
            ? nodes.EnumerateArray().ToList()
            : Enumerable.Empty<JsonElement>();

    private string PathFor(string key) => Path.Combine(_directory, GitHubSync.Hash(key) + ".json");
}

/// <summary>
/// Recent commits kept between polls: each poll asks only for commits since
/// the last sync, then merges them by oid into what was stored.
/// </summary>
public sealed class GitHubCommitSync
{
    public sealed class StoredCommits
    {
        public DateTimeOffset LastSync { get; set; }
        public DateTimeOffset LastFull { get; set; }
        public List<RepositoryCommits> Repositories { get; set; } = new();
    }

    private readonly string _directory;

    public static GitHubCommitSync Shared { get; } = new(Path.Combine(GitHubSync.CacheRoot, "commits"));

    public GitHubCommitSync(string directory) => _directory = directory;

    public StoredCommits? Load(string key) => GitHubSync.ReadJson<StoredCommits>(PathFor(key));

    /// <summary>
    /// The window start to query: the whole window for a full sync, otherwise
    /// the last sync less the skew (never before the window).
    /// </summary>
    public static (DateTime Since, bool IsFull) Plan(StoredCommits? stored, DateTime windowStart, DateTimeOffset now)
    {
        var full = stored == null
                   || now - stored.LastFull >= GitHubSync.FullResyncEvery
                   || GitHubSync.IsStale(stored.LastSync);
        if (full) return (windowStart, true);
        var since = (stored!.LastSync - GitHubSync.Skew).UtcDateTime;
        return (since > windowStart.ToUniversalTime() ? since : windowStart, false);
    }

    /// <summary>
    /// Merge fetched commits into stored repositories by oid; keep only the
    /// window, newest first, at most <paramref name="perRepo"/> per repository.
    /// </summary>
    public static List<RepositoryCommits> Merge(
        IEnumerable<RepositoryCommits> stored, IEnumerable<RepositoryCommits> fetched, DateTime windowStart, int perRepo)
    {
        var byRepo = new Dictionary<string, (RepositoryCommits Repo, Dictionary<string, PullRequestCommit> Commits)>();
        foreach (var repo in stored.Concat(fetched))
        {
            if (!byRepo.TryGetValue(repo.Id, out var entry))
            {
                entry = (repo, new Dictionary<string, PullRequestCommit>());
                byRepo[repo.Id] = entry;
            }
            else
            {
                // The fetched record carries the current branch name and URL.
                entry = (repo, entry.Commits);
                byRepo[repo.Id] = entry;
            }
            foreach (var c in repo.Commits) entry.Commits[c.Id] = c;
        }

        var windowUtc = windowStart.ToUniversalTime();
        return byRepo.Values
            .Select(e => new RepositoryCommits
            {
                Source = e.Repo.Source,
                Container = e.Repo.Container,
                Repository = e.Repo.Repository,
                RepositoryId = e.Repo.RepositoryId,
                WebUrl = e.Repo.WebUrl,
                DefaultBranch = e.Repo.DefaultBranch,
                Commits = e.Commits.Values
                    .Where(c => c.Date is { } d && d.ToUniversalTime() >= windowUtc)
                    .OrderByDescending(c => c.Date)
                    .Take(perRepo)
                    .ToList(),
            })
            .Where(r => r.Commits.Count > 0)
            .OrderByDescending(r => r.LatestDate)
            .ToList();
    }

    public void Save(string key, StoredCommits commits)
    {
        try
        {
            GitHubSync.WriteAtomically(PathFor(key), JsonSerializer.Serialize(commits));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[github] could not store commits {Key}", key);
        }
    }

    private string PathFor(string key) => Path.Combine(_directory, GitHubSync.Hash(key) + ".json");
}
