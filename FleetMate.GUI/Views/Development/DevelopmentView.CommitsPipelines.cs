using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.GUI.Views.Shared;
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
    private string? _commitsRepository;
    private string? _runsRepository;
    private bool _renderingRuns;
    private Task LoadCommitsAsync() => RefreshAreaAsync(DevelopmentRefreshArea.Commits);
    private Task LoadRunsAsync() => RefreshAreaAsync(DevelopmentRefreshArea.Pipelines);

    private void RenderCommits()
    {
        if (AppInstance?.DevelopmentCommits is not { } all) return;

        _commitsRepository = RepoFilterMenu.Fill(CommitsRepoCombo,
            RepositoryFilterHierarchy.Count(all.Where(repo => CommitsAndPipelinesFilter.MatchesSource(repo.Source, _commitsSource)), repo => new RepositoryFilterScope(repo.Source, repo.Container, repo.Repository), repo => repo.Commits.Count), _commitsRepository);

        var repos = CommitsAndPipelinesFilter.Commits(all, _commitsSource, CommitsSearchBox.Text, _commitsRepository);
        CommitsList.ItemsSource = repos.Select(r => new RepositoryCommitsViewModel { Repository = r }).ToList();
        _commitCountSummary = $"{repos.Sum(r => r.Commits.Count)} commits across {repos.Count} repositories · last 14 days";
        UpdateFreshnessStatus();

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

        _commitsRepository = null;

        CommitsSourceAll.IsChecked = _commitsSource == DevelopmentSourceFilter.All;
        CommitsSourceDevOps.IsChecked = _commitsSource == DevelopmentSourceFilter.DevOps;
        CommitsSourceGitHub.IsChecked = _commitsSource == DevelopmentSourceFilter.GitHub;
        RenderCommits();
    }

    private void OnCommitsRepoChanged(object sender, SelectionChangedEventArgs e)
    {
        var repo = RepoFilterMenu.Picked(CommitsRepoCombo, out var changed);
        if (!changed || repo == _commitsRepository) return;
        _commitsRepository = repo;
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

    private void RenderRuns()
    {
        if (AppInstance?.DevelopmentRuns is not { } all) return;

        var selectedRun = (PipelinesList.SelectedItem as PipelineRunRowViewModel)?.Run;
        var selectedId = selectedRun == null ? null : PipelineRunCache.Key(selectedRun);
        _runsRepository = RepoFilterMenu.Fill(PipelinesRepoCombo,
            RepositoryFilterHierarchy.Count(all.Where(run => CommitsAndPipelinesFilter.MatchesSource(run.Source, _runsSource)), run => new RepositoryFilterScope(run.Source, run.Container, run.Repository)), _runsRepository);

        var runs = CommitsAndPipelinesFilter.Runs(all, _runsSource, _runsStatus, PipelinesSearchBox.Text, _runsRepository);
        var approvals = AppInstance?.PipelineApprovals;
        var rows = runs.Select(r => new PipelineRunRowViewModel { Run = r, AwaitingApproval = approvals?.IsAwaiting(r) == true }).ToList();
        _renderingRuns = true;
        try
        {
            PipelinesList.ItemsSource = rows;
            if (selectedId != null) PipelinesList.SelectedItem = rows.FirstOrDefault(row => PipelineRunCache.Key(row.Run) == selectedId);
        }
        finally { _renderingRuns = false; }
        if (RunView.CurrentRun is { } shown && all.FirstOrDefault(run => PipelineRunCache.Key(run) == PipelineRunCache.Key(shown)) is { } current)
            RunView.UpdateSummary(current);

        var running = all.Count(r => r.Status.IsActive());
        var failed = CommitsAndPipelinesFilter.FailingCount(all);
        _pipelineCountSummary = $"{rows.Count} runs · {running} running · {failed} failed · last 7 days";
        UpdateFreshnessStatus();

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

        _runsRepository = null;

        RunsSourceAll.IsChecked = _runsSource == DevelopmentSourceFilter.All;
        RunsSourceDevOps.IsChecked = _runsSource == DevelopmentSourceFilter.DevOps;
        RunsSourceGitHub.IsChecked = _runsSource == DevelopmentSourceFilter.GitHub;
        RenderRuns();
        UpdateFreshnessContext();
    }

    private void OnPipelinesRepoChanged(object sender, SelectionChangedEventArgs e)
    {
        var repo = RepoFilterMenu.Picked(PipelinesRepoCombo, out var changed);
        if (!changed || repo == _runsRepository) return;
        _runsRepository = repo;
        RenderRuns();
        UpdateFreshnessContext();
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

    /// <summary>Open Commits filtered to one repository — the Commits graphs' repository bar.</summary>
    public void ShowCommitsRepository(string repository)
    {
        _commitsSource = DevelopmentSourceFilter.All;
        CommitsSourceAll.IsChecked = true;
        CommitsSourceDevOps.IsChecked = CommitsSourceGitHub.IsChecked = false;
        _commitsRepository = repository;
        CommitsSegment.IsChecked = true;
        RenderCommits();
    }

    /// <summary>Open Pipelines filtered to one repository — the Pipelines graphs' repository bar.</summary>
    public void ShowPipelinesRepository(string repository)
    {
        _runsSource = DevelopmentSourceFilter.All;
        RunsSourceAll.IsChecked = true;
        RunsSourceDevOps.IsChecked = RunsSourceGitHub.IsChecked = false;
        _runsRepository = repository;
        PipelinesSegment.IsChecked = true;
        RenderRuns();
        UpdateFreshnessContext();
    }

    /// <summary>Open Pipelines filtered to one status — the Failing and Running figures' click.</summary>
    public void ShowPipelines(PipelineStatusFilter status)
    {
        _runsStatus = status;
        RunsStatusRunning.IsChecked = status == PipelineStatusFilter.Running;
        RunsStatusFailed.IsChecked = status == PipelineStatusFilter.Failed;
        RunsStatusSucceeded.IsChecked = status == PipelineStatusFilter.Succeeded;
        PipelinesSegment.IsChecked = true;
        RenderRuns();
        UpdateFreshnessContext();
    }

    // MARK: - Approvals

    private void OnPipelineApprovalsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (!IsLoaded) return;
        if (PipelinesSegment.IsChecked == true) RenderRuns();
        RunView.RefreshApprovals();
    });

    /// <summary>The row's Review Approval… item: show the run, then open its review.</summary>
    private async void OnRunReviewApproval(object sender, RoutedEventArgs e)
    {
        if (RowOf<PipelineRunRowViewModel>(sender) is not { } row) return;
        PipelinesList.SelectedItem = (PipelinesList.ItemsSource as IEnumerable<PipelineRunRowViewModel>)?
            .FirstOrDefault(r => PipelineRunCache.Key(r.Run) == PipelineRunCache.Key(row.Run)) ?? PipelinesList.SelectedItem;
        if (!RunView.IsShowing(row.Run))
        {
            ShowDetail(RunView);
            await RunView.ShowAsync(row.Run);
        }
        RunView.ReviewApproval();
    }

    private async void OnRunSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_renderingRuns || PipelinesList.SelectedItem is not PipelineRunRowViewModel row) return;
        UpdateFreshnessContext(row.Run);
        if (RunView.IsShowing(row.Run)) { RunView.UpdateSummary(row.Run); return; }
        ShowDetail(RunView);
        await RunView.ShowAsync(row.Run);
    }
}
