using System.Windows;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.GUI.Views.Shared.Widgets;

namespace FleetMate.GUI.Views.Development;

public partial class DevelopmentView
{
    private App? _freshnessApp;
    private DevelopmentDataSync? _dataSync;
    private string _pullCountSummary = "Pull requests";
    private string _commitCountSummary = "Commits";
    private string _pipelineCountSummary = "Pipelines";

    private void HookFreshness(App app)
    {
        if (_freshnessApp == app) return;
        _freshnessApp = app;
        _dataSync = app.DevelopmentSync;
        app.Inbox.Changed += OnInboxCacheChanged;
        app.CacheChanged += OnDevelopmentCacheChanged;
        app.ServicesReloaded += OnDevelopmentServicesReloaded;
        _dataSync.StatusChanged += OnFreshnessStatusChanged;
        UpdateFreshnessContext();
    }

    private void OnFreshnessUnloaded(object sender, RoutedEventArgs e)
    {
        RunView.CancelPendingLog();
        UnhookFreshness();
    }

    private void UnhookFreshness()
    {
        if (_freshnessApp is { } app)
        {
            app.Inbox.Changed -= OnInboxCacheChanged;
            app.CacheChanged -= OnDevelopmentCacheChanged;
            app.ServicesReloaded -= OnDevelopmentServicesReloaded;
        }
        if (_dataSync != null)
        {
            _dataSync.StatusChanged -= OnFreshnessStatusChanged;
            _dataSync.DetachView(this);
        }
        _freshnessApp = null;
        _dataSync = null;
    }

    private void OnDevelopmentServicesReloaded()
    {
        UnhookFreshness();
        RunView.Clear();
        PullRequestList.ItemsSource = null;
        CommitsList.ItemsSource = null;
        PipelinesList.ItemsSource = null;
        PullsRepoCombo.ItemsSource = null;
        CommitsRepoCombo.ItemsSource = null;
        PipelinesRepoCombo.ItemsSource = null;
        _repository = _commitsRepository = _runsRepository = null;
        _pullCountSummary = "Pull requests";
        _commitCountSummary = "Commits";
        _pipelineCountSummary = "Pipelines";
        ShowDetail(DetailPlaceholder);
        if (IsLoaded && AppInstance is { } app) HookFreshness(app);
    }

    private void OnInboxCacheChanged(object? sender, EventArgs e) => RenderInbox();
    private void OnFreshnessStatusChanged(object? sender, EventArgs e) => UpdateFreshnessStatus();

    private void OnDevelopmentCacheChanged(string key)
    {
        if (!IsLoaded) return;
        if (_dataSync?.IsDisposed == true) { OnDevelopmentServicesReloaded(); return; }
        if (key == "PullRequests" && CurrentSegment == DevelopmentSegment.Pulls) Rerender();
        else if (key == "Commits" && CurrentSegment == DevelopmentSegment.Commits) RenderCommits();
        else if (key == "Runs" && CurrentSegment == DevelopmentSegment.Pipelines) RenderRuns();
    }

    private void UpdateFreshnessContext(PipelineRun? selected = null)
    {
        if (!IsLoaded || _dataSync == null) return;
        _dataSync.AttachView(this, CurrentSegment, CurrentSegment == DevelopmentSegment.Pipelines ? _runsRepository : null,
            CurrentSegment == DevelopmentSegment.Pipelines ? selected ?? RunView.CurrentRun : null,
            CurrentSegment == DevelopmentSegment.Pipelines ? _runsSource : DevelopmentSourceFilter.All);
    }

    private Task RefreshAreaAsync(DevelopmentRefreshArea area) => AppInstance?.DevelopmentSync.RefreshNowAsync(area) ?? Task.CompletedTask;

    private void UpdateFreshnessStatus()
    {
        if (_dataSync == null) return;
        PullRequestCount.Text = _pullCountSummary + _dataSync.StatusSuffix(DevelopmentRefreshArea.PullRequests);
        CommitsCount.Text = _commitCountSummary + _dataSync.StatusSuffix(DevelopmentRefreshArea.Commits);
        PipelinesCount.Text = _pipelineCountSummary + _dataSync.StatusSuffix(DevelopmentRefreshArea.Pipelines);
        if (CurrentSegment is DevelopmentSegment.Repos or DevelopmentSegment.Skills) { LoadingRing.Visibility = Visibility.Collapsed; return; }
        var area = CurrentSegment switch
        {
            DevelopmentSegment.Pulls => DevelopmentRefreshArea.PullRequests,
            DevelopmentSegment.Commits => DevelopmentRefreshArea.Commits,
            _ => DevelopmentRefreshArea.Pipelines,
        };
        var hasRows = area switch
        {
            DevelopmentRefreshArea.PullRequests => AppInstance?.DevelopmentPullRequests != null,
            DevelopmentRefreshArea.Commits => AppInstance?.DevelopmentCommits != null,
            _ => AppInstance?.DevelopmentRuns != null,
        };
        LoadingRing.Visibility = !hasRows && _dataSync.IsLoading(area) ? Visibility.Visible : Visibility.Collapsed;
        if (CurrentSegment == DevelopmentSegment.Pipelines && !_dataSync.IsLoading(DevelopmentRefreshArea.Pipelines))
            foreach (var row in PipelinesList.Items.OfType<PipelineRunRowViewModel>().Where(row => row.Run.Status.IsActive())) row.RefreshTiming();
        if (CurrentSegment == DevelopmentSegment.Pipelines && RunView.CurrentRun is { } shown
            && AppInstance?.DevelopmentRuns?.FirstOrDefault(run => PipelineRunCache.Key(run) == PipelineRunCache.Key(shown)) is { } current)
            RunView.UpdateSummary(current);
    }
}
