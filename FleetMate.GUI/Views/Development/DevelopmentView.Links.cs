using System.Windows;
using FleetMate.Core.Links;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using Serilog;

namespace FleetMate.GUI.Views.Development;

/// <summary>
/// Development's side of fleetmate: links. A pull request, commit or pipeline
/// link switches to its segment, clears the filters, and selects the item,
/// fetching it by id when it isn't loaded. A pipeline definition link opens
/// that pipeline's latest run.
/// </summary>
public partial class DevelopmentView
{
    private bool _linksHooked;

    /// <summary>Take links queued on <see cref="App"/>, now and whenever one arrives.</summary>
    private void HookLinks()
    {
        Loaded += async (_, _) =>
        {
            if (!_linksHooked && AppInstance is { } app)
            {
                _linksHooked = true;
                app.DevelopmentLinkRequested += async (_, _) => { if (IsLoaded) await TakePendingLinkAsync(); };
            }
            await TakePendingLinkAsync();
        };
    }

    private async Task TakePendingLinkAsync()
    {
        if (AppInstance is not { PendingDevelopmentLink: { } link } app) return;
        app.PendingDevelopmentLink = null;
        try
        {
            await OpenLinkAsync(link);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open {Link}", link.ToLink());
        }
    }

    private async Task OpenLinkAsync(FleetMateLink link)
    {
        ClearLinkFilters();
        switch (link)
        {
            case FleetMateLink.PullRequest pr:
                PullRequestsSegment.IsChecked = true;
                await OpenPullRequestLinkAsync(pr);
                break;
            case FleetMateLink.Commit commit:
                CommitsSegment.IsChecked = true;
                await OpenCommitLinkAsync(commit);
                break;
            case FleetMateLink.AzureDevOpsRun or FleetMateLink.AzureDevOpsPipeline or FleetMateLink.GitHubRun:
                PipelinesSegment.IsChecked = true;
                await OpenRunLinkAsync(link);
                break;
        }
    }

    /// <summary>Every filter back to everything, so the linked item can't be hidden.</summary>
    private void ClearLinkFilters()
    {
        _source = DevelopmentSourceFilter.All;
        _scope = DevelopmentScope.Everything;
        _repository = null;
        SearchBox.Text = "";
        SourceAll.IsChecked = true;
        SourceDevOps.IsChecked = false;
        SourceGitHub.IsChecked = false;
        ScopeEverything.IsChecked = true;
        ScopeMine.IsChecked = false;

        _commitsSource = DevelopmentSourceFilter.All;
        CommitsSearchBox.Text = "";
        CommitsSourceAll.IsChecked = true;
        CommitsSourceDevOps.IsChecked = false;
        CommitsSourceGitHub.IsChecked = false;

        _runsSource = DevelopmentSourceFilter.All;
        _runsStatus = PipelineStatusFilter.All;
        PipelinesSearchBox.Text = "";
        RunsSourceAll.IsChecked = true;
        RunsSourceDevOps.IsChecked = false;
        RunsSourceGitHub.IsChecked = false;
        RunsStatusRunning.IsChecked = false;
        RunsStatusFailed.IsChecked = false;
        RunsStatusSucceeded.IsChecked = false;
    }

    // ── Pull requests ────────────────────────────────────────────────────

    private async Task OpenPullRequestLinkAsync(FleetMateLink.PullRequest link)
    {
        if (AppInstance is not { } app) return;
        if (app.DevelopmentPullRequests is null) await LoadPullRequestsAsync();
        Rerender();

        var row = (PullRequestList.ItemsSource as System.Collections.IEnumerable)?
            .OfType<DevelopmentPullRequestRowViewModel>()
            .FirstOrDefault(r => MatchesPullRequest(r.PullRequest, link));
        if (row != null)
        {
            PullRequestList.SelectedItem = row;
            PullRequestList.ScrollIntoView(row);
            return;
        }

        var pr = await FetchPullRequestAsync(app, link);
        if (pr != null) await ShowPullRequestAsync(pr);
        else ShowLinkNotFound(link);
    }

    private static bool MatchesPullRequest(UnifiedPullRequest pr, FleetMateLink.PullRequest link) => link.Source switch
    {
        FleetMateLink.AzureDevOps a => pr.Source == PullRequestSource.AzureDevOps && pr.Number == link.Number
            && Same(pr.Container, a.Project) && Same(pr.Repository, a.Repo),
        FleetMateLink.GitHub g => pr.Source == PullRequestSource.GitHub && pr.Number == link.Number
            && Same(pr.Container, g.Owner) && Same(pr.Repository, g.Repo),
        _ => false,
    };

    private static async Task<UnifiedPullRequest?> FetchPullRequestAsync(App app, FleetMateLink.PullRequest link)
    {
        switch (link.Source)
        {
            case FleetMateLink.AzureDevOps a when app.DevOpsService != null:
                return await app.DevOpsService.GetPullRequestAsync(a.Project, a.Repo, link.Number);
            case FleetMateLink.GitHub g:
                using (var github = new GitHubPullRequestService(app.Config.GitHubProviderOrDefault()))
                    return await github.GetPullRequestAsync(g.Owner, g.Repo, link.Number);
            default:
                return null;
        }
    }

    // ── Commits ──────────────────────────────────────────────────────────

    private async Task OpenCommitLinkAsync(FleetMateLink.Commit link)
    {
        if (AppInstance is not { } app) return;
        if (app.DevelopmentCommits is null) await LoadCommitsAsync();
        else RenderCommits();

        var (source, container, repo) = link.Source switch
        {
            FleetMateLink.AzureDevOps a => (PullRequestSource.AzureDevOps, a.Project, a.Repo),
            FleetMateLink.GitHub g => (PullRequestSource.GitHub, g.Owner, g.Repo),
            _ => (PullRequestSource.GitHub, "", ""),
        };

        var repository = app.DevelopmentCommits?.FirstOrDefault(r =>
            r.Source == source && Same(r.Container, container) && Same(r.Repository, repo));
        var commit = repository?.Commits.FirstOrDefault(c => c.Id.StartsWith(link.Sha, StringComparison.OrdinalIgnoreCase));

        // Not in the recent list: open it by sha. Azure DevOps accepts the
        // repository name where an id is expected.
        repository ??= new RepositoryCommits { Source = source, Container = container, Repository = repo, RepositoryId = repo };
        commit ??= new PullRequestCommit { Id = link.Sha };

        ShowDetail(CommitView);
        await CommitView.ShowAsync(repository, commit);
    }

    // ── Pipelines ────────────────────────────────────────────────────────

    private async Task OpenRunLinkAsync(FleetMateLink link)
    {
        if (AppInstance is not { } app) return;
        if (app.DevelopmentRuns is null) await LoadRunsAsync();
        else RenderRuns();

        var loaded = app.DevelopmentRuns ?? new List<PipelineRun>();
        var run = link switch
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

        var row = (PipelinesList.ItemsSource as System.Collections.IEnumerable)?
            .OfType<PipelineRunRowViewModel>().FirstOrDefault(r => run != null && r.Run.Id == run.Id);
        if (row != null)
        {
            PipelinesList.SelectedItem = row;
            PipelinesList.ScrollIntoView(row);
            return;
        }

        run ??= await FetchRunAsync(app, link);
        if (run == null)
        {
            ShowLinkNotFound(link);
            return;
        }
        ShowDetail(RunView);
        await RunView.ShowAsync(run);
    }

    private static async Task<PipelineRun?> FetchRunAsync(App app, FleetMateLink link)
    {
        switch (link)
        {
            case FleetMateLink.AzureDevOpsRun r when app.DevOpsService != null:
                return await app.DevOpsService.GetPipelineRunAsync(r.Project, r.RunId);
            case FleetMateLink.AzureDevOpsPipeline d when app.DevOpsService != null:
                return await app.DevOpsService.GetLatestPipelineRunAsync(d.Project, d.DefinitionId);
            case FleetMateLink.GitHubRun g:
                using (var actions = new GitHubActionsService(app.Config.GitHubProviderOrDefault()))
                    return await actions.GetPipelineRunAsync(g.Owner, g.Repo, g.RunId);
            default:
                return null;
        }
    }

    private void ShowLinkNotFound(FleetMateLink link)
    {
        if (Window.GetWindow(this) is Shared.MainWindow window)
            window.ShowLinkBanner($"FleetMate couldn't find {link.ToLink()}. It may have been deleted, or you may not have access.");
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
