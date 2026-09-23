using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FleetMate.Core.Models.Projects;
using Serilog;

namespace FleetMate.Core.Services.Projects;

/// <summary>
/// The Code section's side of the Azure DevOps service: every active PR in
/// the organization, policy evaluations as checks, and the review actions that
/// mirror the GitHub ones (approve, request changes, comment, draft/ready).
/// Complete and abandon already live beside the queue.
/// </summary>
public partial class AzureDevOpsService
{
    /// <summary>
    /// The operator's own queue plus every active PR in every project, the
    /// latter tagged <see cref="PullRequestRelation.Organization"/>. A PR that
    /// is both keeps both relations. Never throws.
    /// </summary>
    public async Task<PullRequestQueue> GetCodePullRequestsAsync(int topPerProject = 100)
    {
        var queue = await GetMyPullRequestsAsync("active", topPerProject);

        try
        {
            var projects = await ListProjectsAsync();
            var perProject = await Task.WhenAll(projects.Select(async p =>
            {
                var name = p.Name ?? "";
                var prs = await FetchProjectPullRequestsAsync(name, "", "active", topPerProject);
                return prs
                    .Select(pr => MapPullRequest(pr, name, PullRequestRelation.Organization, OrgUrl))
                    .Where(pr => pr != null)
                    .Cast<UnifiedPullRequest>();
            }));

            foreach (var pr in perProject.SelectMany(x => x)) queue.Insert(pr);
            Log.Information("[azdo] Code list → {Count} pull requests", queue.PullRequests.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[azdo] Failed to list organization pull requests");
            queue.Errors.Add(new PullRequestQueueError { Source = PullRequestSource.AzureDevOps, Message = ex.Message });
        }

        return queue;
    }

    // MARK: - Votes, comments, draft

    /// <summary>Vote 10 (approved) as the signed-in identity.</summary>
    public Task<PullRequestActionResult> ApprovePullRequestAsync(
        string repository, int pullRequestId, string? project = null) =>
        VoteAsync(repository, pullRequestId, project, (int)PullRequestReviewVote.Approved);

    /// <summary>
    /// Vote -5 (waiting for author) — Azure DevOps' "request changes". The
    /// reason, when given, is posted as a thread so the author sees why.
    /// </summary>
    public async Task<PullRequestActionResult> RequestChangesPullRequestAsync(
        string repository, int pullRequestId, string? project = null, string? body = null)
    {
        var vote = await VoteAsync(repository, pullRequestId, project, (int)PullRequestReviewVote.WaitingForAuthor);
        if (!vote.Success || string.IsNullOrWhiteSpace(body)) return vote;
        return await CommentPullRequestAsync(repository, pullRequestId, body!, project);
    }

    private async Task<PullRequestActionResult> VoteAsync(
        string repository, int pullRequestId, string? project, int vote)
    {
        if (!await SetAuthorizationAsync())
            return PullRequestActionResult.Failed("Not authenticated to Azure DevOps");

        var identity = await GetCurrentIdentityAsync();
        if (!identity.IsResolved)
            return PullRequestActionResult.Failed("Could not resolve your Azure DevOps identity to vote with.");

        var path = PullRequestSubPath(repository, pullRequestId, project,
            $"reviewers/{Uri.EscapeDataString(identity.Id!)}");

        return await SendAsync(HttpMethod.Put, path, new { vote }, $"vote {vote} on {repository}!{pullRequestId}");
    }

    /// <summary>A new top-level thread, the same thing "Add comment" does on the web.</summary>
    public Task<PullRequestActionResult> CommentPullRequestAsync(
        string repository, int pullRequestId, string body, string? project = null) =>
        SendAuthorizedAsync(HttpMethod.Post,
            PullRequestSubPath(repository, pullRequestId, project, "threads"),
            new
            {
                comments = new[] { new { parentCommentId = 0, content = body, commentType = 1 } },
                status = 1,
            },
            $"comment on {repository}!{pullRequestId}");

    /// <summary>Publish a draft (<paramref name="ready"/> true) or return it to draft.</summary>
    public Task<PullRequestActionResult> SetPullRequestReadyAsync(
        string repository, int pullRequestId, bool ready, string? project = null) =>
        SendAuthorizedAsync(HttpMethod.Patch,
            PullRequestPath(repository, pullRequestId, project),
            new { isDraft = !ready },
            $"set draft={!ready} on {repository}!{pullRequestId}");

    // MARK: - Checks (policy evaluations)

    /// <summary>
    /// Branch policy evaluations for the PR — builds, reviewer minimums, linked
    /// work items, comment resolution. Needs the project GUID for the artifact
    /// id, so the PR is read first.
    /// </summary>
    public async Task<List<PullRequestCheck>> GetPullRequestChecksAsync(
        string repository, int pullRequestId, string? project = null)
    {
        if (!await SetAuthorizationAsync())
            throw new InvalidOperationException("Not authenticated to Azure DevOps");

        var head = await GetJsonAsync(PullRequestPath(repository, pullRequestId, project));
        var projectId = head.TryGetProperty("repository", out var repo)
                        && repo.TryGetProperty("project", out var proj)
            ? Str(proj, "id")
            : null;

        if (string.IsNullOrEmpty(projectId)) return new List<PullRequestCheck>();

        var artifact = $"vstfs:///CodeReview/CodeReviewId/{projectId}/{pullRequestId}";
        var projectSegment = Uri.EscapeDataString(string.IsNullOrWhiteSpace(project) ? projectId! : project!);
        var evaluations = await GetJsonAsync(
            $"{projectSegment}/_apis/policy/evaluations?artifactId={Uri.EscapeDataString(artifact)}&api-version=7.1-preview.1");

        return ParsePolicyEvaluations(evaluations, OrgUrl, project ?? projectId!);
    }

    internal static List<PullRequestCheck> ParsePolicyEvaluations(JsonElement root, string? orgUrl, string project)
    {
        var result = new List<PullRequestCheck>();
        if (!root.TryGetProperty("value", out var list) || list.ValueKind != JsonValueKind.Array) return result;

        foreach (var evaluation in list.EnumerateArray())
        {
            var config = evaluation.TryGetProperty("configuration", out var c) ? c : default;
            if (config.ValueKind != JsonValueKind.Object) continue;

            // Disabled policies still come back; they gate nothing.
            if (config.TryGetProperty("isEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.False)
                continue;

            var settings = config.TryGetProperty("settings", out var s) ? s : default;
            var type = config.TryGetProperty("type", out var t) ? t : default;
            var name = Str(settings, "displayName") ?? Str(type, "displayName") ?? "Policy";

            string? detailsUrl = null;
            if (evaluation.TryGetProperty("context", out var context)
                && context.ValueKind == JsonValueKind.Object
                && context.TryGetProperty("buildId", out var buildId)
                && buildId.ValueKind == JsonValueKind.Number)
            {
                var root2 = (orgUrl ?? "").TrimEnd('/');
                detailsUrl = $"{root2}/{Uri.EscapeDataString(project)}/_build/results?buildId={buildId.GetInt32()}";
            }

            result.Add(new PullRequestCheck
            {
                Name = name,
                State = PolicyState(Str(evaluation, "status")),
                DetailsUrl = detailsUrl,
                IsRequired = config.TryGetProperty("isBlocking", out var blocking) && blocking.ValueKind == JsonValueKind.True,
            });
        }

        return result;
    }

    internal static PullRequestCheckState PolicyState(string? status) => status?.ToLowerInvariant() switch
    {
        "approved" => PullRequestCheckState.Success,
        "rejected" or "broken" => PullRequestCheckState.Failure,
        "notapplicable" => PullRequestCheckState.Skipped,
        _ => PullRequestCheckState.Pending,
    };

    // MARK: - Helpers

    private static string PullRequestSubPath(string repository, int pullRequestId, string? project, string suffix)
    {
        var repoSegment = Uri.EscapeDataString(repository);
        var prefix = string.IsNullOrWhiteSpace(project) ? "" : $"{Uri.EscapeDataString(project)}/";
        return $"{prefix}_apis/git/repositories/{repoSegment}/pullRequests/{pullRequestId}/{suffix}?api-version=7.0";
    }

    private async Task<PullRequestActionResult> SendAuthorizedAsync(
        HttpMethod method, string path, object body, string label)
    {
        if (!await SetAuthorizationAsync())
            return PullRequestActionResult.Failed("Not authenticated to Azure DevOps");
        return await SendAsync(method, path, body, label);
    }

    private async Task<PullRequestActionResult> SendAsync(HttpMethod method, string path, object body, string label)
    {
        Log.Information("[azdo] {Action}", label);

        try
        {
            var content = new StringContent(JsonSerializer.Serialize(body, _jsonOptions), Encoding.UTF8, "application/json");
            var response = await _client.SendAsync(new HttpRequestMessage(method, path) { Content = content });

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Log.Warning("[azdo] {Action} failed: {Status} - {Error}", label, response.StatusCode, error);
                return PullRequestActionResult.Failed($"{(int)response.StatusCode}: {Truncate(error)}");
            }

            return PullRequestActionResult.Ok();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[azdo] {Action} failed", label);
            return PullRequestActionResult.Failed(ex.Message);
        }
    }
}
