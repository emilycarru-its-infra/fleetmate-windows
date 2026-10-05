using System.Text;
using System.Text.Json;
using FleetMate.Core.Models.Projects;
using Serilog;

namespace FleetMate.Core.Services.Projects;

/// <summary>A person a work item can be assigned to: shown by name, sent as the unique name.</summary>
public sealed record DevOpsMember(string DisplayName, string UniqueName);

/// <summary>A Git repository a branch can be created in.</summary>
public sealed record DevOpsRepository(string Id, string Name, string ProjectId, string Project);

/// <summary>
/// Projects tab: the @Me set behind Mine, the option lists behind the card
/// menu (areas, iterations, types, states, members, repositories), and the
/// edits it makes. Matches the macOS app's calls.
/// </summary>
public partial class AzureDevOpsService
{
    /// <summary>States that take a work item off an open list.</summary>
    public static readonly string[] FinishedStates = { "Closed", "Removed", "Done", "Completed", "Resolved" };

    /// <summary>
    /// Ids of the work items assigned to the signed-in user that are still
    /// open — the Mine view. Org-wide, newest change first, at most 1000.
    /// </summary>
    public async Task<HashSet<int>> GetMyOpenWorkItemIdsAsync(int top = 1000)
    {
        if (!await SetAuthorizationAsync()) return new();

        var finished = string.Join(",", FinishedStates.Select(s => $"'{s}'"));
        var wiql = "SELECT [System.Id] FROM WorkItems WHERE [System.AssignedTo] = @Me " +
                   $"AND [System.State] NOT IN ({finished}) ORDER BY [System.ChangedDate] DESC";

        try
        {
            var body = new StringContent(JsonSerializer.Serialize(new { query = wiql }), Encoding.UTF8, "application/json");
            var response = await _client.PostAsync($"_apis/wit/wiql?$top={top}&api-version=7.0", body);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("[azdo] @Me query failed: {Status}", response.StatusCode);
                return new();
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return ParseWiqlIds(doc.RootElement);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[azdo] @Me query failed");
            return new();
        }
    }

    internal static HashSet<int> ParseWiqlIds(JsonElement root)
    {
        var ids = new HashSet<int>();
        if (!root.TryGetProperty("workItems", out var items) || items.ValueKind != JsonValueKind.Array) return ids;
        foreach (var item in items.EnumerateArray())
            if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number) ids.Add(id.GetInt32());
        return ids;
    }

    // MARK: - Option lists

    private string ProjectSegment(string? project) =>
        Uri.EscapeDataString(string.IsNullOrWhiteSpace(project) ? _config.Project ?? "" : project);

    /// <summary>Every area path in the project, flattened ("Projects\Devices\Windows").</summary>
    public Task<List<string>> GetAreaPathsAsync(string? project = null) => ClassificationPathsAsync(project, "areas");

    /// <summary>Every iteration path in the project, flattened.</summary>
    public Task<List<string>> GetIterationPathsAsync(string? project = null) => ClassificationPathsAsync(project, "iterations");

    private async Task<List<string>> ClassificationPathsAsync(string? project, string group)
    {
        if (!await SetAuthorizationAsync()) return new();
        try
        {
            var json = await GetJsonAsync($"{ProjectSegment(project)}/_apis/wit/classificationnodes/{group}?$depth=10&api-version=7.0");
            return FlattenClassification(json);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[azdo] {Group} unavailable", group);
            return new();
        }
    }

    /// <summary>Depth-first path strings, the root included, the way the work item fields store them.</summary>
    internal static List<string> FlattenClassification(JsonElement node, string? parent = null)
    {
        var result = new List<string>();
        var name = Str(node, "name");
        if (string.IsNullOrEmpty(name)) return result;

        var path = parent == null ? name : $"{parent}\\{name}";
        result.Add(path);

        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray())
                result.AddRange(FlattenClassification(child, path));

        return result;
    }

    /// <summary>Work item type names in the project, alphabetical.</summary>
    public async Task<List<string>> GetWorkItemTypeNamesAsync(string? project = null)
    {
        if (!await SetAuthorizationAsync()) return new();
        try
        {
            var json = await GetJsonAsync($"{ProjectSegment(project)}/_apis/wit/workitemtypes?api-version=7.0");
            return Values(json).Select(t => Str(t, "name")).Where(n => !string.IsNullOrEmpty(n)).Cast<string>()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[azdo] work item types unavailable");
            return new();
        }
    }

    /// <summary>The states a type can be in, in workflow order.</summary>
    public async Task<List<string>> GetWorkItemTypeStatesAsync(string type, string? project = null)
    {
        if (!await SetAuthorizationAsync()) return new();
        try
        {
            var json = await GetJsonAsync(
                $"{ProjectSegment(project)}/_apis/wit/workitemtypes/{Uri.EscapeDataString(type)}/states?api-version=7.0");
            return Values(json).Select(t => Str(t, "name")).Where(n => !string.IsNullOrEmpty(n)).Cast<string>().ToList();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[azdo] states unavailable for {Type}", type);
            return new();
        }
    }

    /// <summary>
    /// The project's team members: the team named "{project} Team" when there is
    /// one, otherwise the first team. Groups and service identities are dropped.
    /// </summary>
    public async Task<List<DevOpsMember>> GetTeamMembersAsync(string? project = null)
    {
        if (!await SetAuthorizationAsync()) return new();
        var proj = string.IsNullOrWhiteSpace(project) ? _config.Project ?? "" : project!;

        try
        {
            var teams = Values(await GetJsonAsync($"_apis/projects/{Uri.EscapeDataString(proj)}/teams?api-version=7.0")).ToList();
            var team = teams.FirstOrDefault(t => string.Equals(Str(t, "name"), $"{proj} Team", StringComparison.OrdinalIgnoreCase));
            if (team.ValueKind == JsonValueKind.Undefined && teams.Count > 0) team = teams[0];
            var teamId = Str(team, "id");
            if (string.IsNullOrEmpty(teamId)) return new();

            var members = await GetJsonAsync(
                $"_apis/projects/{Uri.EscapeDataString(proj)}/teams/{teamId}/members?api-version=7.0");
            return ParseMembers(members);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[azdo] team members unavailable for {Project}", proj);
            return new();
        }
    }

    internal static List<DevOpsMember> ParseMembers(JsonElement json) =>
        Values(json)
            .Select(m => m.TryGetProperty("identity", out var id) ? id : m)
            .Where(i => !(i.TryGetProperty("isContainer", out var c) && c.ValueKind == JsonValueKind.True))
            .Select(i => new DevOpsMember(Str(i, "displayName") ?? "", Str(i, "uniqueName") ?? ""))
            .Where(m => m.UniqueName.Contains('@') && !m.UniqueName.StartsWith("vstfs:", StringComparison.OrdinalIgnoreCase))
            .GroupBy(m => m.UniqueName, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The project's enabled Git repositories, alphabetical.</summary>
    public async Task<List<DevOpsRepository>> GetRepositoriesAsync(string? project = null)
    {
        if (!await SetAuthorizationAsync()) return new();
        try
        {
            var json = await GetJsonAsync($"{ProjectSegment(project)}/_apis/git/repositories?api-version=7.0");
            return Values(json)
                .Where(r => !(r.TryGetProperty("isDisabled", out var d) && d.ValueKind == JsonValueKind.True))
                .Select(r =>
                {
                    var proj = r.TryGetProperty("project", out var p) ? p : default;
                    return new DevOpsRepository(Str(r, "id") ?? "", Str(r, "name") ?? "", Str(proj, "id") ?? "", Str(proj, "name") ?? "");
                })
                .Where(r => r.Id.Length > 0)
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[azdo] repositories unavailable");
            return new();
        }
    }

    private static IEnumerable<JsonElement> Values(JsonElement json) =>
        json.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

    // MARK: - Edits

    /// <summary>
    /// Apply JSON Patch operations to one work item, org-level, bypassing
    /// rules the way the macOS app does so a field edit is not refused for an
    /// unrelated required field.
    /// </summary>
    public Task<PullRequestActionResult> PatchWorkItemAsync(int id, IEnumerable<JsonPatchOperation> operations) =>
        SendJsonPatchAsync($"_apis/wit/workitems/{id}?bypassRules=true&api-version=7.0", operations, $"patch work item {id}");

    public Task<PullRequestActionResult> SetWorkItemFieldAsync(int id, string field, object? value, string op = "replace") =>
        PatchWorkItemAsync(id, new[] { new JsonPatchOperation { Op = op, Path = $"/fields/{field}", Value = value } });

    private async Task<PullRequestActionResult> SendJsonPatchAsync(string path, IEnumerable<JsonPatchOperation> operations, string label)
    {
        if (!await SetAuthorizationAsync()) return PullRequestActionResult.Failed("Not authenticated to Azure DevOps");
        try
        {
            var content = new StringContent(JsonSerializer.Serialize(operations, _jsonOptions), Encoding.UTF8, "application/json-patch+json");
            var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, path) { Content = content });
            if (response.IsSuccessStatusCode) return PullRequestActionResult.Ok();

            var error = await response.Content.ReadAsStringAsync();
            Log.Warning("[azdo] {Action} failed: {Status} - {Error}", label, response.StatusCode, error);
            return PullRequestActionResult.Failed($"{(int)response.StatusCode}: {Truncate(error)}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[azdo] {Action} failed", label);
            return PullRequestActionResult.Failed(ex.Message);
        }
    }

    /// <summary>"{id}-{title slug}": lowercase, non-alphanumerics to '-', slug at most 50 characters.</summary>
    public static string BranchNameFor(int id, string title)
    {
        var slug = new string(title.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        if (slug.Length > 50) slug = slug[..50].TrimEnd('-');
        return slug.Length == 0 ? id.ToString() : $"{id}-{slug}";
    }

    /// <summary>
    /// Create a branch off the repository's default branch and link it to the
    /// work item ("Branch" artifact link), as the macOS Create Branch does.
    /// </summary>
    public async Task<PullRequestActionResult> CreateBranchForWorkItemAsync(DevOpsRepository repo, int workItemId, string title)
    {
        if (!await SetAuthorizationAsync()) return PullRequestActionResult.Failed("Not authenticated to Azure DevOps");

        var branch = BranchNameFor(workItemId, title);
        try
        {
            var repoInfo = await GetJsonAsync($"_apis/git/repositories/{repo.Id}?api-version=7.0");
            var defaultRef = Str(repoInfo, "defaultBranch") ?? "refs/heads/main";

            var refs = await GetJsonAsync(
                $"_apis/git/repositories/{repo.Id}/refs?filter={Uri.EscapeDataString(defaultRef["refs/".Length..])}&api-version=7.0");
            var oid = Values(refs).Select(r => (Name: Str(r, "name"), Oid: Str(r, "objectId")))
                .FirstOrDefault(r => r.Name == defaultRef).Oid;
            if (string.IsNullOrEmpty(oid)) return PullRequestActionResult.Failed($"Could not read {defaultRef} in {repo.Name}.");

            var body = new StringContent(JsonSerializer.Serialize(new[]
            {
                new { name = $"refs/heads/{branch}", oldObjectId = new string('0', 40), newObjectId = oid },
            }), Encoding.UTF8, "application/json");
            var created = await _client.PostAsync($"_apis/git/repositories/{repo.Id}/refs?api-version=7.0", body);
            if (!created.IsSuccessStatusCode)
                return PullRequestActionResult.Failed($"{(int)created.StatusCode}: {Truncate(await created.Content.ReadAsStringAsync())}");

            var artifact = $"vstfs:///Git/Ref/{repo.ProjectId}%2F{repo.Id}%2FGB{Uri.EscapeDataString(branch)}";
            return await PatchWorkItemAsync(workItemId, new[]
            {
                new JsonPatchOperation
                {
                    Op = "add",
                    Path = "/relations/-",
                    Value = new { rel = "ArtifactLink", url = artifact, attributes = new { name = "Branch", comment = "Created from FleetMate" } },
                },
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[azdo] create branch {Branch} failed", branch);
            return PullRequestActionResult.Failed(ex.Message);
        }
    }
}
