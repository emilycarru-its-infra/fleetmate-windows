using System.Diagnostics;
using System.Windows;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using Serilog;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// The in-app pull request viewer — description, conversation, commits and
/// red/green file diffs, opened from a queue row instead of the browser.
///
/// One scroll rather than tabs: the Mac tried tabs first and moved away from
/// them, because reviewing a PR means reading the description, then the
/// commits, then the diff, and tabs make you hunt for which one holds the thing
/// you want.
/// </summary>
public partial class PullRequestDetailWindow : Window
{
    private readonly UnifiedPullRequest _pullRequest;

    /// <summary>Set when an action changed the PR, so the caller can refresh.</summary>
    public bool QueueNeedsRefresh { get; private set; }

    public PullRequestDetailWindow(UnifiedPullRequest pullRequest, Window? owner = null)
    {
        _pullRequest = pullRequest;

        InitializeComponent();

        Owner = owner;

        // Size to the host rather than a fixed guess — a diff is unreadable in a
        // small window and silly in a maximised one. Captured now because the
        // window cannot observe its owner after opening.
        if (owner != null)
        {
            Width = Math.Max(720, owner.ActualWidth * 0.8);
            Height = Math.Max(520, owner.ActualHeight * 0.8);
        }
        else
        {
            Width = 1000;
            Height = 720;
        }

        RenderHeader();
        Loaded += async (_, _) => await LoadAsync();
    }

    private void RenderHeader()
    {
        var row = new PullRequestRowViewModel { PullRequest = _pullRequest };

        Title = $"{_pullRequest.Reference} · {_pullRequest.Title}";
        TitleText.Text = _pullRequest.Title;
        BylineText.Text = $"{row.Byline} · {row.RepositoryLabel}";
        BranchText.Text = row.BranchLabel;

        StateText.Text = row.StateLabel;
        StateText.Foreground = row.StateBrush;

        ReviewerText.Text = _pullRequest.Reviewers.Count > 0
            ? $"Reviewers: {row.ReviewerLabel}"
            : "No reviewers";

        // The same rule as the queue row: these are Azure DevOps operations, and
        // only while the PR is still live.
        CompleteButton.Visibility = row.ActionVisibility;
        AbandonButton.Visibility = row.ActionVisibility;
    }

    private async Task LoadAsync()
    {
        try
        {
            var detail = await FetchAsync();
            Render(detail);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[pr-viewer] Failed to load {Reference}", _pullRequest.Reference);

            LoadingPanel.Visibility = Visibility.Collapsed;
            ErrorText.Visibility = Visibility.Visible;
            ErrorText.Text = $"Could not load this pull request.\n\n{ex.Message}";
        }
    }

    private async Task<PullRequestDetail> FetchAsync()
    {
        if (Application.Current is not App app)
            throw new InvalidOperationException("Application is not available");

        if (_pullRequest.Source == PullRequestSource.AzureDevOps)
        {
            if (app.Config.AzureDevOps is not { } adoConfig)
                throw new InvalidOperationException("Azure DevOps is not configured");

            using var service = new AzureDevOpsService(adoConfig);
            return await service.GetPullRequestDetailAsync(
                _pullRequest.Repository, _pullRequest.Number, _pullRequest.Container);
        }

        if (app.Config.Tasks?.Providers?.GitHub is not { } ghConfig)
            throw new InvalidOperationException("GitHub is not configured");

        using var github = new GitHubPullRequestService(ghConfig);
        return await github.GetPullRequestDetailAsync(
            _pullRequest.Container, _pullRequest.Repository, _pullRequest.Number);
    }

    private void Render(PullRequestDetail detail)
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        ContentScroller.Visibility = Visibility.Visible;

        // Description
        var body = PullRequestCommentViewModel.Strip(detail.Body);
        if (string.IsNullOrWhiteSpace(body))
        {
            DescriptionSection.Visibility = Visibility.Collapsed;
        }
        else
        {
            DescriptionText.Text = body;
        }

        // Conversation. System entries are kept but rendered grey — they are
        // context, so hiding them entirely loses the approval trail.
        var comments = detail.Comments
            .Select(c => new PullRequestCommentViewModel { Comment = c })
            .ToList();

        if (comments.Count == 0)
        {
            ConversationSection.Visibility = Visibility.Collapsed;
        }
        else
        {
            var human = detail.Conversation.Count();
            ConversationHeader.Text = $"Conversation ({human})";
            CommentsList.ItemsSource = comments;
        }

        CommentCountText.Text = detail.Comments.Count > 0
            ? $"{detail.Comments.Count} comments"
            : "";

        // Commits
        if (detail.Commits.Count == 0)
        {
            CommitsSection.Visibility = Visibility.Collapsed;
        }
        else
        {
            CommitsHeader.Text = $"Commits ({detail.Commits.Count})";
            CommitsList.ItemsSource = detail.Commits;
        }

        // Changes
        if (detail.Files.Count == 0)
        {
            ChangesSection.Visibility = Visibility.Collapsed;
        }
        else
        {
            ChangesHeader.Text =
                $"Changes ({detail.Files.Count} files, +{detail.Insertions} −{detail.Deletions})";

            FilesList.ItemsSource = detail.Files
                .Select(f => new DiffFileViewModel { File = f })
                .ToList();
        }

        // A capped file list is stated, not hidden — a diff that quietly omits
        // files is worse than one that admits it.
        if (detail.Truncated)
        {
            TruncationNotice.Visibility = Visibility.Visible;
            TruncationNotice.Text = "⚠ file list truncated";
        }
    }

    // MARK: - Actions

    private void OnOpenInBrowserClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_pullRequest.WebUrl)) return;

        try
        {
            Process.Start(new ProcessStartInfo(_pullRequest.WebUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[pr-viewer] Could not open {Url}", _pullRequest.WebUrl);
        }
    }

    private async void OnCompleteClicked(object sender, RoutedEventArgs e)
    {
        var row = new PullRequestRowViewModel { PullRequest = _pullRequest };

        // Same confirmation as the queue, for the same reason: completing merges
        // into a target branch and is not undoable from here.
        if (!Confirm("Complete pull request?",
                $"{_pullRequest.Title}\n\n{row.BranchLabel}\nin {row.RepositoryLabel}\n\n" +
                "This merges the pull request into its target branch."))
        {
            return;
        }

        await RunActionAsync("Complete", (service, pr) =>
            service.CompletePullRequestAsync(pr.Repository, pr.Number, pr.Container));
    }

    private async void OnAbandonClicked(object sender, RoutedEventArgs e)
    {
        var row = new PullRequestRowViewModel { PullRequest = _pullRequest };

        if (!Confirm("Abandon pull request?",
                $"{_pullRequest.Title}\n\n{row.BranchLabel}\nin {row.RepositoryLabel}\n\n" +
                "This closes the pull request without merging and notifies its reviewers."))
        {
            return;
        }

        await RunActionAsync("Abandon", (service, pr) =>
            service.AbandonPullRequestAsync(pr.Repository, pr.Number, pr.Container));
    }

    private async Task RunActionAsync(
        string action,
        Func<AzureDevOpsService, UnifiedPullRequest, Task<PullRequestActionResult>> perform)
    {
        if (Application.Current is not App app || app.Config.AzureDevOps is not { } adoConfig) return;

        CompleteButton.IsEnabled = false;
        AbandonButton.IsEnabled = false;

        try
        {
            using var service = new AzureDevOpsService(adoConfig);
            var result = await perform(service, _pullRequest);

            if (!result.Success)
            {
                MessageBox.Show(this, $"{action} failed:\n\n{result.Error}",
                    $"{action} pull request", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // The PR is no longer in the state this window is showing, so close
            // and let the queue reload rather than leaving a stale sheet open.
            QueueNeedsRefresh = true;
            Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[pr-viewer] {Action} failed for {Reference}", action, _pullRequest.Reference);
            MessageBox.Show(this, $"{action} failed:\n\n{ex.Message}",
                $"{action} pull request", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            CompleteButton.IsEnabled = true;
            AbandonButton.IsEnabled = true;
        }
    }

    private bool Confirm(string title, string message) =>
        MessageBox.Show(this, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question)
        == MessageBoxResult.OK;
}
