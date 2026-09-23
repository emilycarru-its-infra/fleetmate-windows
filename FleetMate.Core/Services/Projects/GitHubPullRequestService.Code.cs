using System.Text.Json;
using FleetMate.Core.Models.Projects;
using Serilog;

namespace FleetMate.Core.Services.Projects;

/// <summary>
/// The Code section's side of the GitHub pull request service: the wider
/// "everything on my projects" list, single-PR lookup for the inbox, checks,
/// and the review/merge actions.
/// </summary>
public sealed partial class GitHubPullRequestService
{
    /// <summary>
    /// Search strings for the Code list: the queue's three, plus
    /// <c>involves:@me</c> and one <c>user:</c> search per configured owner.
    /// <c>user:</c> matches organizations as well as personal accounts, so one
    /// qualifier covers both config keys.
    /// </summary>
    internal static List<(string Alias, string Query, PullRequestRelation Relation)> CodeSearches(
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

    /// <summary>
    /// Every open PR the operator should see in Code. Never throws — a failure
    /// is returned as a queue error, same contract as the dashboard queue.
    /// </summary>
    public async Task<PullRequestQueue> GetCodePullRequestsAsync(
        IEnumerable<string> owners, int limit = 100, CancellationToken ct = default)
    {
        var searches = CodeSearches(owners);

        // One aliased round trip, as with the queue: the search API is the
        // tightest-limited thing GitHub has.
        var declarations = string.Join(", ", searches.Select(s => $"${s.Alias}: String!"));
        var selections = string.Join("\n", searches.Select(s =>
            $"  {s.Alias}: search(query: ${s.Alias}, type: ISSUE, first: $first) {{ nodes {{ ...PullRequestFields }} }}"));

        var query = $"{PullRequestFragment}\nquery({declarations}, $first: Int!) {{\n{selections}\n}}";

        var variables = new Dictionary<string, object> { ["first"] = limit };
        foreach (var s in searches) variables[s.Alias] = s.Query;

        try
        {
            var data = await _client.ExecuteRawAsync(query, variables, ct);
            var queue = new PullRequestQueue();
            foreach (var s in searches) Absorb(data, s.Alias, s.Relation, queue);

            Log.Information("[github] Code list → {Count} pull requests", queue.PullRequests.Count);
            return queue;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[github] Failed to build the Code pull request list");
            var queue = new PullRequestQueue();
            queue.Errors.Add(new PullRequestQueueError { Source = PullRequestSource.GitHub, Message = ex.Message });
            return queue;
        }
    }

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
