using System.Text.Json;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Projects;
using Serilog;

namespace FleetMate.Core.Services.Projects;

/// <summary>
/// The signed-in operator's GitHub notifications — the Code inbox.
///
/// Uses the same person-scoped token chain as every other GitHub client; the
/// <c>repo</c> scope that `gh` and the Device Flow already request covers the
/// notifications API, so no extra consent is needed.
/// </summary>
public sealed class GitHubNotificationService : IDisposable
{
    private readonly GitHubGraphQLClient _client;

    public GitHubNotificationService(GitHubProviderConfig config)
    {
        _client = new GitHubGraphQLClient(config);
    }

    /// <summary>
    /// Recent notification threads, unread first and newest first within each.
    /// Read threads are included (<c>all=true</c>) so a thread marked read on
    /// github.com does not vanish from the list mid-review.
    /// </summary>
    public async Task<List<GitHubNotification>> ListAsync(bool includeRead = true, CancellationToken ct = default)
    {
        var path = $"/notifications?per_page=50&all={(includeRead ? "true" : "false")}";
        var raw = await _client.ExecuteRestAsync(path, ct: ct);
        using var doc = JsonDocument.Parse(raw);

        var result = Parse(doc.RootElement);
        Log.Information("[github] inbox → {Count} notifications ({Unread} unread)",
            result.Count, result.Count(n => n.Unread));
        return result;
    }

    /// <summary>PATCH /notifications/threads/{id} — GitHub answers 205 with no body.</summary>
    public async Task<PullRequestActionResult> MarkReadAsync(string threadId, CancellationToken ct = default) =>
        await RunAsync(() => _client.ExecuteRestAsync(
            $"/notifications/threads/{Uri.EscapeDataString(threadId)}", "PATCH", ct: ct));

    /// <summary>
    /// PUT /notifications with <c>last_read_at</c> = now. GitHub may process a
    /// large backlog asynchronously (202), so callers should mark rows read
    /// locally rather than wait for the next list to agree.
    /// </summary>
    public async Task<PullRequestActionResult> MarkAllReadAsync(CancellationToken ct = default) =>
        await RunAsync(() => _client.ExecuteRestAsync("/notifications", "PUT",
            new Dictionary<string, object>
            {
                ["last_read_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["read"] = true,
            }, ct));

    /// <summary>
    /// DELETE /notifications/threads/{id}/subscription — stop hearing about the
    /// thread. The thread itself stays until read.
    /// </summary>
    public async Task<PullRequestActionResult> UnsubscribeAsync(string threadId, CancellationToken ct = default) =>
        await RunAsync(() => _client.ExecuteRestAsync(
            $"/notifications/threads/{Uri.EscapeDataString(threadId)}/subscription", "DELETE", ct: ct));

    private static async Task<PullRequestActionResult> RunAsync(Func<Task<byte[]>> call)
    {
        try
        {
            await call();
            return PullRequestActionResult.Ok();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[github] notification action failed");
            return PullRequestActionResult.Failed(ex.Message);
        }
    }

    internal static List<GitHubNotification> Parse(JsonElement root)
    {
        var result = new List<GitHubNotification>();
        if (root.ValueKind != JsonValueKind.Array) return result;

        foreach (var node in root.EnumerateArray())
        {
            var subject = node.TryGetProperty("subject", out var s) ? s : default;
            var repo = node.TryGetProperty("repository", out var r) ? r : default;

            var repository = Str(repo, "full_name") ?? string.Empty;
            var subjectUrl = Str(subject, "url");

            result.Add(new GitHubNotification
            {
                Id = Str(node, "id") ?? string.Empty,
                Reason = Str(node, "reason") ?? string.Empty,
                Unread = node.TryGetProperty("unread", out var u) && u.ValueKind == JsonValueKind.True,
                UpdatedAt = PullRequestDateParser.Parse(Str(node, "updated_at")),
                SubjectTitle = Str(subject, "title") ?? "(untitled)",
                SubjectType = Str(subject, "type") ?? string.Empty,
                SubjectApiUrl = subjectUrl,
                WebUrl = WebUrlFor(subjectUrl, Str(repo, "html_url"), repository),
                Repository = repository,
            });
        }

        return Sort(result);
    }

    /// <summary>Unread first, then newest activity first.</summary>
    public static List<GitHubNotification> Sort(IEnumerable<GitHubNotification> notifications) =>
        notifications
            .OrderByDescending(n => n.Unread)
            .ThenByDescending(n => n.UpdatedAt ?? DateTime.MinValue)
            .ToList();

    /// <summary>
    /// Translate a subject's API URL into the page a person would open.
    /// <c>api.github.com/repos/o/r/pulls/12</c> → <c>github.com/o/r/pull/12</c>;
    /// issues keep their path; commits map to <c>/commit/</c>. Anything else
    /// (releases, check suites, discussions) falls back to the repository page,
    /// which is always valid, rather than guessing a URL that 404s.
    /// </summary>
    internal static string WebUrlFor(string? subjectApiUrl, string? repoHtmlUrl, string repository)
    {
        var repoPage = !string.IsNullOrEmpty(repoHtmlUrl)
            ? repoHtmlUrl!
            : $"https://github.com/{repository}";

        if (string.IsNullOrEmpty(subjectApiUrl)) return repoPage;

        const string prefix = "https://api.github.com/repos/";
        if (!subjectApiUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return repoPage;

        var rest = subjectApiUrl[prefix.Length..];
        var parts = rest.Split('/');
        if (parts.Length < 4) return repoPage;

        var ownerRepo = $"{parts[0]}/{parts[1]}";
        var kind = parts[2];
        var id = parts[3];

        return kind switch
        {
            "pulls" => $"https://github.com/{ownerRepo}/pull/{id}",
            "issues" => $"https://github.com/{ownerRepo}/issues/{id}",
            "commits" => $"https://github.com/{ownerRepo}/commit/{id}",
            _ => repoPage,
        };
    }

    private static string? Str(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public void Dispose() => _client.Dispose();
}
