using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
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

    /// <summary>Raised after a rerun or cancel, so the list can refresh.</summary>
    public event EventHandler? RunChanged;

    public PipelineRunView()
    {
        InitializeComponent();
    }

    public async Task ShowAsync(PipelineRun run)
    {
        _run = run;
        ActionStatus.Visibility = Visibility.Collapsed;
        RenderHeader(run);
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

        var sha = run.CommitSha is { Length: > 8 } s ? s[..8] : run.CommitSha;
        DetailsText.Text = string.Join(" · ", new[]
        {
            sha,
            run.TriggeredBy,
            run.StartedAt?.ToLocalTime().ToString("d MMM, HH:mm"),
            PipelineRunRowViewModel.FormatDuration(run.Duration),
        }.Where(x => !string.IsNullOrEmpty(x)));

        // Cancel only while it can still be cancelled; rerun once it has
        // finished and there is something to rerun it from.
        CancelButton.Visibility = run.Status.IsActive() ? Visibility.Visible : Visibility.Collapsed;
        RerunButton.Visibility = !run.Status.IsActive()
                                 && (run.Source == PullRequestSource.GitHub || run.PipelineId != null)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async Task LoadLogAsync()
    {
        if (_run is not { } run) return;
        var generation = ++_generation;

        LoadingPanel.Visibility = Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;
        ContentScroller.Visibility = Visibility.Collapsed;

        try
        {
            var log = await FetchLogAsync(run);
            if (generation != _generation) return;

            LoadingPanel.Visibility = Visibility.Collapsed;
            ContentScroller.Visibility = Visibility.Visible;
            ContentScroller.ScrollToTop();
            SectionsList.ItemsSource = PipelineLogSectionViewModel.From(log);
            TruncationNotice.Visibility = log.Truncated ? Visibility.Visible : Visibility.Collapsed;

            if (log.Sections.Count == 0)
            {
                ContentScroller.Visibility = Visibility.Collapsed;
                ErrorText.Visibility = Visibility.Visible;
                ErrorText.Text = run.Status.IsActive() ? "No log yet — the run has not started a job." : "This run has no log.";
            }
        }
        catch (Exception ex)
        {
            if (generation != _generation) return;
            Log.Error(ex, "[pipelines] Failed to load the log for {Run}", run.Id);
            LoadingPanel.Visibility = Visibility.Collapsed;
            ErrorText.Visibility = Visibility.Visible;
            ErrorText.Text = $"Could not load this run's log.\n\n{ex.Message}";
        }
    }

    private static async Task<PipelineRunLog> FetchLogAsync(PipelineRun run)
    {
        var app = Application.Current as App ?? throw new InvalidOperationException("Application is not available");

        if (run.Source == PullRequestSource.AzureDevOps)
        {
            if (app.Config.AzureDevOps is not { } ado) throw new InvalidOperationException("Azure DevOps is not configured");
            using var devops = new AzureDevOpsService(ado);
            return await devops.GetPipelineRunLogAsync(run.Container, run.RunId);
        }

        using var actions = new GitHubActionsService(app.Config.GitHubProviderOrDefault());
        return await actions.GetPipelineRunLogAsync(run.Container, run.Repository ?? "", run.RunId);
    }

    private async void OnReloadClicked(object sender, RoutedEventArgs e) => await LoadLogAsync();

    private void OnOpenInBrowserClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_run?.WebUrl)) return;
        try { Process.Start(new ProcessStartInfo(_run.WebUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warning(ex, "[pipelines] Could not open {Url}", _run.WebUrl); }
    }

    private async void OnRerunClicked(object sender, RoutedEventArgs e)
    {
        if (_run is not { } run) return;
        if (!Confirm("Rerun pipeline?", $"{run.PipelineName} #{run.RunNumber}\n\nThis queues the run again on {run.Branch ?? "its branch"}."))
            return;

        await RunActionAsync("Rerun",
            gh => gh.RerunAsync(run.Container, run.Repository ?? "", run.RunId),
            ado => ado.RerunPipelineAsync(run.Container, run.PipelineId ?? 0, run.Branch));
    }

    private async void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        if (_run is not { } run) return;
        if (!Confirm("Cancel run?", $"{run.PipelineName} #{run.RunNumber}\n\nThis stops the run where it is."))
            return;

        await RunActionAsync("Cancel",
            gh => gh.CancelAsync(run.Container, run.Repository ?? "", run.RunId),
            ado => ado.CancelPipelineAsync(run.Container, run.RunId));
    }

    private async Task RunActionAsync(
        string action,
        Func<GitHubActionsService, Task<PullRequestActionResult>> github,
        Func<AzureDevOpsService, Task<PullRequestActionResult>> devops)
    {
        if (_run is not { } run || Application.Current is not App app) return;

        RerunButton.IsEnabled = CancelButton.IsEnabled = false;
        try
        {
            PullRequestActionResult result;
            if (run.Source == PullRequestSource.GitHub)
            {
                using var service = new GitHubActionsService(app.Config.GitHubProviderOrDefault());
                result = await github(service);
            }
            else
            {
                if (app.Config.AzureDevOps is not { } ado) return;
                using var service = new AzureDevOpsService(ado);
                result = await devops(service);
            }

            ActionStatus.Visibility = Visibility.Visible;
            ActionStatus.Text = result.Success ? $"{action} requested." : $"{action} failed: {result.Error}";
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

    private bool Confirm(string title, string message) =>
        MessageBox.Show(Window.GetWindow(this)!, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question)
        == MessageBoxResult.OK;
}
