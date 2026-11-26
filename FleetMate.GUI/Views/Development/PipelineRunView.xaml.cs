using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Shared;
using Serilog;

namespace FleetMate.GUI.Views.Development;

/// <summary>
/// One pipeline run in the Development centre pane: status, branch, commit,
/// requester and timing, Rerun or Cancel, then the log as collapsible sections
/// (jobs on GitHub, task records on Azure DevOps).
/// </summary>
public partial class PipelineRunView : UserControl
{
    private PipelineRun? _run;
    private int _generation;
    private CancellationTokenSource? _logRequest;
    public PipelineRun? CurrentRun => _run;

    /// <summary>Raised after a rerun or cancel, so the list can refresh.</summary>
    public event EventHandler? RunChanged;

    public PipelineRunView()
    {
        InitializeComponent();
        Unloaded += (_, _) => CancelPendingLog();
        IsVisibleChanged += (_, _) => { if (!IsVisible) CancelPendingLog(); };
    }

    public bool IsShowing(PipelineRun run) => _run != null && PipelineRunCache.Key(_run) == PipelineRunCache.Key(run);

    public void UpdateSummary(PipelineRun run)
    {
        if (!IsShowing(run)) return;
        _run = run;
        RenderHeader(run);
    }

    public void CancelPendingLog()
    {
        var pending = _logRequest != null;
        _logRequest?.Cancel();
        _logRequest = null;
        _generation++;
        if (pending)
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            ContentScroller.Visibility = SectionsList.ItemsSource != null ? Visibility.Visible : Visibility.Collapsed;
            ErrorText.Text = PipelineRunDetailText.LogPaused;
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    public void Clear()
    {
        CancelPendingLog();
        _run = null;
        SectionsList.ItemsSource = null;
        LoadingPanel.Visibility = ContentScroller.Visibility = ErrorText.Visibility = Visibility.Collapsed;
        TitleText.Text = WhereText.Text = DetailsText.Text = StatusText.Text = "";
    }

    public async Task ShowAsync(PipelineRun run)
    {
        if (!IsShowing(run))
        {
            CancelPendingLog();
            SectionsList.ItemsSource = null;
            ContentScroller.Visibility = Visibility.Collapsed;
        }
        _run = run;
        AgentButton.Context = AgentContexts.PipelineRun(run);
        ActionStatus.Visibility = Visibility.Collapsed;
        RenderHeader(run);
        _ = RefreshApprovalsForAsync(run);
        await LoadLogAsync();
    }

    private void RenderHeader(PipelineRun run)
    {
        var row = new PipelineRunRowViewModel { Run = run };

        TitleText.Text = row.Title;
        WhereText.Text = row.Subtitle;
        StatusIcon.Glyph = row.StatusGlyph;
        StatusIcon.Foreground = row.StatusBrush;
        StatusText.Text = row.StatusLabel;
        RenderApprovals(run);

        DetailsText.Text = PipelineRunDetailText.Details(run);
        CancelButton.Visibility = PipelineRunDetailText.CanCancel(run) ? Visibility.Visible : Visibility.Collapsed;
        RerunButton.Visibility = PipelineRunDetailText.CanRerun(run) ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadLogAsync()
    {
        if (!IsVisible || _run is not { } run) return;
        CancelPendingLog();
        var request = _logRequest = new CancellationTokenSource();
        var generation = ++_generation;

        LoadingPanel.Visibility = Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;
        ContentScroller.Visibility = Visibility.Collapsed;

        try
        {
            var log = await FetchLogAsync(run, request.Token);
            if (generation != _generation || request.IsCancellationRequested) return;

            LoadingPanel.Visibility = Visibility.Collapsed;
            ContentScroller.Visibility = Visibility.Visible;
            ContentScroller.ScrollToTop();
            SectionsList.ItemsSource = PipelineLogSectionViewModel.From(log);
            TruncationNotice.Visibility = log.Truncated ? Visibility.Visible : Visibility.Collapsed;

            if (log.Sections.Count == 0)
            {
                ContentScroller.Visibility = Visibility.Collapsed;
                ErrorText.Visibility = Visibility.Visible;
                ErrorText.Text = PipelineRunDetailText.EmptyLog(run);
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (generation != _generation || request.IsCancellationRequested) return;
            Log.Error(ex, "[pipelines] Failed to load the log for {Run}", run.Id);
            LoadingPanel.Visibility = Visibility.Collapsed;
            ErrorText.Visibility = Visibility.Visible;
            ErrorText.Text = $"Could not load this run's log.\n\n{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_logRequest, request)) _logRequest = null;
            request.Dispose();
        }
    }

    private static Task<PipelineRunLog> FetchLogAsync(PipelineRun run, CancellationToken ct)
    {
        var app = Application.Current as App ?? throw new InvalidOperationException("Application is not available");
        return CommitsAndPipelinesService.RunLogAsync(app.Config, run, ct);
    }

    private async void OnReloadClicked(object sender, RoutedEventArgs e) => await LoadLogAsync();

    private void OnOpenInBrowserClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_run?.WebUrl)) return;
        FleetMate.Core.Links.ExternalBrowser.Open(_run.WebUrl);
    }

    private async void OnRerunClicked(object sender, RoutedEventArgs e)
    {
        if (_run is not { } run) return;
        if (!Confirm("Rerun pipeline?", PipelineRunDetailText.RerunPrompt(run))) return;
        await RunActionAsync("Rerun", CommitsAndPipelinesService.RerunAsync);
    }

    private async void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        if (_run is not { } run) return;
        if (!Confirm("Cancel run?", PipelineRunDetailText.CancelPrompt(run))) return;
        await RunActionAsync("Cancel", CommitsAndPipelinesService.CancelAsync);
    }

    private async Task RunActionAsync(string action,
        Func<FleetMate.Core.Config.FleetMateConfig, PipelineRun, Task<PullRequestActionResult?>> act)
    {
        if (_run is not { } run || Application.Current is not App app) return;

        RerunButton.IsEnabled = CancelButton.IsEnabled = false;
        try
        {
            if (await act(app.Config, run) is not { } result) return;

            ActionStatus.Visibility = Visibility.Visible;
            ActionStatus.Text = PipelineRunDetailText.ActionResult(action, result);
            ActionStatus.Foreground = result.Success
                ? (Brush)FindResource("SystemControlForegroundBaseMediumBrush")
                : new SolidColorBrush(Color.FromRgb(0xE0, 0x7A, 0x1F));

            if (result.Success) RunChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            RerunButton.IsEnabled = CancelButton.IsEnabled = true;
        }
    }

    // MARK: - Approvals

    private static PipelineApprovalTracker? Approvals => (Application.Current as App)?.PipelineApprovals;

    private IReadOnlyList<PipelineApproval> CurrentApprovals =>
        _run is { } run && Approvals is { } tracker ? tracker.For(run) : Array.Empty<PipelineApproval>();

    /// <summary>Re-draw the badge, status and banner after the tracker changed.</summary>
    public void RefreshApprovals()
    {
        if (_run is { } run) RenderHeader(run);
    }

    private void RenderApprovals(PipelineRun run)
    {
        var approvals = Approvals?.For(run) ?? Array.Empty<PipelineApproval>();
        var waiting = approvals.Count > 0;
        HeaderApprovalBadge.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        ApprovalBanner.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        ApprovalBannerText.Text = waiting ? PipelineApprovalTracker.BannerText(approvals.Count) : "";
        if (waiting)
        {
            StatusText.Text = PipelineStatusVisuals.WaitingForApproval;
            StatusIcon.Foreground = new SolidColorBrush(PipelineStatusVisuals.ApprovalColor);
        }
    }

    /// <summary>Opening a run re-reads its project's approvals, so the banner is current.</summary>
    private static async Task RefreshApprovalsForAsync(PipelineRun run)
    {
        if (Application.Current is not App app) return;
        try
        {
            await CommitsAndPipelinesService.RefreshApprovalsAsync(app.Config, app.PipelineApprovals, app.DevelopmentRuns, run);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[approvals] refresh for {Run} failed", run.Id);
        }
    }

    private void OnReviewApprovalClicked(object sender, RoutedEventArgs e) => ReviewApproval();

    /// <summary>Open the review for this run's first pending approval, if it has one.</summary>
    public void ReviewApproval()
    {
        if (_run is not { } run || CurrentApprovals.Count == 0) return;
        var window = new ApprovalReviewWindow(run, () => CurrentApprovals) { Owner = Window.GetWindow(this) };
        window.Responded += (_, _) => RunChanged?.Invoke(this, EventArgs.Empty);
        window.ShowDialog();
    }

    private bool Confirm(string title, string message) =>
        MessageBox.Show(Window.GetWindow(this)!, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question)
        == MessageBoxResult.OK;
}
