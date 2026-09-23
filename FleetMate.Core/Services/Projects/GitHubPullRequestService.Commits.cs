using System.Text.Json;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Shared;
using Serilog;

namespace FleetMate.Core.Services.Projects;

/// <summary>Development › Commits on GitHub: recent default-branch commits per repository, and one commit's diff.</summary>
public sealed partial class GitHubPullRequestService
{
    private const string RepositoryCommitsSelection = """
        nodes {
          ... on Repository {
            name url owner { login }
            defaultBranchRef {
              name
              target {
                ... on Commit {
                  history(first: $perRepo, since: $since) {
                    nodes { oid message messageHeadline committedDate url author { name user { login } } }
                  }
                }
              }
            }
          }
        }
        """;

    /// <summary>
    /// Repositories under each owner pushed to since <paramref name="since"/>,
    /// with their latest default-branch commits, newest activity first. Owners
    /// go three to a query (the same resource-limit budget as the PR searches);
    /// a failing batch is logged and skipped rather than sinking the list.
    /// </summary>
    public async Task<List<RepositoryCommits>> GetRecentCommitsAsync(
        IEnumerable<string> owners, DateTime since, int perRepo = 10, int reposPerOwner = 30, CancellationToken ct = default)
    {
        var ownerList = owners
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select(o => o.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ownerList.Count == 0) return new();

        var sinceText = since.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
        var sinceDay = sinceText[..10];

        var batches = await Task.WhenAll(ownerList.Chunk(3).Select(async chunk =>
        {
            try
            {
                return await CommitsBatchAsync(chunk, sinceText, sinceDay, perRepo, reposPerOwner, ct);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[github] recent commits failed for {Owners}", string.Join(",", chunk));
                return new List<RepositoryCommits>();
            }
        }));

        // The same repo can surface under two owners (a fork the user owns and
        // the org original); keep the first.
        var result = batches
            .SelectMany(b => b)
            .GroupBy(r => r.Id)
            .Select(g => g.First())
            .OrderByDescending(r => r.LatestDate)
            .ToList();

        Log.Information("[github] recent commits → {Count} repositories across {Owners} owners", result.Count, ownerList.Count);
        return result;
    }

    private async Task<List<RepositoryCommits>> CommitsBatchAsync(
        string[] owners, string sinceText, string sinceDay, int perRepo, int reposPerOwner, CancellationToken ct)
    {
        var declarations = new List<string> { "$since: GitTimestamp!", "$perRepo: Int!", "$repos: Int!" };
        var variables = new Dictionary<string, object> { ["since"] = sinceText, ["perRepo"] = perRepo, ["repos"] = reposPerOwner };
        var selections = new List<string>();

        for (var i = 0; i < owners.Length; i++)
        {
            declarations.Add($"$owner{i}: String!");
            variables[$"owner{i}"] = $"user:{owners[i]} pushed:>={sinceDay} sort:updated-desc";
            selections.Add($"owner{i}: search(query: $owner{i}, type: REPOSITORY, first: $repos) {{ {RepositoryCommitsSelection} }}");
        }

        var query = $"query({string.Join(", ", declarations)}) {{\n{string.Join("\n", selections)}\n}}";
        var data = await _client.ExecuteRawAsync(query, variables, ct);

        var result = new List<RepositoryCommits>();
        for (var i = 0; i < owners.Length; i++)
        {
            if (!data.TryGetProperty($"owner{i}", out var section)) continue;
            result.AddRange(ParseRepositoryCommits(section));
        }
        return result;
    }

    internal static List<RepositoryCommits> ParseRepositoryCommits(JsonElement section)
    {
        var result = new List<RepositoryCommits>();

        foreach (var node in SelfNodes(section))
        {
            var name = Str(node, "name");
            var url = Str(node, "url");
            var owner = node.TryGetProperty("owner", out var o) ? Str(o, "login") : null;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url) || string.IsNullOrEmpty(owner)) continue;

            var branch = node.TryGetProperty("defaultBranchRef", out var b) && b.ValueKind == JsonValueKind.Object ? b : default;
            var target = branch.ValueKind == JsonValueKind.Object && branch.TryGetProperty("target", out var t) ? t : default;

            var commits = Nodes(target, "history")
                .Select(c =>
                {
                    var author = c.TryGetProperty("author", out var a) ? a : default;
                    var login = author.ValueKind == JsonValueKind.Object && author.TryGetProperty("user", out var u) ? Str(u, "login") : null;
                    return new PullRequestCommit
                    {
                        Id = Str(c, "oid") ?? "",
                        Message = Str(c, "message") ?? Str(c, "messageHeadline") ?? "",
                        AuthorName = login ?? Str(author, "name"),
                        Date = PullRequestDateParser.Parse(Str(c, "committedDate")),
                        Url = Str(c, "url"),
                    };
                })
                .Where(c => c.Id.Length > 0)
                .ToList();

            if (commits.Count == 0) continue;

            result.Add(new RepositoryCommits
            {
                Source = PullRequestSource.GitHub,
                Container = owner!,
                Repository = name!,
                WebUrl = url!,
                DefaultBranch = Str(branch, "name"),
                Commits = commits,
            });
        }

        return result;
    }

    /// <summary>The <c>nodes</c> array directly under a search result.</summary>
    private static IEnumerable<JsonElement> SelfNodes(JsonElement section)
    {
        if (section.ValueKind != JsonValueKind.Object) yield break;
        if (!section.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array) yield break;
        foreach (var n in nodes.EnumerateArray()) yield return n;
    }

    /// <summary>Full message and per-file diff for one commit (REST, which hands back ready-made hunks).</summary>
    public async Task<CommitDetail> GetCommitDetailAsync(string owner, string repo, string sha, CancellationToken ct = default)
    {
        var raw = await _client.ExecuteRestAsync(
            $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/commits/{Uri.EscapeDataString(sha)}", ct: ct);
        using var doc = JsonDocument.Parse(raw);
        return ParseCommitDetail(doc.RootElement);
    }

    internal static CommitDetail ParseCommitDetail(JsonElement root)
    {
        var files = new List<DiffFile>();
        var changes = new List<CommitChange>();

        if (root.TryGetProperty("files", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in list.EnumerateArray())
            {
                var name = Str(f, "filename") ?? "(unknown)";
                var patch = Str(f, "patch");
                files.Add(string.IsNullOrEmpty(patch)
                    ? new DiffFile { OldPath = name, NewPath = name }
                    : DiffParser.ParseBareHunks(patch, name));
                changes.Add(new CommitChange { Path = name, ChangeType = (Str(f, "status") ?? "edit").ToLowerInvariant() });
            }
        }

        var stats = root.TryGetProperty("stats", out var s) ? s : default;
        int Stat(string key) => stats.ValueKind == JsonValueKind.Object && stats.TryGetProperty(key, out var v)
                                && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

        return new CommitDetail
        {
            Message = root.TryGetProperty("commit", out var c) ? Str(c, "message") ?? "" : "",
            Files = files,
            Changes = changes,
            Additions = Stat("additions"),
            Deletions = Stat("deletions"),
            // GitHub caps a commit's file list at 300.
            Truncated = files.Count >= 300,
        };
    }
}
