using System.Text.Json;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Projects;
using Serilog;

namespace FleetMate.Core.Services.Projects;

/// <summary>
/// Development › Pipelines on GitHub: Actions runs, their job logs, rerun and
/// cancel. GitHub has no cross-repository run list, so the caller passes the
/// repositories to look at — the ones with recent commits.
/// </summary>
public sealed class GitHubActionsService : IDisposable
{
    private readonly GitHubGraphQLClient _client;

    public GitHubActionsService(GitHubProviderConfig config)
    {
        _client = new GitHubGraphQLClient(config);
    }

    private static string RepoPath(string owner, string repo) =>
        $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";

    /// <summary>Runs created since <paramref name="since"/> across the given repositories, newest first. Never throws.</summary>
    public async Task<List<PipelineRun>> GetRecentPipelineRunsAsync(
        IEnumerable<(string Owner, string Repository)> repositories, DateTime since, CancellationToken ct = default)
    {
        var day = since.ToUniversalTime().ToString("yyyy-MM-dd");
        using var gate = new SemaphoreSlim(6);

        var perRepo = await Task.WhenAll(repositories.Distinct().Select(async r =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var raw = await _client.ExecuteRestAsync(
                    $"{RepoPath(r.Owner, r.Repository)}/actions/runs?per_page=15&created=%3E%3D{day}", ct: ct);
                using var doc = JsonDocument.Parse(raw);
                return ParseRuns(doc.RootElement, r.Owner, r.Repository);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[github] runs unavailable for {Owner}/{Repo}", r.Owner, r.Repository);
                return new List<PipelineRun>();
            }
            finally
            {
                gate.Release();
            }
        }));

        var runs = perRepo.SelectMany(x => x).OrderByDescending(r => r.SortDate).ToList();
        Log.Information("[github] pipeline runs → {Count}", runs.Count);
        return runs;
    }

    internal static List<PipelineRun> ParseRuns(JsonElement root, string owner, string repository)
    {
        var result = new List<PipelineRun>();
        if (!root.TryGetProperty("workflow_runs", out var runs) || runs.ValueKind != JsonValueKind.Array) return result;

        foreach (var run in runs.EnumerateArray())
        {
            if (!run.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number) continue;

            var actor = run.TryGetProperty("triggering_actor", out var ta) && ta.ValueKind == JsonValueKind.Object ? ta
                : run.TryGetProperty("actor", out var a) ? a : default;

            result.Add(new PipelineRun
            {
                Source = PullRequestSource.GitHub,
                Container = owner,
                Repository = repository,
                PipelineName = Str(run, "name") ?? Str(run, "display_title") ?? "workflow",
                PipelineId = run.TryGetProperty("workflow_id", out var w) && w.ValueKind == JsonValueKind.Number && w.TryGetInt32(out var wid) ? wid : null,
                RunId = id.GetInt64(),
                RunNumber = run.TryGetProperty("run_number", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt64().ToString() : "",
                Status = PipelineRunStatusExtensions.FromGitHub(Str(run, "status"), Str(run, "conclusion")),
                Branch = Str(run, "head_branch"),
                CommitSha = Str(run, "head_sha"),
                TriggeredBy = Str(actor, "login"),
                StartedAt = PullRequestDateParser.Parse(Str(run, "run_started_at") ?? Str(run, "created_at")),
                // updated_at is the finish time only once the run has completed.
                FinishedAt = string.Equals(Str(run, "status"), "completed", StringComparison.OrdinalIgnoreCase)
                    ? PullRequestDateParser.Parse(Str(run, "updated_at"))
                    : null,
                WebUrl = Str(run, "html_url") ?? $"https://github.com/{owner}/{repository}/actions",
            });
        }

        return result;
    }

    /// <summary>One section per job, each capped to its tail.</summary>
    public async Task<PipelineRunLog> GetPipelineRunLogAsync(string owner, string repo, long runId, CancellationToken ct = default)
    {
        var raw = await _client.ExecuteRestAsync($"{RepoPath(owner, repo)}/actions/runs/{runId}/jobs?per_page=100", ct: ct);
        using var doc = JsonDocument.Parse(raw);

        var jobs = new List<(long Id, string Name, PipelineRunStatus Status)>();
        if (doc.RootElement.TryGetProperty("jobs", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var job in list.EnumerateArray())
            {
                if (!job.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number) continue;
                jobs.Add((id.GetInt64(), Str(job, "name") ?? "job",
                    PipelineRunStatusExtensions.FromGitHub(Str(job, "status"), Str(job, "conclusion"))));
            }
        }

        var truncated = false;
        var sections = await Task.WhenAll(jobs.Select(async job =>
        {
            string text;
            try
            {
                // The logs endpoint 302s to a signed blob URL; the client
                // follows it without the bearer token.
                text = await _client.GetRestRedirectedTextAsync($"{RepoPath(owner, repo)}/actions/jobs/{job.Id}/logs", ct);
            }
            catch (Exception ex)
            {
                // A queued or skipped job has no log yet — say so rather than fail the run.
                text = job.Status.IsActive() ? "(log not available yet)" : $"(log unavailable: {ex.Message})";
            }

            var (tail, cut) = PipelineRunLog.Tail(text);
            if (cut) truncated = true;
            return new PipelineRunLog.Section { Id = job.Id.ToString(), Name = job.Name, Status = job.Status, Text = tail };
        }));

        return new PipelineRunLog { RunId = runId, Sections = sections.ToList(), Truncated = truncated };
    }

    public Task<PullRequestActionResult> RerunAsync(string owner, string repo, long runId, CancellationToken ct = default) =>
        RunAsync($"rerun {owner}/{repo} run {runId}",
            () => _client.ExecuteRestAsync($"{RepoPath(owner, repo)}/actions/runs/{runId}/rerun", "POST", ct: ct));

    public Task<PullRequestActionResult> CancelAsync(string owner, string repo, long runId, CancellationToken ct = default) =>
        RunAsync($"cancel {owner}/{repo} run {runId}",
            () => _client.ExecuteRestAsync($"{RepoPath(owner, repo)}/actions/runs/{runId}/cancel", "POST", ct: ct));

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

    private static string? Str(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public void Dispose() => _client.Dispose();
}
