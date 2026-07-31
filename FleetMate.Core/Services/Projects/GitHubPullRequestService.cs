using System.Text.Json;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Projects;
using Serilog;

namespace FleetMate.Core.Services.Projects;

/// <summary>
/// Builds the signed-in user's GitHub pull request queue across <em>every</em>
/// repository the token can see — not just the configured org/repo.
///
/// Auth reuses <see cref="GitHubTokenSource"/> (gh CLI → credential store →
/// Device Flow → env → deprecated config token), so an SSO-authorized `gh`
/// login is all that is needed.
/// </summary>
public sealed class GitHubPullRequestService : IDisposable
{
    private readonly GitHubGraphQLClient _client;

    public GitHubPullRequestService(GitHubProviderConfig config)
    {
        _client = new GitHubGraphQLClient(config);
    }

    /// <summary>Whether a usable GitHub token is reachable, without surfacing a login UI.</summary>
    public async Task<bool> IsAuthenticatedAsync(CancellationToken ct = default)
    {
        try { return await _client.AuthenticateAsync(ct); }
        catch { return false; }
    }

    /// <summary>
    /// The three <c>search</c> queries the queue is built from. <c>@me</c>
    /// resolves server-side to the token's owner, so no viewer lookup is needed.
    /// </summary>
    internal static (string Created, string Assigned, string Review) SearchQueries(bool includeDrafts)
    {
        const string b = "is:pr is:open archived:false";
        var draftFilter = includeDrafts ? "" : " -is:draft";

        return (
            Created: $"{b}{draftFilter} author:@me sort:updated-desc",
            // Drafts assigned to you are still your problem, so the filter only
            // applies to the ones you opened.
            Assigned: $"{b} assignee:@me sort:updated-desc",
            Review: $"{b} review-requested:@me sort:updated-desc");
    }

    private const string PullRequestFragment = """
        fragment PullRequestFields on PullRequest {
          number
          title
          url
          isDraft
          state
          createdAt
          updatedAt
          mergeable
          baseRefName
          headRefName
          author { login }
          repository { name owner { login } }
          comments { totalCount }
          reviewThreads { totalCount }
          reviewRequests(first: 10) {
            nodes {
              requestedReviewer {
                ... on User { login name }
                ... on Team { name }
              }
            }
          }
          latestReviews(first: 10) {
            nodes { state author { login } }
          }
        }
        """;

    /// <summary>
    /// Fetch the queue. Never throws — a failure surfaces as a
    /// <see cref="PullRequestQueueError"/> alongside whatever else succeeded, so
    /// one dead provider cannot blank a queue the rest of which is fine.
    /// </summary>
    /// <param name="limit">
    /// Max results per search. GitHub's search API caps a single page at 100;
    /// beyond that the queue would need cursor paging.
    /// </param>
    public async Task<PullRequestQueue> GetMyPullRequestsAsync(
        int limit = 100, bool includeDrafts = true, CancellationToken ct = default)
    {
        var queries = SearchQueries(includeDrafts);

        // One aliased round trip rather than three: the search API is heavily
        // rate-limited, and three sequential calls is the difference between a
        // queue that paints immediately and one that visibly fills in.
        var query = $$"""
            {{PullRequestFragment}}
            query($created: String!, $assigned: String!, $review: String!, $first: Int!) {
              created: search(query: $created, type: ISSUE, first: $first) {
                nodes { ...PullRequestFields }
              }
              assigned: search(query: $assigned, type: ISSUE, first: $first) {
                nodes { ...PullRequestFields }
              }
              review: search(query: $review, type: ISSUE, first: $first) {
                nodes { ...PullRequestFields }
              }
            }
            """;

        try
        {
            var data = await _client.ExecuteRawAsync(query, new
            {
                created = queries.Created,
                assigned = queries.Assigned,
                review = queries.Review,
                first = limit,
            }, ct);

            var queue = new PullRequestQueue();
            Absorb(data, "created", PullRequestRelation.CreatedByMe, queue);
            Absorb(data, "assigned", PullRequestRelation.AssignedToMe, queue);
            Absorb(data, "review", PullRequestRelation.AssignedToMe, queue);

            Log.Information("[github] PR queue → {Count} pull requests", queue.PullRequests.Count);
            return queue;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[github] Failed to build the pull request queue");
            var queue = new PullRequestQueue();
            queue.Errors.Add(new PullRequestQueueError
            {
                Source = PullRequestSource.GitHub,
                Message = ex.Message,
            });
            return queue;
        }
    }

    private static void Absorb(
        JsonElement data, string key, PullRequestRelation relation, PullRequestQueue queue)
    {
        if (!data.TryGetProperty(key, out var section)) return;
        if (!section.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array) return;

        foreach (var node in nodes.EnumerateArray())
        {
            // search(type: ISSUE) also returns issues, which have no
            // number/repository pair we can use — skip anything incomplete
            // rather than rendering a row that goes nowhere.
            var pr = Map(node, relation);
            if (pr != null) queue.Insert(pr);
        }
    }

    internal static UnifiedPullRequest? Map(JsonElement node, PullRequestRelation relation)
    {
        if (!node.TryGetProperty("number", out var numberEl) || numberEl.ValueKind != JsonValueKind.Number)
            return null;
        if (!node.TryGetProperty("repository", out var repo) || repo.ValueKind != JsonValueKind.Object)
            return null;

        var repoName = Str(repo, "name");
        var owner = repo.TryGetProperty("owner", out var ownerEl) ? Str(ownerEl, "login") : null;
        var url = Str(node, "url");

        if (string.IsNullOrEmpty(repoName) || string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(url))
            return null;

        var isDraft = node.TryGetProperty("isDraft", out var d) && d.ValueKind == JsonValueKind.True;
        var state = Str(node, "state")?.ToUpperInvariant() switch
        {
            "MERGED" => PullRequestState.Merged,
            "CLOSED" => PullRequestState.Closed,
            _ => isDraft ? PullRequestState.Draft : PullRequestState.Open,
        };

        // Reviewers = everyone a review is requested from, overlaid with whoever
        // has already left one, so the vote pips reflect current standing rather
        // than just who was asked.
        var reviewers = new Dictionary<string, PullRequestReviewer>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in Nodes(node, "reviewRequests"))
        {
            if (!entry.TryGetProperty("requestedReviewer", out var reviewer)) continue;
            var name = Str(reviewer, "name") ?? Str(reviewer, "login");
            if (string.IsNullOrEmpty(name)) continue;

            reviewers[name] = new PullRequestReviewer
            {
                Id = name,
                DisplayName = name,
                Vote = PullRequestReviewVote.NoVote,
                IsRequired = true,
            };
        }

        foreach (var review in Nodes(node, "latestReviews"))
        {
            if (!review.TryGetProperty("author", out var author)) continue;
            var login = Str(author, "login");
            if (string.IsNullOrEmpty(login)) continue;

            reviewers[login] = new PullRequestReviewer
            {
                Id = login,
                DisplayName = login,
                Vote = PullRequestReviewVoteExtensions.FromGitHub(Str(review, "state")),
                IsRequired = reviewers.TryGetValue(login, out var prior) && prior.IsRequired,
            };
        }

        var commentCount = TotalCount(node, "comments") + TotalCount(node, "reviewThreads");

        return new UnifiedPullRequest
        {
            Source = PullRequestSource.GitHub,
            Number = numberEl.GetInt32(),
            Title = Str(node, "title") ?? "(untitled)",
            AuthorName = node.TryGetProperty("author", out var a) ? Str(a, "login") ?? "Unknown" : "Unknown",
            Container = owner,
            Repository = repoName,
            SourceBranch = Str(node, "headRefName") ?? string.Empty,
            TargetBranch = Str(node, "baseRefName") ?? string.Empty,
            CreatedAt = PullRequestDateParser.Parse(Str(node, "createdAt")),
            UpdatedAt = PullRequestDateParser.Parse(Str(node, "updatedAt")),
            State = state,
            HasConflicts = string.Equals(Str(node, "mergeable"), "CONFLICTING", StringComparison.OrdinalIgnoreCase),
            CommentCount = commentCount,
            Reviewers = reviewers.Values.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
            WebUrl = url,
            Relations = new HashSet<PullRequestRelation> { relation },
        };
    }

    // MARK: - JSON helpers

    private static string? Str(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static IEnumerable<JsonElement> Nodes(JsonElement node, string property)
    {
        if (!node.TryGetProperty(property, out var container)) yield break;
        if (!container.TryGetProperty("nodes", out var nodes)) yield break;
        if (nodes.ValueKind != JsonValueKind.Array) yield break;

        foreach (var child in nodes.EnumerateArray()) yield return child;
    }

    private static int TotalCount(JsonElement node, string property) =>
        node.TryGetProperty(property, out var container)
        && container.TryGetProperty("totalCount", out var count)
        && count.ValueKind == JsonValueKind.Number
            ? count.GetInt32()
            : 0;

    public void Dispose() => _client.Dispose();
}
