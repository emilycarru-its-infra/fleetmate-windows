using FleetMate.Core.Development;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace FleetMate.WinUI.Pages.Development.Pipelines;

/// <summary>
/// One pipeline run: status, branch, commit, requester and timing, Rerun or
/// Cancel, the approval banner, then the log as collapsible sections (jobs on
/// GitHub, task records on Azure DevOps), failed ones open.
/// </summary>
public sealed partial class PipelineRunPane : UserControl
{
    private PipelineRun? _run;
    private int _generation;
    private CancellationTokenSource? _logRequest;
    private bool _hasLog;

    public PipelineRunPane()
    {
        InitializeComponent();
        Unloaded += (_, _) => CancelPendingLog();
    }

    public PipelineRun? CurrentRun => _run;

    /// <summary>Raised after a rerun, a cancel or an approval answer, so the list can refresh.</summary>
    public event EventHandler? RunChanged;

    public bool IsShowing(PipelineRun run) => _run != null && PipelineRunCache.Key(_run) == PipelineRunCache.Key(run);

    /// <summary>The list has a newer copy of the run shown: redraw the header, keep the log.</summary>
    public void UpdateSummary(PipelineRun run)
    {
        if (!IsShowing(run)) return;
        _run = run;
        RenderHeader(run);
    }

    /// <summary>Stop a log still loading, e.g. when Pipelines is hidden; the log shown so far stays.</summary>
    public void CancelPendingLog()
    {
        var pending = _logRequest != null;
        _logRequest?.Cancel();
        _logRequest = null;
        _generation++;
        if (!pending) return;
        LoadingPanel.Visibility = Visibility.Collapsed;
        ContentScroller.Visibility = DevelopmentUi.Show(_hasLog);
        ErrorText.Text = PipelineRunDetailText.LogPaused;
        ErrorText.Visibility = Visibility.Visible;
    }

    public async Task ShowAsync(PipelineRun run)
    {
        if (!IsShowing(run))
        {
            CancelPendingLog();
            SectionsList.ItemsSource = null;
            _hasLog = false;
            ContentScroller.Visibility = Visibility.Collapsed;
        }
        _run = run;
        ActionStatus.Visibility = Visibility.Collapsed;
        RenderHeader(run);
        _ = RefreshApprovalsAsync(run);
        await LoadLogAsync();
    }

    private void RenderHeader(PipelineRun run)
    {
        var waiting = Approvals.Count > 0;
        TitleText.Text = DevelopmentText.RunTitle(run);
        WhereText.Text = DevelopmentText.RunSubtitle(run);
        StatusIcon.Status = run.Status;
        StatusIcon.AwaitingApproval = waiting;
        StatusText.Text = DevelopmentText.RunStatusLabel(run, waiting);
        DetailsText.Text = PipelineRunDetailText.Details(run);
        CancelButton.Visibility = DevelopmentUi.Show(PipelineRunDetailText.CanCancel(run));
        RerunButton.Visibility = DevelopmentUi.Show(PipelineRunDetailText.CanRerun(run));
        ApprovalBar.IsOpen = waiting;
        ApprovalBar.Message = waiting ? PipelineApprovalTracker.BannerText(Approvals.Count) : "";
    }

    // MARK: - Log

    private async Task LoadLogAsync()
    {
        if (_run is not { } run) return;
        CancelPendingLog();
        var request = _logRequest = new CancellationTokenSource();
        var generation = ++_generation;

        LoadingPanel.Visibility = Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;
        ContentScroller.Visibility = Visibility.Collapsed;

        try
        {
            var services = App.Services;
            var log = services.DemoMode
                ? DevelopmentDemoData.RunLog(run)
                : await CommitsAndPipelinesService.RunLogAsync(services.Config, run, request.Token);
            if (generation != _generation || request.IsCancellationRequested) return;

            LoadingPanel.Visibility = Visibility.Collapsed;
            SectionsList.ItemsSource = LogSectionItem.From(log);
            _hasLog = log.Sections.Count > 0;
            TruncationText.Text = PipelineRunDetailText.LogTruncated;
            TruncationNotice.Visibility = DevelopmentUi.Show(log.Truncated);
            ContentScroller.Visibility = DevelopmentUi.Show(_hasLog);
            ContentScroller.ChangeView(null, 0, null, disableAnimation: true);
            if (!_hasLog)
            {
                ErrorText.Text = PipelineRunDetailText.EmptyLog(run);
                ErrorText.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (generation != _generation || request.IsCancellationRequested) return;
            Log.Error(ex, "[pipelines] Failed to load the log for {Run}", run.Id);
            LoadingPanel.Visibility = Visibility.Collapsed;
            ErrorText.Text = $"Could not load this run's log.\n\n{ex.Message}";
            ErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            if (ReferenceEquals(_logRequest, request)) _logRequest = null;
            request.Dispose();
        }
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await LoadLogAsync();

    // MARK: - Actions

    private void OpenInBrowser_Click(object sender, RoutedEventArgs e) => DevelopmentUi.OpenUrl(_run?.WebUrl);

    private void CopyForAgent_Click(object sender, RoutedEventArgs e)
    {
        if (_run is { } run) DevelopmentUi.Copy(AgentContexts.PipelineRun(run).Markdown);
    }

    private async void Rerun_Click(object sender, RoutedEventArgs e)
    {
        if (_run is not { } run) return;
        if (await ConfirmAsync("Rerun pipeline?", PipelineRunDetailText.RerunPrompt(run), "Rerun"))
            await RunActionAsync("Rerun", CommitsAndPipelinesService.RerunAsync);
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_run is not { } run) return;
        if (await ConfirmAsync("Cancel run?", PipelineRunDetailText.CancelPrompt(run), "Cancel Run"))
            await RunActionAsync("Cancel", CommitsAndPipelinesService.CancelAsync);
    }

    private async Task RunActionAsync(string action,
        Func<Core.Config.FleetMateConfig, PipelineRun, Task<PullRequestActionResult?>> act)
    {
        if (_run is not { } run) return;
        var services = App.Services;
        RerunButton.IsEnabled = CancelButton.IsEnabled = false;
        try
        {
            // Demo mode changes nothing anywhere.
            var result = services.DemoMode ? PullRequestActionResult.Ok() : await act(services.Config, run);
            if (result is null) return;
            ActionStatus.Text = PipelineRunDetailText.ActionResult(action, result);
            ActionStatus.Foreground = DevelopmentUi.Theme(result.Success ? "TextFillColorSecondaryBrush" : "SystemFillColorCautionBrush");
            ActionStatus.Visibility = Visibility.Visible;
            if (result.Success) RunChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[pipelines] {Action} failed for {Run}", action, run.Id);
            ActionStatus.Text = $"{action} failed: {ex.Message}";
            ActionStatus.Foreground = DevelopmentUi.Theme("SystemFillColorCautionBrush");
            ActionStatus.Visibility = Visibility.Visible;
        }
        finally
        {
            RerunButton.IsEnabled = CancelButton.IsEnabled = true;
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message, string action)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = action,
            CloseButtonText = "Keep",
            DefaultButton = ContentDialogButton.Close,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    // MARK: - Approvals

    private IReadOnlyList<PipelineApproval> Approvals =>
        _run is { } run ? App.Services.PipelineApprovals.For(run) : Array.Empty<PipelineApproval>();

    /// <summary>Redraw the badge, status and banner after the tracker changed.</summary>
    public void RefreshApprovals()
    {
        if (_run is { } run) RenderHeader(run);
    }

    /// <summary>Opening a run re-reads its project's approvals, so the banner is current.</summary>
    private static async Task RefreshApprovalsAsync(PipelineRun run)
    {
        var services = App.Services;
        if (services.DemoMode) return;
        try
        {
            await CommitsAndPipelinesService.RefreshApprovalsAsync(services.Config, services.PipelineApprovals, services.DevelopmentRuns, run);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[approvals] refresh for {Run} failed", run.Id);
        }
    }

    private async void Review_Click(object sender, RoutedEventArgs e) => await ReviewApprovalAsync();

    /// <summary>Open the review for this run's pending approvals, if it has any.</summary>
    public async Task ReviewApprovalAsync()
    {
        if (_run is not { } run || Approvals.Count == 0) return;
        var dialog = new ApprovalReviewDialog(run, () => App.Services.PipelineApprovals.For(run)) { XamlRoot = XamlRoot };
        dialog.Responded += (_, _) => RunChanged?.Invoke(this, EventArgs.Empty);
        await dialog.ShowAsync();
    }
}
