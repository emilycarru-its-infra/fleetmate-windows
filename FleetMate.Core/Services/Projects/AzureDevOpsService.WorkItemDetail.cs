using System.Globalization;
using System.Text;
using System.Text.Json;
using FleetMate.Core.Models.Projects;
using Serilog;

namespace FleetMate.Core.Services.Projects;

/// <summary>
/// The work item sidebar (macOS AzDoTaskSidebarView): the full item with its
/// relations, the discussion with reactions, Link to Code, and Save All.
/// </summary>
public partial class AzureDevOpsService
{
    // MARK: - Detail

    /// <summary>One work item with relations, org-level so any project's item opens.</summary>
    public async Task<WorkItemDetail?> GetWorkItemDetailAsync(int id)
    {
        if (!await SetAuthorizationAsync()) return null;
        try
        {
            var json = await GetJsonAsync($"_apis/wit/workitems/{id}?$expand=relations&api-version=7.0");
            return ParseWorkItemDetail(json, Root);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[azdo] work item {Id} detail unavailable", id);
            return null;
        }
    }

    internal static WorkItemDetail ParseWorkItemDetail(JsonElement json, string orgRoot)
    {
        var f = json.TryGetProperty("fields", out var fields) ? fields : default;
        var id = json.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt32() : 0;
        var project = Str(f, "System.TeamProject") ?? "";
        var assigned = f.ValueKind == JsonValueKind.Object && f.TryGetProperty("System.AssignedTo", out var a) ? a : default;

        var relations = new List<WorkItemRelationInfo>();
        if (json.TryGetProperty("relations", out var rels) && rels.ValueKind == JsonValueKind.Array)
        {
            foreach (var rel in rels.EnumerateArray())
            {
                var type = Str(rel, "rel") ?? "";
                var attributes = rel.TryGetProperty("attributes", out var attr) ? attr : default;
                relations.Add(new WorkItemRelationInfo(RelationKind(type), type, Str(rel, "url") ?? "",
                    Str(attributes, "name"), Str(attributes, "comment")));
            }
        }

        var web = json.TryGetProperty("_links", out var links) && links.TryGetProperty("html", out var html)
            ? Str(html, "href")
            : null;

        return new WorkItemDetail
        {
            Id = id,
            Title = Str(f, "System.Title") ?? "",
            State = Str(f, "System.State") ?? "",
            Reason = Str(f, "System.Reason"),
            Type = Str(f, "System.WorkItemType") ?? "",
            Project = project,
            AssignedTo = Str(assigned, "displayName"),
            AssignedToUniqueName = Str(assigned, "uniqueName"),
            AreaPath = Str(f, "System.AreaPath"),
            IterationPath = Str(f, "System.IterationPath"),
            Tags = Str(f, "System.Tags"),
            Priority = (int?)Num(f, "Microsoft.VSTS.Common.Priority"),
            DueDate = Date(f, "Microsoft.VSTS.Scheduling.DueDate"),
            Description = Str(f, "System.Description"),
            ReproSteps = Str(f, "Microsoft.VSTS.TCM.ReproSteps"),
            AcceptanceCriteria = Str(f, "Microsoft.VSTS.Common.AcceptanceCriteria"),
            OriginalEstimate = Num(f, "Microsoft.VSTS.Scheduling.OriginalEstimate"),
            RemainingWork = Num(f, "Microsoft.VSTS.Scheduling.RemainingWork"),
            CompletedWork = Num(f, "Microsoft.VSTS.Scheduling.CompletedWork"),
            BoardColumn = Str(f, "System.BoardColumn"),
            Created = Date(f, "System.CreatedDate"),
            CreatedBy = Person(f, "System.CreatedBy"),
            Changed = Date(f, "System.ChangedDate"),
            ChangedBy = Person(f, "System.ChangedBy"),
            StateChanged = Date(f, "Microsoft.VSTS.Common.StateChangeDate"),
            Resolved = Date(f, "Microsoft.VSTS.Common.ResolvedDate"),
            ResolvedBy = Person(f, "Microsoft.VSTS.Common.ResolvedBy"),
            Closed = Date(f, "Microsoft.VSTS.Common.ClosedDate"),
            ClosedBy = Person(f, "Microsoft.VSTS.Common.ClosedBy"),
            WebUrl = web ?? $"{orgRoot}/{Uri.EscapeDataString(project)}/_workitems/edit/{id}",
            Relations = relations,
        };
    }

    internal static WorkItemRelationKind RelationKind(string rel) => rel switch
    {
        "System.LinkTypes.Hierarchy-Reverse" => WorkItemRelationKind.Parent,
        "System.LinkTypes.Hierarchy-Forward" => WorkItemRelationKind.Child,
        "System.LinkTypes.Related" => WorkItemRelationKind.Related,
        "System.LinkTypes.Dependency-Reverse" => WorkItemRelationKind.Predecessor,
        "System.LinkTypes.Dependency-Forward" => WorkItemRelationKind.Successor,
        "ArtifactLink" => WorkItemRelationKind.Artifact,
        _ => WorkItemRelationKind.Other,
    };

    private static double? Num(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var v)
        && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : null;

    private static DateTime? Date(JsonElement element, string property) => PullRequestDateParser.Parse(Str(element, property));

    private static string? Person(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var p)
            ? Str(p, "displayName")
            : null;

    // MARK: - Artifacts

    public static string CommitArtifactUri(string projectId, string repositoryId, string sha) =>
        $"vstfs:///Git/Commit/{projectId}%2F{repositoryId}%2F{sha}";

    public static string BranchArtifactUri(string projectId, string repositoryId, string branch) =>
        $"vstfs:///Git/Ref/{projectId}%2F{repositoryId}%2FGB{Uri.EscapeDataString(branch)}";

    public static string PullRequestArtifactUri(string projectId, string repositoryId, int pullRequestId) =>
        $"vstfs:///Git/PullRequestId/{projectId}%2F{repositoryId}%2F{pullRequestId}";

    /// <summary>The artifact's parts after "vstfs:///Git/{Kind}/", decoded once: project, repository, key.</summary>
    private static (string Kind, string[] Parts) ArtifactParts(string url)
    {
        const string prefix = "vstfs:///Git/";
        if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return ("", Array.Empty<string>());
        var rest = url[prefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash < 0) return (rest, Array.Empty<string>());
        return (rest[..slash], rest[(slash + 1)..].Replace("%2f", "%2F").Split("%2F", 3, StringSplitOptions.None));
    }

    /// <summary>Commit, Pull Request or Branch, from a vstfs artifact URI.</summary>
    public static string ArtifactKind(string url) => ArtifactParts(url).Kind.ToLowerInvariant() switch
    {
        "commit" => "Commit",
        "pullrequestid" => "Pull Request",
        "ref" => "Branch",
        _ => "Artifact",
    };

    /// <summary>A readable label without a network call: the branch name, a short SHA, or "PR #12".</summary>
    public static string ArtifactLabel(string url)
    {
        var (kind, parts) = ArtifactParts(url);
        if (parts.Length < 3) return ArtifactKind(url);
        var key = parts[2];
        return kind.ToLowerInvariant() switch
        {
            "commit" => key.Length > 7 ? key[..7] : key,
            "pullrequestid" => $"PR #{key}",
            "ref" => BranchFromRefKey(key),
            _ => key,
        };
    }

    private static string BranchFromRefKey(string key)
    {
        var name = Uri.UnescapeDataString(key);
        if (name.StartsWith("GB", StringComparison.Ordinal)) name = name[2..];
        name = Uri.UnescapeDataString(name);
        return name.StartsWith("refs/heads/", StringComparison.Ordinal) ? name["refs/heads/".Length..] : name;
    }

    /// <summary>The web page for a code artifact, or null when the URI is not one.</summary>
    public static string? ArtifactWebUrl(string url, string orgRoot)
    {
        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return url;
        var (kind, parts) = ArtifactParts(url);
        if (parts.Length < 3) return null;
        var (project, repo, key) = (parts[0], parts[1], parts[2]);
        return kind.ToLowerInvariant() switch
        {
            "commit" => $"{orgRoot}/{project}/_git/{repo}/commit/{key}",
            "pullrequestid" => $"{orgRoot}/{project}/_git/{repo}/pullrequest/{key}",
            "ref" => $"{orgRoot}/{project}/_git/{repo}?version={Uri.EscapeDataString("GB" + BranchFromRefKey(key))}",
            _ => null,
        };
    }

    public string ArtifactWebUrl(string url) => ArtifactWebUrl(url, Root) ?? "";

    /// <summary>Link a commit, branch or pull request to the work item.</summary>
    public Task<PullRequestActionResult> AddArtifactLinkAsync(int workItemId, string artifactUri, string linkName) =>
        PatchWorkItemAsync(workItemId, new[]
        {
            new JsonPatchOperation
            {
                Op = "add",
                Path = "/relations/-",
                Value = new { rel = "ArtifactLink", url = artifactUri, attributes = new { name = linkName } },
            },
        });

    // MARK: - Link to Code candidates

    public async Task<List<CodeLinkCandidate>> GetBranchesAsync(DevOpsRepository repo)
    {
        if (!await SetAuthorizationAsync()) return new();
        var json = await GetJsonAsync($"{ProjectSegment(repo.Project)}/_apis/git/repositories/{repo.Id}/refs?filter=heads/&api-version=7.0");
        return Values(json)
            .Select(r => Str(r, "name"))
            .Where(n => n?.StartsWith("refs/heads/") == true)
            .Select(n => n!["refs/heads/".Length..])
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new CodeLinkCandidate("branch", n, n, null))
            .ToList();
    }

    public async Task<List<CodeLinkCandidate>> GetCommitsAsync(DevOpsRepository repo, int top = 200)
    {
        if (!await SetAuthorizationAsync()) return new();
        var json = await GetJsonAsync($"{ProjectSegment(repo.Project)}/_apis/git/repositories/{repo.Id}/commits?searchCriteria.$top={top}&api-version=7.0");
        return ParseCommitRefs(json, "").Select(CommitCandidate).ToList();
    }

    /// <summary>One commit by SHA (pasted into the search box), or null when there is none.</summary>
    public async Task<CodeLinkCandidate?> GetCommitAsync(DevOpsRepository repo, string sha)
    {
        if (!await SetAuthorizationAsync()) return null;
        try
        {
            var json = await GetJsonAsync($"{ProjectSegment(repo.Project)}/_apis/git/repositories/{repo.Id}/commits/{Uri.EscapeDataString(sha)}?api-version=7.0");
            var id = Str(json, "commitId");
            if (string.IsNullOrEmpty(id)) return null;
            var author = json.TryGetProperty("author", out var a) ? Str(a, "name") : null;
            return CommitCandidate(new PullRequestCommit { Id = id, Message = Str(json, "comment") ?? "", AuthorName = author });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[azdo] commit {Sha} not found in {Repo}", sha, repo.Name);
            return null;
        }
    }

    private static CodeLinkCandidate CommitCandidate(PullRequestCommit c) =>
        new("commit", c.Id, c.Message.Split('\n')[0],
            string.Join(" · ", new[] { c.Id[..Math.Min(7, c.Id.Length)], c.AuthorName }.Where(x => !string.IsNullOrEmpty(x))));

    public async Task<List<CodeLinkCandidate>> GetRepositoryPullRequestsAsync(DevOpsRepository repo, int top = 50)
    {
        if (!await SetAuthorizationAsync()) return new();
        var json = await GetJsonAsync(
            $"{ProjectSegment(repo.Project)}/_apis/git/repositories/{repo.Id}/pullrequests?searchCriteria.status=all&$top={top}&api-version=7.0");
        return Values(json).Select(pr =>
        {
            var id = pr.TryGetProperty("pullRequestId", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0;
            var detail = string.Join(" · ", new[]
            {
                $"#{id}", ShortBranchName(Str(pr, "sourceRefName")), CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Str(pr, "status") ?? ""),
            }.Where(x => !string.IsNullOrEmpty(x)));
            return new CodeLinkCandidate("pr", id.ToString(), Str(pr, "title") ?? "Untitled pull request", detail);
        }).Where(c => c.Key != "0").ToList();
    }

    // MARK: - Discussion

    private string CommentsPath(int workItemId, string project) =>
        $"{ProjectSegment(project)}/_apis/wit/workitems/{workItemId}/comments";

    /// <summary>Comments with reactions, newest first like the macOS feed.</summary>
    public async Task<List<WorkItemDiscussionComment>> GetDiscussionAsync(int workItemId, string project)
    {
        if (!await SetAuthorizationAsync()) return new();
        try
        {
            var json = await GetJsonAsync($"{CommentsPath(workItemId, project)}?$expand=all&order=desc&api-version=7.1-preview.4");
            return ParseDiscussion(json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[azdo] comments for {Id} unavailable", workItemId);
            return new();
        }
    }

    internal static List<WorkItemDiscussionComment> ParseDiscussion(JsonElement json)
    {
        if (!json.TryGetProperty("comments", out var list) || list.ValueKind != JsonValueKind.Array) return new();
        return list.EnumerateArray()
            .Where(c => !(c.TryGetProperty("isDeleted", out var d) && d.ValueKind == JsonValueKind.True))
            .Select(ParseComment)
            .OrderByDescending(c => c.Created ?? DateTime.MinValue)
            .ToList();
    }

    internal static WorkItemDiscussionComment ParseComment(JsonElement c)
    {
        var by = c.TryGetProperty("createdBy", out var b) ? b : default;
        var reactions = new List<CommentReaction>();
        if (c.TryGetProperty("reactions", out var r) && r.ValueKind == JsonValueKind.Array)
        {
            foreach (var reaction in r.EnumerateArray())
            {
                var type = Str(reaction, "type");
                var count = reaction.TryGetProperty("count", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0;
                var engaged = reaction.TryGetProperty("isCurrentUserEngaged", out var e) && e.ValueKind == JsonValueKind.True;
                if (!string.IsNullOrEmpty(type) && count > 0) reactions.Add(new CommentReaction(type!, count, engaged));
            }
        }

        return new WorkItemDiscussionComment
        {
            Id = c.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt32() : 0,
            Text = Str(c, "renderedText") is { Length: > 0 } rendered ? rendered : Str(c, "text") ?? "",
            Author = Str(by, "displayName"),
            AuthorUniqueName = Str(by, "uniqueName"),
            AuthorId = Str(by, "id"),
            Created = PullRequestDateParser.Parse(Str(c, "createdDate")),
            Reactions = reactions,
        };
    }

    public async Task<WorkItemDiscussionComment?> AddDiscussionCommentAsync(int workItemId, string project, string text) =>
        await SendCommentAsync(HttpMethod.Post, $"{CommentsPath(workItemId, project)}?api-version=7.1-preview.4", text);

    public async Task<WorkItemDiscussionComment?> UpdateDiscussionCommentAsync(int workItemId, string project, int commentId, string text) =>
        await SendCommentAsync(HttpMethod.Patch, $"{CommentsPath(workItemId, project)}/{commentId}?api-version=7.1-preview.4", text);

    private async Task<WorkItemDiscussionComment?> SendCommentAsync(HttpMethod method, string path, string text)
    {
        if (!await SetAuthorizationAsync()) return null;
        var body = new StringContent(JsonSerializer.Serialize(new { text }), Encoding.UTF8, "application/json");
        var response = await _client.SendAsync(new HttpRequestMessage(method, path) { Content = body });
        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("[azdo] comment {Method} failed: {Status}", method, response.StatusCode);
            return null;
        }
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return ParseComment(doc.RootElement);
    }

    public Task<bool> DeleteDiscussionCommentAsync(int workItemId, string project, int commentId) =>
        SendNoBodyAsync(HttpMethod.Delete, $"{CommentsPath(workItemId, project)}/{commentId}?api-version=7.1-preview.4");

    /// <summary>PUT adds the signed-in user's reaction; DELETE takes it back.</summary>
    public Task<bool> SetCommentReactionAsync(int workItemId, string project, int commentId, string type, bool add) =>
        SendNoBodyAsync(add ? HttpMethod.Put : HttpMethod.Delete,
            $"{CommentsPath(workItemId, project)}/{commentId}/reactions/{type}?api-version=7.1-preview.1");

    private async Task<bool> SendNoBodyAsync(HttpMethod method, string path)
    {
        if (!await SetAuthorizationAsync()) return false;
        try
        {
            var response = await _client.SendAsync(new HttpRequestMessage(method, path));
            if (!response.IsSuccessStatusCode) Log.Warning("[azdo] {Method} {Path} failed: {Status}", method, path, response.StatusCode);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[azdo] {Method} {Path} failed", method, path);
            return false;
        }
    }

    // MARK: - Save All

    /// <summary>Patch operations for the fields the edit changed, and nothing else.</summary>
    internal static List<JsonPatchOperation> EditOperations(WorkItemDetail item, WorkItemEdit edit)
    {
        var ops = new List<JsonPatchOperation>();
        void Set(string field, object? value, string op = "replace") =>
            ops.Add(new JsonPatchOperation { Op = op, Path = $"/fields/{field}", Value = value });
        static bool Changed(string? before, string? after) => after != null && !string.Equals(before ?? "", after, StringComparison.Ordinal);

        if (Changed(item.Title, edit.Title?.Trim())) Set("System.Title", edit.Title!.Trim());
        if (Changed(item.State, edit.State)) Set("System.State", edit.State, "add");
        if (Changed(item.Type, edit.Type)) Set("System.WorkItemType", edit.Type, "add");
        if (edit.AssignedToUniqueName != null
            && !string.Equals(item.AssignedToUniqueName ?? "", edit.AssignedToUniqueName, StringComparison.OrdinalIgnoreCase))
            Set("System.AssignedTo", edit.AssignedToUniqueName);
        if (edit.Priority != null && edit.Priority != item.Priority) Set("Microsoft.VSTS.Common.Priority", edit.Priority);
        if (Changed(item.AreaPath, edit.AreaPath)) Set("System.AreaPath", edit.AreaPath);
        if (Changed(item.IterationPath, edit.IterationPath)) Set("System.IterationPath", edit.IterationPath);
        if (edit.Tags != null && !TagsEqual(item.Tags, edit.Tags)) Set("System.Tags", edit.Tags);
        if (Changed(item.Description, edit.Description)) Set("System.Description", edit.Description);
        if (Changed(item.ReproSteps, edit.ReproSteps)) Set("Microsoft.VSTS.TCM.ReproSteps", edit.ReproSteps);
        if (Changed(item.AcceptanceCriteria, edit.AcceptanceCriteria)) Set("Microsoft.VSTS.Common.AcceptanceCriteria", edit.AcceptanceCriteria);
        if (edit.OriginalEstimate != item.OriginalEstimate) Effort("Microsoft.VSTS.Scheduling.OriginalEstimate", item.OriginalEstimate, edit.OriginalEstimate);
        if (edit.RemainingWork != item.RemainingWork) Effort("Microsoft.VSTS.Scheduling.RemainingWork", item.RemainingWork, edit.RemainingWork);
        if (edit.CompletedWork != item.CompletedWork) Effort("Microsoft.VSTS.Scheduling.CompletedWork", item.CompletedWork, edit.CompletedWork);

        if (edit.DueDate?.Date != item.DueDate?.Date)
        {
            if (edit.DueDate is { } due) Set("Microsoft.VSTS.Scheduling.DueDate", due.ToString("yyyy-MM-dd"), "add");
            else ops.Add(new JsonPatchOperation { Op = "remove", Path = "/fields/Microsoft.VSTS.Scheduling.DueDate" });
        }

        return ops;

        void Effort(string field, double? before, double? after)
        {
            if (after is { } value) Set(field, value, "add");
            else if (before != null) ops.Add(new JsonPatchOperation { Op = "remove", Path = $"/fields/{field}" });
        }
    }

    private static bool TagsEqual(string? a, string b)
    {
        static IEnumerable<string> Split(string? s) =>
            (s ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => t.ToLowerInvariant()).OrderBy(t => t);
        return Split(a).SequenceEqual(Split(b));
    }

    public Task<PullRequestActionResult> SaveWorkItemAsync(WorkItemDetail item, WorkItemEdit edit)
    {
        var ops = EditOperations(item, edit);
        return ops.Count == 0 ? Task.FromResult(PullRequestActionResult.Ok()) : PatchWorkItemAsync(item.Id, ops);
    }
}
