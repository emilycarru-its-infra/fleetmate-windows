using System.Windows;
using FleetMate.Core.Links;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.GUI.Views.Shared;
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

    /// <summary>
    /// An Azure DevOps link that failed before Azure DevOps sign-in finished,
    /// opened again once it does. A link that launched the app arrives first.
    /// </summary>
    private FleetMateLink? _linkAwaitingSignIn;

    /// <summary>Take links queued on <see cref="App"/>, now and whenever one arrives.</summary>
    private void HookLinks()
    {
        Loaded += async (_, _) =>
        {
            if (!_linksHooked && AppInstance is { } app)
            {
                _linksHooked = true;
                app.DevelopmentLinkRequested += async (_, _) => { if (IsLoaded) await TakePendingLinkAsync(); };
                app.DevOpsSignedIn += async () => await OnDevOpsSignedInAsync();
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

    /// <summary>
    /// Signed in to Azure DevOps: reload the lists already loaded, since a load
    /// that ran before sign-in left Azure DevOps out, then open a held link.
    /// </summary>
    private async Task OnDevOpsSignedInAsync()
    {
        if (AppInstance is not { } app) return;
        var reloads = new List<Task>();
        if (app.DevelopmentPullRequests != null) reloads.Add(LoadPullRequestsAsync());
        if (app.DevelopmentCommits != null) reloads.Add(LoadCommitsAsync());
        if (app.DevelopmentRuns != null) reloads.Add(LoadRunsAsync());
        await Task.WhenAll(reloads);

        if (_linkAwaitingSignIn is not { } link) return;
        _linkAwaitingSignIn = null;
        Log.Information("[development] Signed in to Azure DevOps; opening {Link}", link.ToLink());
        try
        {
            await OpenLinkAsync(link);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open {Link}", link.ToLink());
        }
    }

    /// <summary>
    /// A link that could not be found: hold it for Azure DevOps sign-in, open it
    /// again at once when sign-in landed while it was being fetched, or report it.
    /// GitHub links never wait.
    /// </summary>
    private async Task LinkFailedAsync(App app, FleetMateLink link, bool startedSignedIn)
    {
        var retry = link.NeedsAzureDevOps() ? app.DevOpsRetry(startedSignedIn) : SignInRetry.ShowError;
        switch (retry)
        {
            case SignInRetry.RetryNow:
                await OpenLinkAsync(link);
                break;
            case SignInRetry.WaitForSignIn:
                _linkAwaitingSignIn = link;
                Log.Information("[development] {Link} opens once Azure DevOps sign-in finishes", link.ToLink());
                DevOpsWaitHost.Content = new DevOpsSignInWaitPanel(
                    link is FleetMateLink.PullRequest ? "the linked pull request" : "the linked run");
                ShowDetail(DevOpsWaitHost);
                break;
            default:
                ShowLinkNotFound(link);
                break;
        }
    }

    private async Task OpenLinkAsync(FleetMateLink link)
    {
        _linkAwaitingSignIn = null;
        ClearLinkFilters();
        var startedSignedIn = AppInstance?.IsDevOpsSsoAuthenticated ?? false;
        switch (link)
        {
            case FleetMateLink.PullRequest pr:
                PullRequestsSegment.IsChecked = true;
                await OpenPullRequestLinkAsync(pr, startedSignedIn);
                break;
            case FleetMateLink.Commit commit:
                CommitsSegment.IsChecked = true;
                await OpenCommitLinkAsync(commit);
                break;
            case FleetMateLink.AzureDevOpsRun or FleetMateLink.AzureDevOpsPipeline or FleetMateLink.GitHubRun:
                PipelinesSegment.IsChecked = true;
                await OpenRunLinkAsync(link, startedSignedIn);
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

    private async Task OpenPullRequestLinkAsync(FleetMateLink.PullRequest link, bool startedSignedIn)
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

        var pr = await FetchOrNullAsync(() => FetchPullRequestAsync(app, link), link);
        if (pr != null) await ShowPullRequestAsync(pr);
        else await LinkFailedAsync(app, link, startedSignedIn);
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
        // The commit view holds an Azure DevOps commit for sign-in itself, the
        // same way for a link and for a row picked before sign-in finished.
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

    private async Task OpenRunLinkAsync(FleetMateLink link, bool startedSignedIn)
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

        // A pipeline definition the loaded runs don't cover opens on its
        // latest run, fetched from Azure DevOps.
        run ??= await FetchOrNullAsync(() => FetchRunAsync(app, link), link);
        if (run == null)
        {
            await LinkFailedAsync(app, link, startedSignedIn);
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

    /// <summary>A fetch that threw counts as not found; the caller decides whether sign-in explains it.</summary>
    private static async Task<T?> FetchOrNullAsync<T>(Func<Task<T?>> fetch, FleetMateLink link) where T : class
    {
        try { return await fetch(); }
        catch (Exception ex)
        {
            Log.Information(ex, "[development] Fetching {Link} failed", link.ToLink());
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
