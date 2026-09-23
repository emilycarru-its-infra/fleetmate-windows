using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using Serilog;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// The pull request viewer — header, checks, review composer and actions, then
/// description, conversation, commits and red/green diffs in one scroll.
///
/// A control rather than a window so the same view serves the dashboard's
/// pop-out <see cref="PullRequestDetailWindow"/> and the Development tab's inline
/// right pane.
/// </summary>
public partial class PullRequestDetailView : UserControl
{
    private UnifiedPullRequest? _pullRequest;
    private int _loadGeneration;

    /// <summary>
    /// Raised after an action that changes the PR's state (merge, close, draft
    /// toggle). The PR object this view holds is stale from that point, so the
    /// host should refresh its list — or, for a window, close.
    /// </summary>
    public event EventHandler? StateChanged;

    public PullRequestDetailView()
    {
        InitializeComponent();
    }

    public UnifiedPullRequest? PullRequest => _pullRequest;

    /// <summary>Show a pull request, replacing whatever was shown before.</summary>
    public async Task ShowAsync(UnifiedPullRequest pullRequest)
    {
        _pullRequest = pullRequest;
        ComposerBox.Text = "";
        HideStatus();
        RenderHeader();
        await LoadAsync();
    }

    private void RenderHeader()
    {
        if (_pullRequest is not { } pr) return;

        var row = new PullRequestRowViewModel { PullRequest = pr };
        var plan = new PullRequestActionPlan { PullRequest = pr };

        TitleText.Text = pr.Title;
        BylineText.Text = $"{row.Byline} · {row.RepositoryLabel}";
        BranchText.Text = row.BranchLabel;

        StateText.Text = row.StateLabel;
        StateText.Foreground = row.StateBrush;

        ReviewerText.Text = pr.Reviewers.Count > 0 ? $"Reviewers: {row.ReviewerLabel}" : "No reviewers";
        CommentCountText.Text = "";

        ActionPanel.Visibility = PullRequestCheckViewModel.Show(plan.IsLive);
        CommentButton.Visibility = PullRequestCheckViewModel.Show(plan.CanComment);
        ApproveButton.Visibility = PullRequestCheckViewModel.Show(plan.CanReview);
        RequestChangesButton.Visibility = PullRequestCheckViewModel.Show(plan.CanReview);
        MergeButton.Visibility = PullRequestCheckViewModel.Show(plan.CanMerge);
        MergeButton.Content = plan.MergeLabel;
        MergeMethodCombo.Visibility = PullRequestCheckViewModel.Show(plan.ShowMergeMethod);
        DraftButton.Visibility = PullRequestCheckViewModel.Show(plan.CanToggleDraft);
        DraftButton.Content = plan.DraftLabel;
        CloseButton.Visibility = PullRequestCheckViewModel.Show(plan.CanClose);
        CloseButton.Content = plan.CloseLabel;

        ChecksSummary.Text = "Loading checks…";
        ChecksList.ItemsSource = null;
    }

    private async Task LoadAsync()
    {
        if (_pullRequest is not { } pr) return;

        // A slow load for a PR the operator has already moved past must not
        // overwrite the one now selected.
        var generation = ++_loadGeneration;

        LoadingPanel.Visibility = Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;
        ContentScroller.Visibility = Visibility.Collapsed;

        var checksTask = FetchChecksAsync(pr);

        try
        {
            var detail = await FetchDetailAsync(pr);
            if (generation != _loadGeneration) return;
            Render(detail);
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration) return;
            Log.Error(ex, "[pr-viewer] Failed to load {Reference}", pr.Reference);

            LoadingPanel.Visibility = Visibility.Collapsed;
            ErrorText.Visibility = Visibility.Visible;
            ErrorText.Text = $"Could not load this pull request.\n\n{ex.Message}";
        }

        try
        {
            var checks = await checksTask;
            if (generation != _loadGeneration) return;
            ChecksSummary.Text = PullRequestCheckViewModel.Summary(checks);
            ChecksList.ItemsSource = checks
                .OrderBy(c => c.State == PullRequestCheckState.Failure ? 0 : c.State == PullRequestCheckState.Pending ? 1 : 2)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => new PullRequestCheckViewModel { Check = c })
                .ToList();
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration) return;
            Log.Warning(ex, "[pr-viewer] Checks unavailable for {Reference}", pr.Reference);
            ChecksSummary.Text = "Checks unavailable";
        }
    }

    private static App RequireApp() =>
        Application.Current as App ?? throw new InvalidOperationException("Application is not available");

    private static async Task<PullRequestDetail> FetchDetailAsync(UnifiedPullRequest pr)
    {
        var app = RequireApp();

        if (pr.Source == PullRequestSource.AzureDevOps)
        {
            if (app.Config.AzureDevOps is not { } adoConfig)
                throw new InvalidOperationException("Azure DevOps is not configured");

            using var service = new AzureDevOpsService(adoConfig);
            return await service.GetPullRequestDetailAsync(pr.Repository, pr.Number, pr.Container);
        }

        using var github = new GitHubPullRequestService(app.Config.GitHubProviderOrDefault());
        return await github.GetPullRequestDetailAsync(pr.Container, pr.Repository, pr.Number);
    }

    private static async Task<List<PullRequestCheck>> FetchChecksAsync(UnifiedPullRequest pr)
    {
        var app = RequireApp();

        if (pr.Source == PullRequestSource.AzureDevOps)
        {
            if (app.Config.AzureDevOps is not { } adoConfig) return new();
            using var service = new AzureDevOpsService(adoConfig);
            return await service.GetPullRequestChecksAsync(pr.Repository, pr.Number, pr.Container);
        }

        using var github = new GitHubPullRequestService(app.Config.GitHubProviderOrDefault());
        return await github.GetChecksAsync(pr.Container, pr.Repository, pr.Number);
    }

    private void Render(PullRequestDetail detail)
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        ContentScroller.Visibility = Visibility.Visible;
        ContentScroller.ScrollToTop();

        var body = PullRequestCommentViewModel.Strip(detail.Body);
        DescriptionSection.Visibility = PullRequestCheckViewModel.Show(!string.IsNullOrWhiteSpace(body));
        DescriptionText.Text = body;

        // System entries are kept but rendered grey — they are context, so
        // hiding them entirely loses the approval trail.
        var comments = detail.Comments.Select(c => new PullRequestCommentViewModel { Comment = c }).ToList();
        ConversationSection.Visibility = PullRequestCheckViewModel.Show(comments.Count > 0);
        ConversationHeader.Text = $"Conversation ({detail.Conversation.Count()})";
        CommentsList.ItemsSource = comments;

        CommentCountText.Text = detail.Comments.Count > 0 ? $"{detail.Comments.Count} comments" : "";

        CommitsSection.Visibility = PullRequestCheckViewModel.Show(detail.Commits.Count > 0);
        CommitsHeader.Text = $"Commits ({detail.Commits.Count})";
        CommitsList.ItemsSource = detail.Commits;

        ChangesSection.Visibility = PullRequestCheckViewModel.Show(detail.Files.Count > 0);
        ChangesHeader.Text = $"Changes ({detail.Files.Count} files, +{detail.Insertions} −{detail.Deletions})";
        FilesList.ItemsSource = detail.Files.Select(f => new DiffFileViewModel { File = f }).ToList();

        // A capped file list is stated, not hidden.
        TruncationNotice.Visibility = PullRequestCheckViewModel.Show(detail.Truncated);
        TruncationNotice.Text = "⚠ file list truncated";
    }

    // MARK: - Browse

    private void OnOpenInBrowserClicked(object sender, RoutedEventArgs e) => OpenUrl(_pullRequest?.WebUrl);

    private void OnCopyLinkClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_pullRequest?.WebUrl)) return;
        try
        {
            Clipboard.SetText(_pullRequest.WebUrl);
            ShowStatus("Link copied.", false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[pr-viewer] Clipboard unavailable");
        }
    }

    private void OnCheckClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PullRequestCheckViewModel check }) OpenUrl(check.DetailsUrl);
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warning(ex, "[pr-viewer] Could not open {Url}", url); }
    }

    // MARK: - Review actions

    private async void OnCommentClicked(object sender, RoutedEventArgs e)
    {
        var body = ComposerBox.Text.Trim();
        if (body.Length == 0)
        {
            ShowStatus("Write the comment first.", true);
            return;
        }

        await RunAsync("Comment", terminal: false,
            (gh, pr) => gh.CommentAsync(pr.Container, pr.Repository, pr.Number, body),
            (ado, pr) => ado.CommentPullRequestAsync(pr.Repository, pr.Number, body, pr.Container));
    }

    private async void OnApproveClicked(object sender, RoutedEventArgs e)
    {
        var body = ComposerBox.Text.Trim();

        await RunAsync("Approve", terminal: false,
            (gh, pr) => gh.ApproveAsync(pr.Container, pr.Repository, pr.Number, body),
            async (ado, pr) =>
            {
                var vote = await ado.ApprovePullRequestAsync(pr.Repository, pr.Number, pr.Container);
                return vote.Success && body.Length > 0
                    ? await ado.CommentPullRequestAsync(pr.Repository, pr.Number, body, pr.Container)
                    : vote;
            });
    }

    private async void OnRequestChangesClicked(object sender, RoutedEventArgs e)
    {
        var body = ComposerBox.Text.Trim();

        // GitHub rejects REQUEST_CHANGES without a body, and on either side a
        // bare "changes requested" leaves the author guessing.
        if (body.Length == 0)
        {
            ShowStatus("Say what needs to change in the box first.", true);
            return;
        }

        await RunAsync("Request changes", terminal: false,
            (gh, pr) => gh.RequestChangesAsync(pr.Container, pr.Repository, pr.Number, body),
            (ado, pr) => ado.RequestChangesPullRequestAsync(pr.Repository, pr.Number, pr.Container, body));
    }

    // MARK: - State actions

    private async void OnMergeClicked(object sender, RoutedEventArgs e)
    {
        if (_pullRequest is not { } pr) return;

        var method = MergeMethodCombo.SelectedItem is ComboBoxItem { Tag: string tag }
                     && Enum.TryParse<PullRequestMergeMethod>(tag, out var parsed)
            ? parsed
            : PullRequestMergeMethod.Squash;

        var plan = new PullRequestActionPlan { PullRequest = pr };
        var how = pr.Source == PullRequestSource.GitHub
            ? $"This {method.GitHubValue()}-merges into {pr.TargetBranch}."
            : $"This completes the pull request into {pr.TargetBranch} with its own merge settings.";

        if (!Confirm($"{plan.MergeLabel} pull request?", $"{pr.Title}\n\n{pr.Container}/{pr.Repository} {pr.Reference}\n\n{how}"))
            return;

        await RunAsync(plan.MergeLabel, terminal: true,
            (gh, p) => gh.MergeAsync(p.Container, p.Repository, p.Number, method),
            (ado, p) => ado.CompletePullRequestAsync(p.Repository, p.Number, p.Container));
    }

    private async void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        if (_pullRequest is not { } pr) return;
        var plan = new PullRequestActionPlan { PullRequest = pr };

        if (!Confirm($"{plan.CloseLabel} pull request?",
                $"{pr.Title}\n\nThis closes the pull request without merging and notifies its reviewers."))
            return;

        await RunAsync(plan.CloseLabel, terminal: true,
            (gh, p) => gh.CloseAsync(p.Container, p.Repository, p.Number),
            (ado, p) => ado.AbandonPullRequestAsync(p.Repository, p.Number, p.Container));
    }

    private async void OnDraftClicked(object sender, RoutedEventArgs e)
    {
        if (_pullRequest is not { } pr) return;
        var ready = pr.State == PullRequestState.Draft;

        await RunAsync(ready ? "Mark ready" : "Convert to draft", terminal: true,
            (gh, p) => gh.SetReadyAsync(p.NodeId, ready),
            (ado, p) => ado.SetPullRequestReadyAsync(p.Repository, p.Number, ready, p.Container));
    }

    /// <summary>
    /// Run one action against whichever provider owns the PR. Non-terminal
    /// actions (comment, votes) reload the view so the new entry shows; terminal
    /// ones hand back to the host, whose PR object is now stale.
    /// </summary>
    private async Task RunAsync(
        string action,
        bool terminal,
        Func<GitHubPullRequestService, UnifiedPullRequest, Task<PullRequestActionResult>> github,
        Func<AzureDevOpsService, UnifiedPullRequest, Task<PullRequestActionResult>> devops)
    {
        if (_pullRequest is not { } pr) return;
        var app = RequireApp();

        ActionPanel.IsEnabled = false;
        ShowStatus($"{action}…", false);

        try
        {
            PullRequestActionResult result;

            if (pr.Source == PullRequestSource.GitHub)
            {
                using var service = new GitHubPullRequestService(app.Config.GitHubProviderOrDefault());
                result = await github(service, pr);
            }
            else
            {
                if (app.Config.AzureDevOps is not { } adoConfig)
                {
                    ShowStatus("Azure DevOps is not configured.", true);
                    return;
                }
                using var service = new AzureDevOpsService(adoConfig);
                result = await devops(service, pr);
            }

            if (!result.Success)
            {
                ShowStatus($"{action} failed: {result.Error}", true);
                return;
            }

            ComposerBox.Text = "";
            ShowStatus($"{action} done.", false);

            if (terminal)
            {
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                await LoadAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[pr-viewer] {Action} failed for {Reference}", action, pr.Reference);
            ShowStatus($"{action} failed: {ex.Message}", true);
        }
        finally
        {
            ActionPanel.IsEnabled = true;
        }
    }

    private void ShowStatus(string message, bool isError)
    {
        ActionStatus.Visibility = Visibility.Visible;
        ActionStatus.Text = message;
        ActionStatus.Foreground = isError
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0x7A, 0x1F))
            : (System.Windows.Media.Brush)FindResource("SystemControlForegroundBaseMediumBrush");
    }

    private void HideStatus() => ActionStatus.Visibility = Visibility.Collapsed;

    private bool Confirm(string title, string message) =>
        MessageBox.Show(Window.GetWindow(this)!, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question)
        == MessageBoxResult.OK;
}
