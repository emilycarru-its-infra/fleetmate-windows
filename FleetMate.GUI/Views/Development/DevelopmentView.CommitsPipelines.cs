using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using Serilog;

namespace FleetMate.GUI.Views.Development;

/// <summary>Development › Commits and Pipelines, plus the refresh cadence shared by every segment.</summary>
public partial class DevelopmentView
{
    /// <summary>Commits look back two weeks; pipeline runs one.</summary>
    private static readonly TimeSpan CommitsWindow = TimeSpan.FromDays(14);
    private static readonly TimeSpan RunsWindow = TimeSpan.FromDays(7);

    private DevelopmentSourceFilter _commitsSource = DevelopmentSourceFilter.All;
    private DevelopmentSourceFilter _runsSource = DevelopmentSourceFilter.All;
    private PipelineStatusFilter _runsStatus = PipelineStatusFilter.All;
    private bool _loadingCommits;
    private bool _loadingRuns;
    private DispatcherTimer? _cycleTimer;
    private DispatcherTimer? _activeRunsTimer;

    // MARK: - Refresh cadence

    /// <summary>
    /// Everything loaded refreshes every 15 minutes — 12 refreshes an hour of a
    /// dozen GraphQL searches is what blows GitHub's hourly budget. While
    /// Pipelines is showing and a run is still going, runs also refresh every
    /// 5 minutes so its status moves.
    /// </summary>
    private void StartTimers()
    {
        _cycleTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        _cycleTimer.Tick += async (_, _) => { if (IsVisible) await RefreshAsync(); };
        _cycleTimer.Start();

        _activeRunsTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _activeRunsTimer.Tick += async (_, _) =>
        {
            if (IsVisible && PipelinesSegment.IsChecked == true
                && AppInstance?.DevelopmentRuns?.Any(r => r.Status.IsActive()) == true)
            {
                await LoadRunsAsync();
            }
        };
        _activeRunsTimer.Start();
    }

    // MARK: - Commits

    private async Task LoadCommitsAsync()
    {
        if (_loadingCommits || AppInstance is not { } app) return;
        _loadingCommits = true;
        LoadingRing.Visibility = Visibility.Visible;

        try
        {
            var since = DateTime.UtcNow - CommitsWindow;
            var config = app.Config;
            var tasks = new List<Task<List<RepositoryCommits>>>();

            if (!string.IsNullOrWhiteSpace(config.AzureDevOps?.Organization))
            {
                tasks.Add(Task.Run(async () =>
                {
                    using var devops = new AzureDevOpsService(config.AzureDevOps!);
                    return await devops.GetRecentCommitsAsync(since);
                }));
            }

            tasks.Add(Task.Run(async () =>
            {
                var gh = config.GitHubProviderOrDefault();
                using var github = new GitHubPullRequestService(gh);
                var owners = new List<string?> { gh.Organization, gh.Owner };
                try
                {
                    var (login, orgs) = await github.GetViewerAsync();
                    owners.Add(login);
                    owners.AddRange(orgs);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "[development] viewer lookup failed for commits");
                }

                return await github.GetRecentCommitsAsync(owners.Where(o => !string.IsNullOrWhiteSpace(o)).Cast<string>(), since);
            }));

            var results = await Task.WhenAll(tasks);
            app.DevelopmentCommits = results.SelectMany(r => r).OrderByDescending(r => r.LatestDate).ToList();
            RenderCommits();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[development] Failed to load commits");
            CommitsCount.Text = $"Could not load commits — {ex.Message}";
        }
        finally
        {
            _loadingCommits = false;
            LoadingRing.Visibility = _loading || _loadingRuns ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void RenderCommits()
    {
        if (AppInstance?.DevelopmentCommits is not { } all) return;

        var repos = CommitsAndPipelinesFilter.Commits(all, _commitsSource, CommitsSearchBox.Text);
        CommitsList.ItemsSource = repos.Select(r => new RepositoryCommitsViewModel { Repository = r }).ToList();
        CommitsCount.Text = $"{repos.Sum(r => r.Commits.Count)} commits across {repos.Count} repositories · last 14 days";

        if (CommitsSegment.IsChecked == true)
        {
            EmptyText.Visibility = repos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = all.Count == 0 ? "No commits in the last 14 days." : "Nothing matches these filters.";
        }
    }

    private void OnCommitsFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized) RenderCommits();
    }

    private void OnCommitsSourceClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && Enum.TryParse<DevelopmentSourceFilter>(tag, out var source))
            _commitsSource = source;

        CommitsSourceAll.IsChecked = _commitsSource == DevelopmentSourceFilter.All;
        CommitsSourceDevOps.IsChecked = _commitsSource == DevelopmentSourceFilter.DevOps;
        CommitsSourceGitHub.IsChecked = _commitsSource == DevelopmentSourceFilter.GitHub;
        RenderCommits();
    }

    private void OnShowAllClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RepositoryCommitsViewModel repo) repo.ToggleShowAll();
    }

    private async void OnCommitClicked(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CommitRowViewModel row) return;
        ShowDetail(CommitView);
        await CommitView.ShowAsync(row.Repository, row.Commit);
    }

    // MARK: - Pipelines

    private async Task LoadRunsAsync()
    {
        if (_loadingRuns || AppInstance is not { } app) return;
        _loadingRuns = true;
        LoadingRing.Visibility = Visibility.Visible;

        try
        {
            var since = DateTime.UtcNow - RunsWindow;
            var config = app.Config;
            var tasks = new List<Task<List<PipelineRun>>>();

            if (!string.IsNullOrWhiteSpace(config.AzureDevOps?.Organization))
            {
                tasks.Add(Task.Run(async () =>
                {
                    using var devops = new AzureDevOpsService(config.AzureDevOps!);
                    return await devops.GetRecentPipelineRunsAsync(since);
                }));
            }

            // GitHub has no cross-repository run list: the repositories with
            // recent commits are the scope, so Commits loads first if needed.
            if (app.DevelopmentCommits is null) await LoadCommitsAsync();
            var repos = (app.DevelopmentCommits ?? new())
                .Where(r => r.Source == PullRequestSource.GitHub)
                .Select(r => (r.Container, r.Repository))
                .ToList();

            if (repos.Count > 0)
            {
                tasks.Add(Task.Run(async () =>
                {
                    using var actions = new GitHubActionsService(config.GitHubProviderOrDefault());
                    return await actions.GetRecentPipelineRunsAsync(repos, since);
                }));
            }

            var results = await Task.WhenAll(tasks);
            app.DevelopmentRuns = results.SelectMany(r => r).OrderByDescending(r => r.SortDate).ToList();
            RenderRuns();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[development] Failed to load pipeline runs");
            PipelinesCount.Text = $"Could not load pipeline runs — {ex.Message}";
        }
        finally
        {
            _loadingRuns = false;
            LoadingRing.Visibility = _loading || _loadingCommits ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void RenderRuns()
    {
        if (AppInstance?.DevelopmentRuns is not { } all) return;

        var selectedId = (PipelinesList.SelectedItem as PipelineRunRowViewModel)?.Run.Id;
        var runs = CommitsAndPipelinesFilter.Runs(all, _runsSource, _runsStatus, PipelinesSearchBox.Text);
        var rows = runs.Select(r => new PipelineRunRowViewModel { Run = r }).ToList();
        PipelinesList.ItemsSource = rows;
        if (selectedId != null) PipelinesList.SelectedItem = rows.FirstOrDefault(r => r.Run.Id == selectedId);

        var running = all.Count(r => r.Status.IsActive());
        var failed = CommitsAndPipelinesFilter.FailingCount(all);
        PipelinesCount.Text = $"{rows.Count} runs · {running} running · {failed} failed · last 7 days";

        if (PipelinesSegment.IsChecked == true)
        {
            EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = all.Count == 0 ? "No pipeline runs in the last 7 days." : "Nothing matches these filters.";
        }
    }

    private void OnPipelinesFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized) RenderRuns();
    }

    private void OnRunsSourceClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && Enum.TryParse<DevelopmentSourceFilter>(tag, out var source))
            _runsSource = source;

        RunsSourceAll.IsChecked = _runsSource == DevelopmentSourceFilter.All;
        RunsSourceDevOps.IsChecked = _runsSource == DevelopmentSourceFilter.DevOps;
        RunsSourceGitHub.IsChecked = _runsSource == DevelopmentSourceFilter.GitHub;
        RenderRuns();
    }

    /// <summary>Status chips toggle: clicking the active one clears it back to all.</summary>
    private void OnRunsStatusClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && Enum.TryParse<PipelineStatusFilter>(tag, out var status))
            _runsStatus = _runsStatus == status ? PipelineStatusFilter.All : status;

        RunsStatusRunning.IsChecked = _runsStatus == PipelineStatusFilter.Running;
        RunsStatusFailed.IsChecked = _runsStatus == PipelineStatusFilter.Failed;
        RunsStatusSucceeded.IsChecked = _runsStatus == PipelineStatusFilter.Succeeded;
        RenderRuns();
    }

    private async void OnRunSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PipelinesList.SelectedItem is not PipelineRunRowViewModel row) return;
        ShowDetail(RunView);
        await RunView.ShowAsync(row.Run);
    }
}
