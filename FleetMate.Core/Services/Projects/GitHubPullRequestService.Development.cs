using System.Text.Json;
using FleetMate.Core.Models.Projects;
using Serilog;

namespace FleetMate.Core.Services.Projects;

/// <summary>
/// The Development tab's side of the GitHub pull request service: the wider
/// "everything on my projects" list, single-PR lookup for the inbox, checks,
/// and the review/merge actions.
/// </summary>
public sealed partial class GitHubPullRequestService
{
    /// <summary>
    /// Recent conversation for the activity sidebar, aliased so it can sit
    /// beside the queue fragment's own <c>comments { totalCount }</c>. Kept out
    /// of <see cref="PullRequestFragment"/> so the dashboard queue does not pay
    /// for it.
    /// </summary>
    private const string ActivityFragment = """
        fragment PullRequestActivity on PullRequest {
          recentComments: comments(last: 3) {
            nodes { author { login } body createdAt url }
          }
          recentReviews: reviews(last: 3) {
            nodes { author { login } body state submittedAt url }
          }
          recentThreads: reviewThreads(last: 3) {
            nodes { comments(last: 1) { nodes { author { login } body createdAt url } } }
          }
        }
        """;

    /// <summary>
    /// Search strings for the Development list: the queue's three, plus
    /// <c>involves:@me</c> and one <c>user:</c> search per owner.
    /// <c>user:</c> matches organizations as well as personal accounts, so one
    /// qualifier covers the viewer, their organizations and both config keys.
    /// </summary>
    internal static List<(string Alias, string Query, PullRequestRelation Relation)> DevelopmentSearches(
        IEnumerable<string> owners)
    {
        const string b = "is:pr is:open archived:false";
        var queries = SearchQueries(includeDrafts: true);

        var list = new List<(string, string, PullRequestRelation)>
        {
            ("created", queries.Created, PullRequestRelation.CreatedByMe),
            ("assigned", queries.Assigned, PullRequestRelation.AssignedToMe),
            ("review", queries.Review, PullRequestRelation.AssignedToMe),
            ("involved", $"{b} involves:@me sort:updated-desc", PullRequestRelation.Involved),
        };

        var index = 0;
        foreach (var owner in owners
                     .Where(o => !string.IsNullOrWhiteSpace(o))
                     .Select(o => o.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            list.Add(($"owner{index++}", $"{b} user:{owner} sort:updated-desc", PullRequestRelation.Organization));
        }

        return list;
    }

    /// <summary>The signed-in login and the organizations it belongs to.</summary>
    public async Task<(string? Login, List<string> Organizations)> GetViewerAsync(CancellationToken ct = default)
    {
        var data = await _client.ExecuteRawAsync(
            "query { viewer { login organizations(first: 100) { nodes { login } } } }", ct: ct);

        var viewer = data.TryGetProperty("viewer", out var v) ? v : default;
        var orgs = Nodes(viewer, "organizations")
            .Select(o => Str(o, "login"))
            .Where(o => !string.IsNullOrEmpty(o))
            .Cast<string>()
            .ToList();

        return (Str(viewer, "login"), orgs);
    }

    /// <summary>
    /// Every open PR the operator should see in Development. Never throws: a
    /// failure comes back as a queue error, the same contract as the dashboard
    /// queue.
    /// </summary>
    /// <param name="owners">
    /// Configured owner/organization. The viewer's own login and organization
    /// memberships are added here, so every PR on the operator's projects shows
    /// up without listing each owner in config.
    /// </param>
    public async Task<PullRequestQueue> GetDevelopmentPullRequestsAsync(
        IEnumerable<string> owners, int limit = 50, int ownerLimit = 50, CancellationToken ct = default)
    {
        var queue = new PullRequestQueue();

        try
        {
            var allOwners = owners.ToList();
            try
            {
                var (login, orgs) = await GetViewerAsync(ct);
                if (!string.IsNullOrEmpty(login))
                {
                    queue.ViewerNames.Add(login);
                    allOwners.Add(login);
                }
                allOwners.AddRange(orgs);
            }
            catch (Exception ex)
            {
                // Signed out surfaces on the search below; anything else just
                // narrows the owner list.
                Log.Debug(ex, "[github] viewer lookup failed");
            }

            var searches = DevelopmentSearches(allOwners);
            var batches = Batch(searches);

            // A single aliased query with a search per organization blows
            // GitHub's per-query resource limit once the activity fragment is
            // attached, so the searches go out three to a query, three queries
            // at a time, and a batch that still trips the limit is split in
            // half and retried. One failed batch is reported; the rest render.
            using var gate = new SemaphoreSlim(3);
            var results = await Task.WhenAll(batches.Select(async batch =>
            {
                await gate.WaitAsync(ct);
                try { return (batch, parts: await RunSplittingAsync(batch, limit, ownerLimit, ct), error: (string?)null); }
                catch (Exception ex) { return (batch, parts: new List<(List<(string Alias, string Query, PullRequestRelation Relation)>, JsonElement)>(), error: (string?)ex.Message); }
                finally { gate.Release(); }
            }));

            foreach (var (batch, parts, error) in results)
            {
                if (error == null)
                {
                    foreach (var (part, d) in parts)
                    foreach (var s in part) Absorb(d, s.Alias, s.Relation, queue);
                }
                else
                {
                    Log.Warning("[github] Development search batch failed ({Aliases}): {Error}",
                        string.Join(",", batch.Select(b => b.Alias)), error);
                    queue.Errors.Add(new PullRequestQueueError { Source = PullRequestSource.GitHub, Message = error ?? "search failed" });
                }
            }

            // Every batch failing is one error, not one per batch.
            if (queue.Errors.Count == batches.Count && batches.Count > 1)
            {
                var first = queue.Errors[0];
                queue.Errors.Clear();
                queue.Errors.Add(first);
            }

            Log.Information("[github] Development list → {Count} pull requests across {Owners} owners in {Batches} batches",
                queue.PullRequests.Count, searches.Count(s => s.Relation == PullRequestRelation.Organization), batches.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[github] Failed to build the Development pull request list");
            queue.Errors.Add(new PullRequestQueueError { Source = PullRequestSource.GitHub, Message = ex.Message });
        }

        return queue;
    }

    /// <summary>
    /// Three searches per query, personal ones first. GitHub answered "resource
    /// limits exceeded" both for one query covering a dozen organizations and
    /// for the four personal searches together at first:100 with the activity
    /// fragment; three at first:50 with the trimmed fragment fits, and
    /// <see cref="RunSplittingAsync"/> halves any batch that still does not.
    /// </summary>
    internal static List<List<(string Alias, string Query, PullRequestRelation Relation)>> Batch(
        List<(string Alias, string Query, PullRequestRelation Relation)> searches, int perBatch = 3) =>
        searches
            .Where(s => s.Relation != PullRequestRelation.Organization)
            .Chunk(perBatch)
            .Concat(searches.Where(s => s.Relation == PullRequestRelation.Organization).Chunk(perBatch))
            .Select(c => c.ToList())
            .ToList();

    /// <summary>
    /// Run one batch; on "resource limits exceeded" split it in half and retry
    /// each half, down to a single search, so the list degrades to fewer
    /// searches per query rather than failing.
    /// </summary>
    private async Task<List<(List<(string Alias, string Query, PullRequestRelation Relation)> Batch, JsonElement Data)>> RunSplittingAsync(
        List<(string Alias, string Query, PullRequestRelation Relation)> batch, int limit, int ownerLimit, CancellationToken ct)
    {
        try
        {
            return new() { (batch, await RunSearchBatchAsync(batch, limit, ownerLimit, ct)) };
        }
        catch (Exception ex) when (IsResourceLimit(ex) && batch.Count > 1)
        {
            var half = batch.Count / 2;
            Log.Information("[github] resource limit on {Count} searches; splitting", batch.Count);
            var first = await RunSplittingAsync(batch.Take(half).ToList(), limit, ownerLimit, ct);
            var second = await RunSplittingAsync(batch.Skip(half).ToList(), limit, ownerLimit, ct);
            return first.Concat(second).ToList();
        }
    }

    internal static bool IsResourceLimit(Exception ex) =>
        ex.Message.Contains("Resource limits", StringComparison.OrdinalIgnoreCase);

    private async Task<JsonElement> RunSearchBatchAsync(
        List<(string Alias, string Query, PullRequestRelation Relation)> batch, int limit, int ownerLimit, CancellationToken ct)
    {
        var declarations = string.Join(", ", batch.Select(s => $"${s.Alias}: String!"));
        var selections = string.Join("\n", batch.Select(s =>
            $"  {s.Alias}: search(query: ${s.Alias}, type: ISSUE, first: {(s.Relation == PullRequestRelation.Organization ? ownerLimit : limit)}) " +
            "{ nodes { ...PullRequestFields ...PullRequestActivity } }"));

        var query = $"{PullRequestFragment}\n{ActivityFragment}\nquery({declarations}) {{\n{selections}\n}}";

        var variables = new Dictionary<string, object>();
        foreach (var s in batch) variables[s.Alias] = s.Query;

        return await _client.ExecuteRawAsync(query, variables, ct);
    }

    /// <summary>
    /// Flatten the activity aliases into one list, oldest first. A review with
    /// no body is still an event ("approved"), so it is kept as a system entry
    /// rather than dropped.
    /// </summary>
    internal static List<PullRequestComment> ParseActivity(JsonElement node)
    {
        var result = new List<PullRequestComment>();

        foreach (var c in Nodes(node, "recentComments"))
            result.Add(ActivityComment(c, Str(c, "body"), Str(c, "createdAt"), isSystem: false));

        foreach (var r in Nodes(node, "recentReviews"))
        {
            var body = Str(r, "body");
            var isSystem = string.IsNullOrWhiteSpace(body);
            if (isSystem) body = ReviewVerb(Str(r, "state"));
            if (body == null) continue;
            result.Add(ActivityComment(r, body, Str(r, "submittedAt"), isSystem));
        }

        foreach (var thread in Nodes(node, "recentThreads"))
        foreach (var c in Nodes(thread, "comments"))
            result.Add(ActivityComment(c, Str(c, "body"), Str(c, "createdAt"), isSystem: false));

        return result
            .Where(c => !string.IsNullOrWhiteSpace(c.Body))
            .OrderBy(c => c.Date ?? DateTime.MinValue)
            .ToList();
    }

    private static PullRequestComment ActivityComment(JsonElement node, string? body, string? date, bool isSystem) => new()
    {
        Id = Str(node, "url") ?? Guid.NewGuid().ToString(),
        AuthorName = node.TryGetProperty("author", out var a) ? Str(a, "login") ?? "unknown" : "unknown",
        Body = body ?? string.Empty,
        Date = PullRequestDateParser.Parse(date),
        Url = Str(node, "url"),
        IsSystem = isSystem,
    };

    /// <summary>What an empty-bodied review did; null for PENDING, which nobody else can see yet.</summary>
    private static string? ReviewVerb(string? state) => state?.ToUpperInvariant() switch
    {
        "APPROVED" => "approved",
        "CHANGES_REQUESTED" => "requested changes",
        "COMMENTED" => "reviewed",
        "DISMISSED" => "review dismissed",
        _ => null,
    };

    /// <summary>One pull request by number — what an inbox row opens.</summary>
    public async Task<UnifiedPullRequest?> GetPullRequestAsync(
        string owner, string repo, int number, CancellationToken ct = default)
    {
        var query = $$"""
            {{PullRequestFragment}}
            query($owner: String!, $name: String!, $number: Int!) {
              repository(owner: $owner, name: $name) {
                pullRequest(number: $number) { ...PullRequestFields }
              }
            }
            """;

        var data = await _client.ExecuteRawAsync(query, new { owner, name = repo, number }, ct);
        if (!data.TryGetProperty("repository", out var r) || r.ValueKind != JsonValueKind.Object) return null;
        if (!r.TryGetProperty("pullRequest", out var pr) || pr.ValueKind != JsonValueKind.Object) return null;

        return Map(pr, PullRequestRelation.Involved);
    }

    // MARK: - Checks

    /// <summary>
    /// The head commit's status-check rollup: check runs (Actions and apps) and
    /// legacy commit statuses in one list, each flagged when branch protection
    /// requires it.
    /// </summary>
    public async Task<List<PullRequestCheck>> GetChecksAsync(
        string owner, string repo, int number, CancellationToken ct = default)
    {
        const string query = """
            query($owner: String!, $name: String!, $number: Int!) {
              repository(owner: $owner, name: $name) {
                pullRequest(number: $number) {
                  commits(last: 1) {
                    nodes {
                      commit {
                        statusCheckRollup {
                          contexts(first: 100) {
                            nodes {
                              __typename
                              ... on CheckRun {
                                name status conclusion detailsUrl
                                isRequired(pullRequestNumber: $number)
                              }
                              ... on StatusContext {
                                context state targetUrl
                                isRequired(pullRequestNumber: $number)
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """;

        var data = await _client.ExecuteRawAsync(query, new { owner, name = repo, number }, ct);
        return ParseChecks(data);
    }

    internal static List<PullRequestCheck> ParseChecks(JsonElement data)
    {
        var result = new List<PullRequestCheck>();

        if (!data.TryGetProperty("repository", out var repo) || repo.ValueKind != JsonValueKind.Object) return result;
        if (!repo.TryGetProperty("pullRequest", out var pr) || pr.ValueKind != JsonValueKind.Object) return result;

        foreach (var commitNode in Nodes(pr, "commits"))
        {
            if (!commitNode.TryGetProperty("commit", out var commit)) continue;
            if (!commit.TryGetProperty("statusCheckRollup", out var rollup) || rollup.ValueKind != JsonValueKind.Object)
                continue;

            foreach (var context in Nodes(rollup, "contexts"))
            {
                var isRequired = context.TryGetProperty("isRequired", out var req) && req.ValueKind == JsonValueKind.True;

                if (Str(context, "__typename") == "CheckRun")
                {
                    result.Add(new PullRequestCheck
                    {
                        Name = Str(context, "name") ?? "check",
                        State = CheckRunState(Str(context, "status"), Str(context, "conclusion")),
                        DetailsUrl = Str(context, "detailsUrl"),
                        IsRequired = isRequired,
                    });
                }
                else
                {
                    result.Add(new PullRequestCheck
                    {
                        Name = Str(context, "context") ?? "status",
                        State = StatusContextState(Str(context, "state")),
                        DetailsUrl = Str(context, "targetUrl"),
                        IsRequired = isRequired,
                    });
                }
            }
        }

        return result;
    }

    /// <summary>A check run is pending until COMPLETED; then its conclusion decides.</summary>
    internal static PullRequestCheckState CheckRunState(string? status, string? conclusion)
    {
        if (!string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
            return PullRequestCheckState.Pending;

        return conclusion?.ToUpperInvariant() switch
        {
            "SUCCESS" => PullRequestCheckState.Success,
            "NEUTRAL" => PullRequestCheckState.Neutral,
            "SKIPPED" => PullRequestCheckState.Skipped,
            "FAILURE" or "TIMED_OUT" or "CANCELLED" or "ACTION_REQUIRED" or "STARTUP_FAILURE"
                => PullRequestCheckState.Failure,
            _ => PullRequestCheckState.Neutral,
        };
    }

    internal static PullRequestCheckState StatusContextState(string? state) => state?.ToUpperInvariant() switch
    {
        "SUCCESS" => PullRequestCheckState.Success,
        "FAILURE" or "ERROR" => PullRequestCheckState.Failure,
        _ => PullRequestCheckState.Pending,
    };

    // MARK: - Actions

    private static string PullPath(string owner, string repo, int number) =>
        $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pulls/{number}";

    private static string IssuePath(string owner, string repo, int number) =>
        $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues/{number}";

    public Task<PullRequestActionResult> ApproveAsync(
        string owner, string repo, int number, string? body = null, CancellationToken ct = default) =>
        ReviewAsync(owner, repo, number, "APPROVE", body, ct);

    /// <summary>GitHub requires a body for REQUEST_CHANGES.</summary>
    public Task<PullRequestActionResult> RequestChangesAsync(
        string owner, string repo, int number, string body, CancellationToken ct = default) =>
        ReviewAsync(owner, repo, number, "REQUEST_CHANGES", body, ct);

    private Task<PullRequestActionResult> ReviewAsync(
        string owner, string repo, int number, string reviewEvent, string? body, CancellationToken ct)
    {
        var payload = new Dictionary<string, object> { ["event"] = reviewEvent };
        if (!string.IsNullOrWhiteSpace(body)) payload["body"] = body!;

        return RunAsync($"{reviewEvent} {owner}/{repo}#{number}",
            () => _client.ExecuteRestAsync($"{PullPath(owner, repo, number)}/reviews", "POST", payload, ct));
    }

    /// <summary>A conversation comment (issue comment), not a review — it lands in the same thread the detail view shows.</summary>
    public Task<PullRequestActionResult> CommentAsync(
        string owner, string repo, int number, string body, CancellationToken ct = default) =>
        RunAsync($"comment {owner}/{repo}#{number}",
            () => _client.ExecuteRestAsync($"{IssuePath(owner, repo, number)}/comments", "POST",
                new Dictionary<string, object> { ["body"] = body }, ct));

    public Task<PullRequestActionResult> MergeAsync(
        string owner, string repo, int number, PullRequestMergeMethod method, CancellationToken ct = default) =>
        RunAsync($"merge ({method}) {owner}/{repo}#{number}",
            () => _client.ExecuteRestAsync($"{PullPath(owner, repo, number)}/merge", "PUT",
                new Dictionary<string, object> { ["merge_method"] = method.GitHubValue() }, ct));

    public Task<PullRequestActionResult> CloseAsync(
        string owner, string repo, int number, CancellationToken ct = default) =>
        RunAsync($"close {owner}/{repo}#{number}",
            () => _client.ExecuteRestAsync(PullPath(owner, repo, number), "PATCH",
                new Dictionary<string, object> { ["state"] = "closed" }, ct));

    /// <summary>
    /// Mark ready for review (<paramref name="ready"/> true) or convert back to
    /// draft. GraphQL only — REST has no endpoint for either direction.
    /// </summary>
    public async Task<PullRequestActionResult> SetReadyAsync(
        string nodeId, bool ready, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(nodeId))
            return PullRequestActionResult.Failed("This pull request has no GitHub node id; refresh and try again.");

        var mutation = ready
            ? "mutation($id: ID!) { markPullRequestReadyForReview(input: { pullRequestId: $id }) { pullRequest { isDraft } } }"
            : "mutation($id: ID!) { convertPullRequestToDraft(input: { pullRequestId: $id }) { pullRequest { isDraft } } }";

        try
        {
            await _client.ExecuteRawAsync(mutation, new { id = nodeId }, ct);
            return PullRequestActionResult.Ok();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[github] set ready={Ready} failed for {Node}", ready, nodeId);
            return PullRequestActionResult.Failed(ex.Message);
        }
    }

    private static async Task<PullRequestActionResult> RunAsync(string label, Func<Task<byte[]>> call)
    {
        Log.Information("[github] {Action}", label);
        try
        {
            await call();
            return PullRequestActionResult.Ok();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[github] {Action} failed", label);
            return PullRequestActionResult.Failed(ex.Message);
        }
    }
}
