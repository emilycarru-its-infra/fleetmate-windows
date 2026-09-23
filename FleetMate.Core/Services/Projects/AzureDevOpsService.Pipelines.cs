using System.Net.Http.Headers;
using System.Text.Json;
using FleetMate.Core.Models.Projects;
using Serilog;

namespace FleetMate.Core.Services.Projects;

/// <summary>
/// Development › Commits and Pipelines on Azure DevOps: recent default-branch
/// commits per repository, one commit's change list, build runs across every
/// project, their task logs, rerun and cancel.
/// </summary>
public partial class AzureDevOpsService
{
    private string Root => (OrgUrl ?? "").TrimEnd('/');

    // MARK: - Commits

    /// <summary>
    /// Every enabled repository in every project with commits since
    /// <paramref name="since"/>, newest activity first. Never throws; an
    /// unreadable project or repository is skipped.
    /// </summary>
    public async Task<List<RepositoryCommits>> GetRecentCommitsAsync(DateTime since, int perRepo = 10)
    {
        if (!await SetAuthorizationAsync()) return new();

        var from = Uri.EscapeDataString(since.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));
        var projects = await ListProjectsAsync();
        using var gate = new SemaphoreSlim(8);

        var repos = (await Task.WhenAll(projects.Select(async p =>
        {
            var project = p.Name ?? "";
            try
            {
                var json = await GetJsonAsync($"{Uri.EscapeDataString(project)}/_apis/git/repositories?api-version=7.0");
                return ParseRepositories(json, project);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[azdo] repositories unavailable for {Project}", project);
                return new List<(string Project, string Id, string Name, string? Branch, string Web)>();
            }
        }))).SelectMany(x => x).ToList();

        var result = await Task.WhenAll(repos.Select(async r =>
        {
            await gate.WaitAsync();
            try
            {
                var json = await GetJsonAsync(
                    $"{Uri.EscapeDataString(r.Project)}/_apis/git/repositories/{r.Id}/commits" +
                    $"?searchCriteria.fromDate={from}&searchCriteria.$top={perRepo}&api-version=7.0");

                var commits = ParseCommitRefs(json, $"{r.Web}/commit/");
                return commits.Count == 0 ? null : new RepositoryCommits
                {
                    Source = PullRequestSource.AzureDevOps,
                    Container = r.Project,
                    Repository = r.Name,
                    RepositoryId = r.Id,
                    WebUrl = r.Web,
                    DefaultBranch = r.Branch,
                    Commits = commits,
                };
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[azdo] commits unavailable for {Project}/{Repo}", r.Project, r.Name);
                return null;
            }
            finally
            {
                gate.Release();
            }
        }));

        var list = result.Where(r => r != null).Cast<RepositoryCommits>()
            .OrderByDescending(r => r.LatestDate).ToList();
        Log.Information("[azdo] recent commits → {Count} repositories", list.Count);
        return list;
    }

    internal List<(string Project, string Id, string Name, string? Branch, string Web)> ParseRepositories(JsonElement json, string project)
    {
        var result = new List<(string, string, string, string?, string)>();
        if (!json.TryGetProperty("value", out var list) || list.ValueKind != JsonValueKind.Array) return result;

        foreach (var repo in list.EnumerateArray())
        {
            // A disabled repository refuses every read.
            if (repo.TryGetProperty("isDisabled", out var d) && d.ValueKind == JsonValueKind.True) continue;

            var id = Str(repo, "id");
            var name = Str(repo, "name");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) continue;

            var branch = Str(repo, "defaultBranch");
            if (branch?.StartsWith("refs/heads/") == true) branch = branch["refs/heads/".Length..];

            result.Add((project, id!, name!, branch,
                Str(repo, "webUrl") ?? $"{Root}/{Uri.EscapeDataString(project)}/_git/{Uri.EscapeDataString(name!)}"));
        }

        return result;
    }

    internal static List<PullRequestCommit> ParseCommitRefs(JsonElement json, string commitUrlPrefix)
    {
        var result = new List<PullRequestCommit>();
        if (!json.TryGetProperty("value", out var list) || list.ValueKind != JsonValueKind.Array) return result;

        foreach (var node in list.EnumerateArray())
        {
            var id = Str(node, "commitId");
            if (string.IsNullOrEmpty(id)) continue;
            var author = node.TryGetProperty("author", out var a) ? a : default;

            result.Add(new PullRequestCommit
            {
                Id = id!,
                Message = Str(node, "comment") ?? "",
                AuthorName = Str(author, "name"),
                Date = PullRequestDateParser.Parse(Str(author, "date")),
                Url = commitUrlPrefix + id,
            });
        }

        return result.OrderByDescending(c => c.Date ?? DateTime.MinValue).ToList();
    }

    /// <summary>
    /// Full message and change list for one commit. Azure DevOps returns paths
    /// and change types but no patch, so there is no diff here.
    /// </summary>
    public async Task<CommitDetail> GetCommitDetailAsync(string repositoryId, string sha, string? project = null)
    {
        if (!await SetAuthorizationAsync()) throw new InvalidOperationException("Not authenticated to Azure DevOps");

        var prefix = string.IsNullOrWhiteSpace(project) ? "" : $"{Uri.EscapeDataString(project)}/";
        var basePath = $"{prefix}_apis/git/repositories/{Uri.EscapeDataString(repositoryId)}/commits/{Uri.EscapeDataString(sha)}";

        var commitTask = GetJsonAsync($"{basePath}?api-version=7.0");
        var changesTask = GetJsonAsync($"{basePath}/changes?top=500&api-version=7.0");
        await Task.WhenAll(commitTask, changesTask);

        var changes = ParseCommitChanges(await changesTask);
        return new CommitDetail
        {
            Message = Str(await commitTask, "comment") ?? "",
            Changes = changes,
            Truncated = changes.Count >= 500,
        };
    }

    internal static List<CommitChange> ParseCommitChanges(JsonElement json)
    {
        var result = new List<CommitChange>();
        if (!json.TryGetProperty("changes", out var list) || list.ValueKind != JsonValueKind.Array) return result;

        foreach (var change in list.EnumerateArray())
        {
            var item = change.TryGetProperty("item", out var i) ? i : default;
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("isFolder", out var f) && f.ValueKind == JsonValueKind.True)
                continue;
            var path = Str(item, "path");
            if (string.IsNullOrEmpty(path)) continue;

            result.Add(new CommitChange
            {
                Path = path!.TrimStart('/'),
                ChangeType = (Str(change, "changeType") ?? "edit").ToLowerInvariant(),
            });
        }

        return result;
    }

    // MARK: - Pipelines

    /// <summary>Build runs queued since <paramref name="since"/> in every project, newest first. Never throws.</summary>
    public async Task<List<PipelineRun>> GetRecentPipelineRunsAsync(DateTime since, int topPerProject = 50)
    {
        if (!await SetAuthorizationAsync()) return new();

        var minTime = Uri.EscapeDataString(since.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));
        var projects = await ListProjectsAsync();

        var perProject = await Task.WhenAll(projects.Select(async p =>
        {
            var project = p.Name ?? "";
            try
            {
                var json = await GetJsonAsync(
                    $"{Uri.EscapeDataString(project)}/_apis/build/builds?minTime={minTime}&$top={topPerProject}" +
                    "&queryOrder=queueTimeDescending&api-version=7.0");
                return ParseBuilds(json, project, Root);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[azdo] builds unavailable for {Project}", project);
                return new List<PipelineRun>();
            }
        }));

        var runs = perProject.SelectMany(x => x).OrderByDescending(r => r.SortDate).ToList();
        Log.Information("[azdo] pipeline runs → {Count}", runs.Count);
        return runs;
    }

    internal static List<PipelineRun> ParseBuilds(JsonElement json, string project, string root)
    {
        var result = new List<PipelineRun>();
        if (!json.TryGetProperty("value", out var list) || list.ValueKind != JsonValueKind.Array) return result;

        foreach (var build in list.EnumerateArray())
        {
            if (!build.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number) continue;

            var definition = build.TryGetProperty("definition", out var d) ? d : default;
            var branch = Str(build, "sourceBranch");
            if (branch?.StartsWith("refs/heads/") == true) branch = branch["refs/heads/".Length..];

            var web = build.TryGetProperty("_links", out var links) && links.TryGetProperty("web", out var w) ? Str(w, "href") : null;

            result.Add(new PipelineRun
            {
                Source = PullRequestSource.AzureDevOps,
                Container = project,
                Repository = build.TryGetProperty("repository", out var r) ? Str(r, "name") : null,
                PipelineName = Str(definition, "name") ?? "pipeline",
                PipelineId = definition.ValueKind == JsonValueKind.Object && definition.TryGetProperty("id", out var did)
                             && did.ValueKind == JsonValueKind.Number ? did.GetInt32() : null,
                RunId = id.GetInt64(),
                RunNumber = Str(build, "buildNumber") ?? id.GetInt64().ToString(),
                Status = PipelineRunStatusExtensions.FromAzureDevOps(Str(build, "status"), Str(build, "result")),
                Branch = branch,
                CommitSha = Str(build, "sourceVersion"),
                TriggeredBy = build.TryGetProperty("requestedFor", out var rf) ? Str(rf, "displayName") : null,
                StartedAt = PullRequestDateParser.Parse(Str(build, "startTime") ?? Str(build, "queueTime")),
                FinishedAt = PullRequestDateParser.Parse(Str(build, "finishTime")),
                WebUrl = web ?? $"{root}/{Uri.EscapeDataString(project)}/_build/results?buildId={id.GetInt64()}",
            });
        }

        return result;
    }

    /// <summary>
    /// One section per Task record in the build timeline, in execution order,
    /// each read as JSON lines and capped to its tail.
    /// </summary>
    public async Task<PipelineRunLog> GetPipelineRunLogAsync(string project, long buildId)
    {
        if (!await SetAuthorizationAsync()) throw new InvalidOperationException("Not authenticated to Azure DevOps");

        var p = Uri.EscapeDataString(project);
        var timeline = await GetJsonAsync($"{p}/_apis/build/builds/{buildId}/timeline?api-version=7.0");
        var tasks = ParseTimelineTasks(timeline);

        using var gate = new SemaphoreSlim(6);
        var truncated = false;

        var sections = await Task.WhenAll(tasks.Select(async t =>
        {
            if (t.LogId is not { } logId)
                return new PipelineRunLog.Section { Id = t.Id, Name = t.Name, Status = t.Status, Text = "(no log)" };

            await gate.WaitAsync();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{p}/_apis/build/builds/{buildId}/logs/{logId}?api-version=7.0");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var response = await _client.SendAsync(request);
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

                var lines = doc.RootElement.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array
                    ? v.EnumerateArray().Select(l => l.GetString() ?? "")
                    : Enumerable.Empty<string>();

                var (tail, cut) = PipelineRunLog.Tail(string.Join("\n", lines));
                if (cut) truncated = true;
                return new PipelineRunLog.Section { Id = t.Id, Name = t.Name, Status = t.Status, Text = tail };
            }
            catch (Exception ex)
            {
                return new PipelineRunLog.Section { Id = t.Id, Name = t.Name, Status = t.Status, Text = $"(log unavailable: {ex.Message})" };
            }
            finally
            {
                gate.Release();
            }
        }));

        return new PipelineRunLog { RunId = buildId, Sections = sections.ToList(), Truncated = truncated };
    }

    internal static List<(string Id, string Name, PipelineRunStatus Status, int? LogId)> ParseTimelineTasks(JsonElement timeline)
    {
        var result = new List<(string Id, string Name, PipelineRunStatus Status, int? LogId, DateTime Start, int Order)>();
        if (!timeline.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
            return new();

        foreach (var record in records.EnumerateArray())
        {
            if (!string.Equals(Str(record, "type"), "Task", StringComparison.OrdinalIgnoreCase)) continue;

            int? logId = record.TryGetProperty("log", out var log) && log.ValueKind == JsonValueKind.Object
                         && log.TryGetProperty("id", out var lid) && lid.ValueKind == JsonValueKind.Number
                ? lid.GetInt32()
                : null;

            result.Add((
                Str(record, "id") ?? Guid.NewGuid().ToString(),
                Str(record, "name") ?? "task",
                PipelineRunStatusExtensions.FromAzureDevOps(Str(record, "state"), Str(record, "result")),
                logId,
                PullRequestDateParser.Parse(Str(record, "startTime")) ?? DateTime.MaxValue,
                record.TryGetProperty("order", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : 0));
        }

        return result
            .OrderBy(r => r.Start)
            .ThenBy(r => r.Order)
            .Select(r => (r.Id, r.Name, r.Status, r.LogId))
            .ToList();
    }

    /// <summary>Queue the same definition again on the same branch.</summary>
    public Task<PullRequestActionResult> RerunPipelineAsync(string project, int definitionId, string? branch) =>
        SendAuthorizedAsync(HttpMethod.Post,
            $"{Uri.EscapeDataString(project)}/_apis/build/builds?api-version=7.0",
            new
            {
                definition = new { id = definitionId },
                sourceBranch = string.IsNullOrEmpty(branch) || branch.StartsWith("refs/") ? branch : $"refs/heads/{branch}",
            },
            $"rerun definition {definitionId} in {project}");

    public Task<PullRequestActionResult> CancelPipelineAsync(string project, long buildId) =>
        SendAuthorizedAsync(HttpMethod.Patch,
            $"{Uri.EscapeDataString(project)}/_apis/build/builds/{buildId}?api-version=7.0",
            new { status = "cancelling" },
            $"cancel build {buildId} in {project}");
}
