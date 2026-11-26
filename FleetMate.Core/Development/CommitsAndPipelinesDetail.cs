using FleetMate.Core.Config;
using FleetMate.Core.Links;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;

namespace FleetMate.Core.Development;

/// <summary>The words on a commit's detail pane, shared by FleetMate.GUI and FleetMate.WinUI.</summary>
public static class CommitDetailText
{
    /// <summary>"Ada · 4f3c2a1b · 3 Nov 2026, 14:05 · owner/repo".</summary>
    public static string Byline(PullRequestCommit commit, RepositoryCommits repository) =>
        $"{commit.AuthorName ?? "unknown"} · {commit.ShortSha} · {commit.Date?.ToLocalTime():d MMM yyyy, HH:mm} · {repository.DisplayName}";

    /// <summary>The message without its subject line, which is already the title; empty when there is no body.</summary>
    public static string Body(string message)
    {
        var newline = message.IndexOfAny(new[] { '\n', '\r' });
        return newline < 0 ? "" : message[newline..].Trim();
    }

    /// <summary>"Changes (3 files, +12 −4)" for diffs; "Changes (3 files)" for a change list.</summary>
    public static string ChangesHeader(CommitDetail detail) => detail.Files.Count > 0
        ? $"Changes ({detail.Files.Count} files, +{detail.Additions} −{detail.Deletions})"
        : $"Changes ({detail.Changes.Count} files)";
}

/// <summary>The rules and words of a pipeline run's detail pane.</summary>
public static class PipelineRunDetailText
{
    /// <summary>"4f3c2a1b · Ada · 3 Nov, 14:05 · 3m 05s", leaving out what the run lacks.</summary>
    public static string Details(PipelineRun run)
    {
        var sha = run.CommitSha is { Length: > 8 } s ? s[..8] : run.CommitSha;
        return string.Join(" · ", new[]
        {
            sha,
            run.TriggeredBy,
            run.StartedAt?.ToLocalTime().ToString("d MMM, HH:mm"),
            DevelopmentText.FormatDuration(run.Duration),
        }.Where(x => !string.IsNullOrEmpty(x)));
    }

    /// <summary>Cancel only while it can still be cancelled.</summary>
    public static bool CanCancel(PipelineRun run) => run.Status.IsActive();

    /// <summary>Rerun once it has finished and there is something to rerun it from.</summary>
    public static bool CanRerun(PipelineRun run) =>
        !run.Status.IsActive() && (run.Source == PullRequestSource.GitHub || run.PipelineId != null);

    public static string RerunPrompt(PipelineRun run) =>
        $"{run.PipelineName} #{run.RunNumber}\n\nThis queues the run again on {run.Branch ?? "its branch"}.";

    public static string CancelPrompt(PipelineRun run) => $"{run.PipelineName} #{run.RunNumber}\n\nThis stops the run where it is.";

    public static string ActionResult(string action, PullRequestActionResult result) =>
        result.Success ? $"{action} requested." : $"{action} failed: {result.Error}";

    public static string EmptyLog(PipelineRun run) =>
        run.Status.IsActive() ? "No log yet — the run has not started a job." : "This run has no log.";

    public const string LogPaused = "Log loading paused. Reload to fetch the latest log.";
    public const string LogTruncated = "Long sections show their last 400 KB";

    /// <summary>Which log sections start open: failed ones; on a run with no failure, only the last.</summary>
    public static List<bool> ExpandedSections(PipelineRunLog log)
    {
        var open = log.Sections.Select(s => s.Status is PipelineRunStatus.Failed or PipelineRunStatus.Partial).ToList();
        if (open.Count > 0 && !open.Contains(true)) open[^1] = true;
        return open;
    }
}

/// <summary>Text for the approval review, kept out of the windows so it can be tested.</summary>
public static class ApprovalReviewText
{
    /// <summary>"Waiting since 3 Nov, 14:05 · Approvers: Release managers".</summary>
    public static string Meta(PipelineApproval approval) =>
        string.Join(" · ", new[]
        {
            approval.CreatedOn is { } created ? $"Waiting since {created.ToLocalTime():d MMM, HH:mm}" : null,
            approval.Approvers.Count > 0 ? $"Approvers: {string.Join(", ", approval.Approvers)}" : null,
        }.Where(s => s != null));

    public static string Title(PipelineRun run) => $"Review {run.PipelineName} #{run.RunNumber}";

    public static string Instructions(PipelineApproval approval) =>
        string.IsNullOrWhiteSpace(approval.Instructions) ? "No instructions given." : approval.Instructions;

    public static string More(int count) => count > 1 ? $"{count - 1} more after this one" : "";

    public static string RejectTitle(PipelineRun run) => $"Reject {run.PipelineName} #{run.RunNumber}?";

    public const string RejectMessage = "The run stops at this step.";
    public const string NothingWaiting = "Nothing is waiting for approval on this run.";
}

/// <summary>Finding a commit or pipeline link's target among the loaded rows.</summary>
public static class DevelopmentLinkTargets
{
    public static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The commit a link names, from the loaded repositories when it is there;
    /// otherwise a stand-in opened by sha. Azure DevOps accepts the repository
    /// name where an id is expected.
    /// </summary>
    public static (RepositoryCommits Repository, PullRequestCommit Commit) Commit(
        IEnumerable<RepositoryCommits>? loaded, FleetMateLink.Commit link)
    {
        var (source, container, repo) = link.Source switch
        {
            FleetMateLink.AzureDevOps a => (PullRequestSource.AzureDevOps, a.Project, a.Repo),
            FleetMateLink.GitHub g => (PullRequestSource.GitHub, g.Owner, g.Repo),
            _ => (PullRequestSource.GitHub, "", ""),
        };
        var repository = loaded?.FirstOrDefault(r => r.Source == source && Same(r.Container, container) && Same(r.Repository, repo));
        var commit = repository?.Commits.FirstOrDefault(c => c.Id.StartsWith(link.Sha, StringComparison.OrdinalIgnoreCase));
        return (repository ?? new RepositoryCommits { Source = source, Container = container, Repository = repo, RepositoryId = repo },
            commit ?? new PullRequestCommit { Id = link.Sha });
    }

    /// <summary>The run a link names among the loaded runs; a pipeline link means its latest run.</summary>
    public static PipelineRun? Run(IEnumerable<PipelineRun> loaded, FleetMateLink link) => link switch
    {
        FleetMateLink.AzureDevOpsRun r => loaded.FirstOrDefault(x =>
            x.Source == PullRequestSource.AzureDevOps && x.RunId == r.RunId && Same(x.Container, r.Project)),
        FleetMateLink.AzureDevOpsPipeline d => loaded
            .Where(x => x.Source == PullRequestSource.AzureDevOps && x.PipelineId == d.DefinitionId && Same(x.Container, d.Project))
            .OrderByDescending(x => x.SortDate).FirstOrDefault(),
        FleetMateLink.GitHubRun g => loaded.FirstOrDefault(x =>
            x.Source == PullRequestSource.GitHub && x.RunId == g.RunId && Same(x.Container, g.Owner) && Same(x.Repository, g.Repo)),
        _ => null,
    };
}

/// <summary>
/// The reads and actions behind the commit and run detail panes. The shared
/// Azure DevOps service carries the sign-in token; a fresh one could only use
/// the broker, which misses a sign-in made another way.
/// </summary>
public static class CommitsAndPipelinesService
{
    public static async Task<CommitDetail> CommitDetailAsync(FleetMateConfig config, AzureDevOpsService? devOps,
        RepositoryCommits repository, PullRequestCommit commit)
    {
        if (repository.Source == PullRequestSource.AzureDevOps)
        {
            var id = repository.RepositoryId ?? repository.Repository;
            if (devOps != null) return await devOps.GetCommitDetailAsync(id, commit.Id, repository.Container);
            if (config.AzureDevOps is not { } ado) throw new InvalidOperationException("Azure DevOps is not configured");
            using var fresh = new AzureDevOpsService(ado);
            return await fresh.GetCommitDetailAsync(id, commit.Id, repository.Container);
        }

        using var github = new GitHubPullRequestService(config.GitHubProviderOrDefault());
        return await github.GetCommitDetailAsync(repository.Container, repository.Repository, commit.Id);
    }

    public static async Task<PipelineRunLog> RunLogAsync(FleetMateConfig config, PipelineRun run, CancellationToken ct)
    {
        if (run.Source == PullRequestSource.AzureDevOps)
        {
            if (config.AzureDevOps is not { } ado) throw new InvalidOperationException("Azure DevOps is not configured");
            using var devops = new AzureDevOpsService(ado, ct);
            return await devops.GetPipelineRunLogAsync(run.Container, run.RunId);
        }

        using var actions = new GitHubActionsService(config.GitHubProviderOrDefault());
        return await actions.GetPipelineRunLogAsync(run.Container, run.Repository ?? "", run.RunId, ct);
    }

    /// <summary>Fetch the run a link names when it isn't loaded: a pipeline link opens its latest run.</summary>
    public static async Task<PipelineRun?> RunForLinkAsync(FleetMateConfig config, AzureDevOpsService? devOps, FleetMateLink link)
    {
        switch (link)
        {
            case FleetMateLink.AzureDevOpsRun r when devOps != null:
                return await devOps.GetPipelineRunAsync(r.Project, r.RunId);
            case FleetMateLink.AzureDevOpsPipeline d when devOps != null:
                return await devOps.GetLatestPipelineRunAsync(d.Project, d.DefinitionId);
            case FleetMateLink.GitHubRun g:
                using (var actions = new GitHubActionsService(config.GitHubProviderOrDefault()))
                    return await actions.GetPipelineRunAsync(g.Owner, g.Repo, g.RunId);
            default:
                return null;
        }
    }

    /// <summary>Queue the run again. Null when Azure DevOps is not configured.</summary>
    public static Task<PullRequestActionResult?> RerunAsync(FleetMateConfig config, PipelineRun run) => ActAsync(config, run,
        gh => gh.RerunAsync(run.Container, run.Repository ?? "", run.RunId),
        ado => ado.RerunPipelineAsync(run.Container, run.PipelineId ?? 0, run.Branch));

    /// <summary>Stop the run. Null when Azure DevOps is not configured.</summary>
    public static Task<PullRequestActionResult?> CancelAsync(FleetMateConfig config, PipelineRun run) => ActAsync(config, run,
        gh => gh.CancelAsync(run.Container, run.Repository ?? "", run.RunId),
        ado => ado.CancelPipelineAsync(run.Container, run.RunId));

    private static async Task<PullRequestActionResult?> ActAsync(FleetMateConfig config, PipelineRun run,
        Func<GitHubActionsService, Task<PullRequestActionResult>> github,
        Func<AzureDevOpsService, Task<PullRequestActionResult>> devops)
    {
        if (run.Source == PullRequestSource.GitHub)
        {
            using var service = new GitHubActionsService(config.GitHubProviderOrDefault());
            return await github(service);
        }
        if (config.AzureDevOps is not { } ado) return null;
        using var azure = new AzureDevOpsService(ado);
        return await devops(azure);
    }

    /// <summary>Re-read the approvals of an active Azure DevOps run's project, so the banner is current.</summary>
    public static async Task RefreshApprovalsAsync(FleetMateConfig config, PipelineApprovalTracker tracker,
        IReadOnlyCollection<PipelineRun>? loaded, PipelineRun run)
    {
        if (run.Source != PullRequestSource.AzureDevOps || !run.Status.IsActive() || config.AzureDevOps is not { } ado) return;
        using var service = new AzureDevOpsService(ado);
        var runs = loaded?.ToList() ?? new List<PipelineRun> { run };
        if (!runs.Any(r => PipelineRunCache.Key(r) == PipelineRunCache.Key(run))) runs.Add(run);
        await tracker.RefreshAsync(runs, service, new[] { run.Container });
    }
}
