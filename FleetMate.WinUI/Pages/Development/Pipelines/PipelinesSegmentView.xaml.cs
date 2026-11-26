using FleetMate.Core.Shared;
using FleetMate.WinUI.Shell;
using FleetMate.Core.Development;
using FleetMate.Core.Links;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Services.Terminal;
using FleetMate.WinUI.Pages.Development.Shared;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Serilog;

namespace FleetMate.WinUI.Pages.Development.Pipelines;

/// <summary>
/// Development › Pipelines: the last week's runs across Azure Pipelines and
/// GitHub Actions, filtered by source, status (Approval only while runs wait),
/// repository and the toolbar search, and the selected run's summary, jobs and
/// log beside them. Runs held for approval carry a badge and can be approved
/// or rejected from the row's menu or the run's banner.
/// </summary>
public sealed partial class PipelinesSegmentView : UserControl, IDevelopmentSegmentView
{
    private readonly ListDetailLayout _layout;
    private readonly Dictionary<ToggleButton, PipelineStatusFilter> _statusChips;
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private DevelopmentPage? _page;
    private DevelopmentSourceFilter _source = DevelopmentSourceFilter.All;
    private PipelineStatusFilter _status = PipelineStatusFilter.All;
    private string? _repository;
    private string _search = "";
    private string _summary = "Pipelines";
    private List<PipelineRunItem> _rows = new();
    private bool _shown;
    private bool _rendering;
    private bool _busy;

    /// <summary>An Azure DevOps run link that failed before sign-in finished, opened again once it does.</summary>
    private FleetMateLink? _linkAwaitingSignIn;

    public PipelinesSegmentView()
    {
        InitializeComponent();
        AgentHandOff.AttachDrag(RunsList, item => item is PipelineRunItem row ? AgentContexts.PipelineRun(row.Run) : null);
        _layout = new ListDetailLayout(Root, ListColumn, DetailColumn, ListPane, DetailPane, BackButton);
        _statusChips = new()
        {
            [StatusApproval] = PipelineStatusFilter.Approval,
            [StatusRunning] = PipelineStatusFilter.Running,
            [StatusFailed] = PipelineStatusFilter.Failed,
            [StatusSucceeded] = PipelineStatusFilter.Succeeded,
        };
        RunPane.RunChanged += async (_, _) => await RefreshAsync();
    }

    public DevelopmentSegment Segment => DevelopmentSegment.Pipelines;

    public bool IsBusy => _busy;
    public event EventHandler? BusyChanged;

    public void Attach(DevelopmentPage page)
    {
        _page = page;
        var services = App.Services;
        services.CacheChanged += key => { if (key == "Runs" && _shown) Render(); else if (key == "Runs") UpdateBusy(); };
        services.DevelopmentSync.StatusChanged += (_, _) => { if (_shown) OnSyncStatusChanged(); };
        services.PipelineApprovals.Changed += (_, _) => _ui.TryEnqueue(() =>
        {
            if (!_shown) return;
            Render();
            RunPane.RefreshApprovals();
        });
        services.DevOpsSignedIn += OnDevOpsSignedIn;
        if (services.DemoMode && services.DevelopmentRuns is { } runs)
            _ = services.PipelineApprovals.RefreshAsync(runs, DevelopmentDemoData.Approvals(DateTime.UtcNow));
    }

    public void SetShown(bool shown)
    {
        _shown = shown;
        if (!shown)
        {
            RunPane.CancelPendingLog();
            return;
        }
        AttachSync();
        Render();
    }

    public void ApplySearch(string text)
    {
        if (_search == text) return;
        _search = text;
        if (_shown) Render();
    }

    public Task RefreshAsync() => App.Services.DevelopmentSync.RefreshNowAsync(DevelopmentRefreshArea.Pipelines);

    /// <summary>Tell the refresh worker what is shown: the repository, source and selected run refresh first.</summary>
    private void AttachSync(PipelineRun? selected = null)
    {
        if (!_shown) return;
        App.Services.DevelopmentSync.AttachView(this, Segment, _repository, selected ?? RunPane.CurrentRun, _source);
    }

    // MARK: - Render

    private void Render()
    {
        var services = App.Services;
        UpdateBusy();
        if (services.DevelopmentRuns is not { } all)
        {
            RunsList.ItemsSource = null;
            EmptyText.Text = services.DevelopmentSync.IsLoading(DevelopmentRefreshArea.Pipelines) ? "Loading pipeline runs…" : "Pipeline runs load when this view opens.";
            EmptyText.Visibility = Visibility.Visible;
            UpdateCount();
            return;
        }

        var selectedKey = (RunsList.SelectedItem as PipelineRunItem)?.Run is { } selectedRun ? PipelineRunCache.Key(selectedRun) : null;
        _repository = RepoFilter.SetEntries(RepositoryFilterHierarchy.Count(
            all.Where(run => CommitsAndPipelinesFilter.MatchesSource(run.Source, _source)),
            run => new RepositoryFilterScope(run.Source, run.Container, run.Repository)), _repository);

        var approvals = services.PipelineApprovals;
        Func<PipelineRun, bool> awaiting = approvals.IsAwaiting;
        var awaitingCount = CommitsAndPipelinesFilter.ApprovalRunCount(all, awaiting);
        _status = CommitsAndPipelinesFilter.StatusAfterApprovalCount(_status, awaitingCount);
        StatusApproval.Content = awaitingCount > 0 ? $"Approval {awaitingCount}" : "Approval";
        StatusApproval.Visibility = DevelopmentUi.Show(CommitsAndPipelinesFilter.ShowApprovalChip(awaitingCount, _status));
        SyncStatusChips();

        var runs = CommitsAndPipelinesFilter.Runs(all, _source, _status, _search, _repository, awaiting);
        _rows = runs.Select(run => new PipelineRunItem(run, awaiting(run))).ToList();
        _rendering = true;
        try
        {
            RunsList.ItemsSource = _rows;
            if (selectedKey != null) RunsList.SelectedItem = _rows.FirstOrDefault(row => PipelineRunCache.Key(row.Run) == selectedKey);
        }
        finally { _rendering = false; }

        if (RunPane.CurrentRun is { } shown && all.FirstOrDefault(run => PipelineRunCache.Key(run) == PipelineRunCache.Key(shown)) is { } current)
            RunPane.UpdateSummary(current);

        _summary = DevelopmentText.PipelinesSummary(_rows.Count, all);
        EmptyText.Visibility = DevelopmentUi.Show(_rows.Count == 0);
        EmptyText.Text = all.Count == 0 ? "No pipeline runs in the last 7 days." : "Nothing matches these filters.";
        UpdateCount();
    }

    private void UpdateCount() =>
        CountText.Text = _summary + App.Services.DevelopmentSync.StatusSuffix(DevelopmentRefreshArea.Pipelines);

    /// <summary>The count line, the active runs' running time and the shown run's summary follow each refresh.</summary>
    private void OnSyncStatusChanged()
    {
        UpdateCount();
        UpdateBusy();
        var services = App.Services;
        if (services.DevelopmentSync.IsLoading(DevelopmentRefreshArea.Pipelines)) return;
        foreach (var row in _rows.Where(row => row.Run.Status.IsActive())) row.RefreshTiming();
        if (RunPane.CurrentRun is { } shown
            && services.DevelopmentRuns?.FirstOrDefault(run => PipelineRunCache.Key(run) == PipelineRunCache.Key(shown)) is { } current)
            RunPane.UpdateSummary(current);
    }

    private void UpdateBusy()
    {
        var services = App.Services;
        var busy = services.DevelopmentRuns == null && services.DevelopmentSync.IsLoading(DevelopmentRefreshArea.Pipelines);
        if (busy == _busy) return;
        _busy = busy;
        BusyChanged?.Invoke(this, EventArgs.Empty);
    }

    // MARK: - Filters

    private void Source_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && Enum.TryParse<DevelopmentSourceFilter>(tag, out var source)) _source = source;
        _repository = null;
        SyncSourceChips();
        Render();
        AttachSync();
    }

    /// <summary>Status chips toggle: clicking the lit one clears it back to all.</summary>
    private void Status_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton chip && _statusChips.TryGetValue(chip, out var status))
            _status = _status == status ? PipelineStatusFilter.All : status;
        Render();
    }

    private void SyncSourceChips()
    {
        SourceAll.IsChecked = _source == DevelopmentSourceFilter.All;
        SourceDevOps.IsChecked = _source == DevelopmentSourceFilter.DevOps;
        SourceGitHub.IsChecked = _source == DevelopmentSourceFilter.GitHub;
    }

    /// <summary>One status chip lit at most, Approval included.</summary>
    private void SyncStatusChips()
    {
        foreach (var (chip, status) in _statusChips) chip.IsChecked = _status == status;
    }

    private void RepoFilter_SelectionChanged(object? sender, EventArgs e)
    {
        if (RepoFilter.Selection == _repository) return;
        _repository = RepoFilter.Selection;
        Render();
        AttachSync();
    }

    /// <summary>Show Pipelines filtered to one repository — the Pipelines graphs' repository bar.</summary>
    public void ShowRepository(string repository)
    {
        _source = DevelopmentSourceFilter.All;
        SyncSourceChips();
        _repository = repository;
        _page?.ShowSegment(Segment);
        Render();
        AttachSync();
    }

    /// <summary>Show Pipelines filtered to one status — the Failing and Running figures' click.</summary>
    public void ShowStatus(PipelineStatusFilter status)
    {
        _status = status;
        _page?.ShowSegment(Segment);
        Render();
        AttachSync();
    }

    // MARK: - Rows

    private async void RunsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering || RunsList.SelectedItem is not PipelineRunItem row) return;
        await ShowRunAsync(row.Run);
    }

    private async Task ShowRunAsync(PipelineRun run)
    {
        _linkAwaitingSignIn = null;
        AttachSync(run);
        DevelopmentContext.Select("pipelineRun", new[]
        {
            new ContextSelection("pipelineRun", PipelineRunCache.Key(run), new Dictionary<string, string?>
            {
                ["pipeline"] = run.PipelineName,
                ["run"] = run.RunNumber,
                ["repository"] = DevelopmentText.RunSubtitle(run),
                ["status"] = run.Status.DisplayName(),
                ["commit"] = run.CommitSha,
                ["url"] = run.WebUrl,
                ["source"] = run.Source.ToString(),
            }),
        });
        ShowDetail(RunPane);
        _layout.ShowDetail();
        if (RunPane.IsShowing(run)) RunPane.UpdateSummary(run);
        else await RunPane.ShowAsync(run);
    }

    private void ShowDetail(FrameworkElement shown)
    {
        foreach (var each in new FrameworkElement[] { Placeholder, RunPane, WaitHost })
            each.Visibility = DevelopmentUi.Show(ReferenceEquals(each, shown));
    }

    /// <summary>Right-click, Shift+F10 or the context key on a run: Review Approval…, Open in Browser, Copy Link.</summary>
    private void RunsList_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        var container = default(ListViewItem);
        for (var element = args.OriginalSource as DependencyObject; element != null && container == null; element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element))
            container = element as ListViewItem;
        if (container == null || RunsList.ItemFromContainer(container) is not PipelineRunItem row) return;
        args.Handled = true;

        var menu = new MenuFlyout();
        if (row.AwaitingApproval)
        {
            var review = new MenuFlyoutItem { Text = "Review Approval…", Icon = new FontIcon { Glyph = "" } };
            review.Click += async (_, _) => await ReviewApprovalAsync(row);
            menu.Items.Add(review);
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        var open = new MenuFlyoutItem { Text = "Open in Browser", Icon = new FontIcon { Glyph = "" } };
        open.Click += (_, _) => DevelopmentUi.OpenUrl(row.Run.WebUrl);
        var copy = new MenuFlyoutItem { Text = "Copy Link", Icon = new FontIcon { Glyph = "" } };
        copy.Click += (_, _) => DevelopmentUi.Copy(row.Run.WebUrl);
        menu.Items.Add(open);
        menu.Items.Add(copy);
        AgentHandOff.Prepend(menu, () => [AgentContexts.PipelineRun(row.Run)], container);
        if (args.TryGetPosition(container, out var point)) menu.ShowAt(container, new FlyoutShowOptions { Position = point });
        else menu.ShowAt(container);
    }

    /// <summary>The row's Review Approval…: show the run, then open its review.</summary>
    private async Task ReviewApprovalAsync(PipelineRunItem row)
    {
        RunsList.SelectedItem = row;
        if (!RunPane.IsShowing(row.Run)) await ShowRunAsync(row.Run);
        await RunPane.ReviewApprovalAsync();
    }

    /// <summary>
    /// Select a run and wait for its log — the QA capture's pipelines views: the
    /// first failed run, or with <paramref name="awaitingApproval"/> the first one an approval holds.
    /// </summary>
    public async Task SelectFirstAsync(bool awaitingApproval = false)
    {
        var row = awaitingApproval
            ? _rows.FirstOrDefault(r => r.AwaitingApproval)
            : _rows.FirstOrDefault(r => r.Status is PipelineRunStatus.Failed) ?? _rows.FirstOrDefault();
        if (row == null) return;
        _rendering = true;
        RunsList.SelectedItem = row;
        _rendering = false;
        await ShowRunAsync(row.Run);
    }

    // MARK: - Links and sign-in

    /// <summary>Signed in to Azure DevOps: reload runs loaded without it, then open a held link.</summary>
    private async void OnDevOpsSignedIn()
    {
        if (_shown && App.Services.DevelopmentRuns != null) await RefreshAsync();
        if (_linkAwaitingSignIn is not { } link) return;
        _linkAwaitingSignIn = null;
        Log.Information("[development] Signed in to Azure DevOps; opening {Link}", link.ToLink());
        try { await OpenLinkAsync(link); }
        catch (Exception ex) { Log.Warning(ex, "Could not open {Link}", link.ToLink()); }
    }

    /// <summary>A <c>fleetmate:</c> run or pipeline link: clear the filters and open the run, fetching it when it isn't loaded.</summary>
    public async Task OpenLinkAsync(FleetMateLink link)
    {
        _linkAwaitingSignIn = null;
        _source = DevelopmentSourceFilter.All;
        _status = PipelineStatusFilter.All;
        _repository = null;
        SyncSourceChips();
        var services = App.Services;
        var startedSignedIn = services.IsDevOpsSignedIn;
        if (services.DevelopmentRuns is null) await RefreshAsync();
        Render();

        var run = DevelopmentLinkTargets.Run(services.DevelopmentRuns ?? new List<PipelineRun>(), link);
        if (run != null && _rows.FirstOrDefault(r => r.Run.Id == run.Id) is { } row)
        {
            RunsList.SelectedItem = row;
            RunsList.ScrollIntoView(row);
            return;
        }

        // A pipeline the loaded runs don't cover opens on its latest run.
        run ??= services.DemoMode ? null : await FetchOrNullAsync(link);
        if (run == null)
        {
            await LinkFailedAsync(link, startedSignedIn);
            return;
        }
        _rendering = true;
        RunsList.SelectedItem = null;
        _rendering = false;
        await ShowRunAsync(run);
    }

    private static async Task<PipelineRun?> FetchOrNullAsync(FleetMateLink link)
    {
        try { return await CommitsAndPipelinesService.RunForLinkAsync(App.Services.Config, App.Services.DevOpsService, link); }
        catch (Exception ex)
        {
            Log.Information(ex, "[development] Fetching {Link} failed", link.ToLink());
            return null;
        }
    }

    /// <summary>
    /// A link that could not be found: hold it for Azure DevOps sign-in, open it
    /// again at once when sign-in landed meanwhile, or report it. GitHub links never wait.
    /// </summary>
    private async Task LinkFailedAsync(FleetMateLink link, bool startedSignedIn)
    {
        var retry = link.NeedsAzureDevOps() ? App.Services.DevOpsRetry(startedSignedIn) : SignInRetry.ShowError;
        switch (retry)
        {
            case SignInRetry.RetryNow:
                await OpenLinkAsync(link);
                break;
            case SignInRetry.WaitForSignIn:
                _linkAwaitingSignIn = link;
                Log.Information("[development] {Link} opens once Azure DevOps sign-in finishes", link.ToLink());
                WaitHost.Content = new DevOpsSignInWaitPanel("the linked run");
                ShowDetail(WaitHost);
                _layout.ShowDetail();
                break;
            default:
                _page?.ShowLinkNotFound(link);
                break;
        }
    }
}
