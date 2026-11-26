using FleetMate.Core.Development;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Serilog;
using Windows.ApplicationModel.DataTransfer;

namespace FleetMate.WinUI.Pages.Development.Pulls;

/// <summary>
/// The pull request viewer — header, review composer and actions, then
/// description, conversation, checks, commits and red/green diffs in one
/// scroll — as FleetMate.GUI's <c>PullRequestDetailView</c>. The action rules
/// (<see cref="PullRequestActionPlan"/>) and layout
/// (<see cref="PullRequestDetailLayout"/>) are FleetMate.Core's, shared with it.
/// </summary>
public sealed partial class PullRequestDetailView : UserControl
{
    private UnifiedPullRequest? _pullRequest;
    private int _loadGeneration;

    /// <summary>
    /// Pull requests whose details came back Not Found this session. Search can
    /// list one the REST token cannot read (a private fork, say); asking again
    /// only spends rate-limit budget on the same answer.
    /// </summary>
    private static readonly Dictionary<string, string> Unreadable = new();

    /// <summary>
    /// Raised after an action that changes the PR's state (merge, close, draft
    /// toggle). The PR this view holds is stale from then, so the host reloads its list.
    /// </summary>
    public event EventHandler? StateChanged;

    public PullRequestDetailView() => InitializeComponent();

    public UnifiedPullRequest? PullRequest => _pullRequest;

    /// <summary>Show a pull request, replacing whatever was shown before.</summary>
    public async Task ShowAsync(UnifiedPullRequest pullRequest)
    {
        _pullRequest = pullRequest;
        ComposerBox.Text = "";
        ActionStatus.IsOpen = false;
        RenderHeader();
        await LoadAsync();
    }

    private void RenderHeader()
    {
        if (_pullRequest is not { } pr) return;
        var plan = new PullRequestActionPlan { PullRequest = pr };

        TitleText.Text = pr.Title;
        BylineText.Text = $"{DevelopmentText.PullRequestByline(pr)} · {PullRequestPresentation.RepositoryLabel(pr)}";
        BranchText.Text = PullRequestPresentation.BranchLabel(pr);
        StateText.Text = DevelopmentText.StateLabel(pr);
        StateText.Foreground = DevelopmentUi.StateBrush(pr);
        ReviewerText.Text = pr.Reviewers.Count > 0 ? $"Reviewers: {PullRequestPresentation.ReviewerLabel(pr)}" : "No reviewers";
        CommentCountText.Text = "";

        ActionPanel.Visibility = DevelopmentUi.Show(plan.IsLive);
        CommentButton.Visibility = DevelopmentUi.Show(plan.CanComment);
        ApproveButton.Visibility = DevelopmentUi.Show(plan.CanReview);
        RequestChangesButton.Visibility = DevelopmentUi.Show(plan.CanReview);
        MergeButton.Visibility = DevelopmentUi.Show(plan.CanMerge);
        MergeButton.Content = plan.MergeLabel;
        MergeMethodBox.Visibility = DevelopmentUi.Show(plan.ShowMergeMethod);
        DraftButton.Visibility = DevelopmentUi.Show(plan.CanToggleDraft);
        DraftButton.Content = plan.DraftLabel;
        CloseButton.Visibility = DevelopmentUi.Show(plan.CanClose);
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

        if (Unreadable.TryGetValue(pr.Id, out var notFound))
        {
            ShowLoadError(notFound);
            ChecksSummary.Text = "Checks unavailable";
            return;
        }

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
            if (ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound })
                Unreadable[pr.Id] = "This pull request cannot be read with the current GitHub access.";
            ShowLoadError(Unreadable.GetValueOrDefault(pr.Id, ex.Message));
        }

        try
        {
            var checks = await checksTask;
            if (generation != _loadGeneration) return;
            ChecksSummary.Text = PullRequestPresentation.ChecksSummary(checks);
            ChecksList.ItemsSource = PullRequestPresentation.OrderChecks(checks).Select(c => new CheckRow(c)).ToList();
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration) return;
            Log.Warning(ex, "[pr-viewer] Checks unavailable for {Reference}", pr.Reference);
            ChecksSummary.Text = "Checks unavailable";
        }
    }

    private void ShowLoadError(string message)
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        ContentScroller.Visibility = Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Visible;
        ErrorText.Text = $"Could not load this pull request.\n\n{message}";
    }

    private static async Task<PullRequestDetail> FetchDetailAsync(UnifiedPullRequest pr)
    {
        var services = App.Services;
        if (services.DemoMode) return DevelopmentDemoData.Detail(pr);
        if (pr.Source == PullRequestSource.AzureDevOps)
        {
            if (services.Config.AzureDevOps is not { } config) throw new InvalidOperationException("Azure DevOps is not configured");
            using var service = new AzureDevOpsService(config);
            return await service.GetPullRequestDetailAsync(pr.Repository, pr.Number, pr.Container);
        }
        using var github = new GitHubPullRequestService(services.Config.GitHubProviderOrDefault());
        return await github.GetPullRequestDetailAsync(pr.Container, pr.Repository, pr.Number);
    }

    private static async Task<List<PullRequestCheck>> FetchChecksAsync(UnifiedPullRequest pr)
    {
        var services = App.Services;
        if (services.DemoMode) return DevelopmentDemoData.Checks();
        if (pr.Source == PullRequestSource.AzureDevOps)
        {
            if (services.Config.AzureDevOps is not { } config) return new();
            using var service = new AzureDevOpsService(config);
            return await service.GetPullRequestChecksAsync(pr.Repository, pr.Number, pr.Container);
        }
        using var github = new GitHubPullRequestService(services.Config.GitHubProviderOrDefault());
        return await github.GetChecksAsync(pr.Container, pr.Repository, pr.Number);
    }

    private void Render(PullRequestDetail detail)
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        ContentScroller.Visibility = Visibility.Visible;
        ContentScroller.ChangeView(null, 0, null, disableAnimation: true);

        var body = PullRequestPresentation.StripHtml(detail.Body);
        DescriptionSection.Visibility = DevelopmentUi.Show(!string.IsNullOrWhiteSpace(body));
        Description.Show(body);

        // System entries are kept but drawn in the secondary colour: hiding
        // them entirely loses the approval trail.
        var comments = detail.Comments.Select(c => new CommentRow(c)).ToList();
        ConversationSection.Visibility = DevelopmentUi.Show(comments.Count > 0);
        // The header count and the count beside the state both leave system entries out.
        var conversationCount = detail.Conversation.Count();
        ConversationHeader.Text = $"Conversation ({conversationCount})";
        CommentsList.ItemsSource = comments;
        CommentCountText.Text = PullRequestDetail.CommentCountLabel(conversationCount);

        CommitsSection.Visibility = DevelopmentUi.Show(detail.Commits.Count > 0);
        CommitsHeader.Text = $"Commits ({detail.Commits.Count})";
        CommitsList.ItemsSource = detail.Commits;

        Changes.Visibility = DevelopmentUi.Show(detail.Files.Count > 0);
        Changes.Header = PullRequestPresentation.ChangesHeader(detail);
        Changes.ShowFiles(detail.Files, detail.Truncated);

        ApplyLayout();
    }

    // MARK: - Layout

    private void ContentScroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (PullRequestDetailLayout.IsWide(e.PreviousSize.Width) != PullRequestDetailLayout.IsWide(e.NewSize.Width)) ApplyLayout();
    }

    /// <summary>Place each section for the pane's width and which sections have content.</summary>
    private void ApplyLayout()
    {
        var wide = PullRequestDetailLayout.IsWide(ContentScroller.ActualWidth);
        var layout = PullRequestDetailLayout.For(
            wide,
            hasOverview: DescriptionSection.Visibility == Visibility.Visible,
            hasHandbook: HandbookHost.Visibility == Visibility.Visible,
            hasComments: ConversationSection.Visibility == Visibility.Visible);

        ColumnGap.Width = new GridLength(wide ? 16 : 0);
        Place(DescriptionSection, layout.Overview);
        Place(HandbookHost, layout.Handbook);
        Place(ConversationSection, layout.Comments);
        Place(SidePanel, layout.Side);
        Place(Changes, layout.Changes);
    }

    private static void Place(FrameworkElement section, GridPlacement placement)
    {
        Grid.SetRow(section, placement.Row);
        Grid.SetColumn(section, placement.Column);
        Grid.SetColumnSpan(section, placement.ColumnSpan);
    }

    // MARK: - Browse

    private void OpenInBrowser_Click(object sender, RoutedEventArgs e) => DevelopmentUi.OpenUrl(_pullRequest?.WebUrl);

    /// <summary>Title and link together: rich text pastes as a titled link, plain text as both.</summary>
    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (_pullRequest is not { WebUrl.Length: > 0 } pr) return;
        try
        {
            var payload = RecordLinkPayload.Create(pr.Title, pr.WebUrl);
            var package = new DataPackage();
            package.SetText(payload.Text);
            package.SetHtmlFormat(payload.Html); // already CF_HTML
            Clipboard.SetContent(package);
            ShowStatus("Title and link copied.", false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[pr-viewer] Clipboard unavailable");
        }
    }

    private void Check_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is CheckRow check) DevelopmentUi.OpenUrl(check.Check.DetailsUrl);
    }

    // MARK: - Review actions

    private async void Comment_Click(object sender, RoutedEventArgs e)
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

    private async void Approve_Click(object sender, RoutedEventArgs e)
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

    private async void RequestChanges_Click(object sender, RoutedEventArgs e)
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

    private async void Merge_Click(object sender, RoutedEventArgs e)
    {
        if (_pullRequest is not { } pr) return;
        var method = MergeMethodBox.SelectedItem is ComboBoxItem { Tag: string tag }
                     && Enum.TryParse<PullRequestMergeMethod>(tag, out var parsed)
            ? parsed
            : PullRequestMergeMethod.Squash;
        var plan = new PullRequestActionPlan { PullRequest = pr };

        if (!await ConfirmAsync($"{plan.MergeLabel} pull request?",
                $"{pr.Title}\n\n{pr.Container}/{pr.Repository} {pr.Reference}\n\n{plan.MergeExplanation(method)}", plan.MergeLabel))
            return;

        await RunAsync(plan.MergeLabel, terminal: true,
            (gh, p) => gh.MergeAsync(p.Container, p.Repository, p.Number, method),
            (ado, p) => ado.CompletePullRequestAsync(p.Repository, p.Number, p.Container));
    }

    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_pullRequest is not { } pr) return;
        var plan = new PullRequestActionPlan { PullRequest = pr };
        if (!await ConfirmAsync($"{plan.CloseLabel} pull request?",
                $"{pr.Title}\n\nThis closes the pull request without merging and notifies its reviewers.", plan.CloseLabel))
            return;

        await RunAsync(plan.CloseLabel, terminal: true,
            (gh, p) => gh.CloseAsync(p.Container, p.Repository, p.Number),
            (ado, p) => ado.AbandonPullRequestAsync(p.Repository, p.Number, p.Container));
    }

    private async void Draft_Click(object sender, RoutedEventArgs e)
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
    /// ones hand back to the host, whose PR is now stale. Demo mode changes nothing.
    /// </summary>
    private async Task RunAsync(
        string action,
        bool terminal,
        Func<GitHubPullRequestService, UnifiedPullRequest, Task<PullRequestActionResult>> github,
        Func<AzureDevOpsService, UnifiedPullRequest, Task<PullRequestActionResult>> devops)
    {
        if (_pullRequest is not { } pr) return;
        var services = App.Services;
        if (services.DemoMode)
        {
            ShowStatus($"{action} is not sent in demo mode.", false);
            return;
        }

        ActionPanel.IsHitTestVisible = false;
        ActionButtons.Opacity = 0.6;
        ShowStatus($"{action}…", false);
        try
        {
            PullRequestActionResult result;
            if (pr.Source == PullRequestSource.GitHub)
            {
                using var service = new GitHubPullRequestService(services.Config.GitHubProviderOrDefault());
                result = await github(service, pr);
            }
            else
            {
                if (services.Config.AzureDevOps is not { } config)
                {
                    ShowStatus("Azure DevOps is not configured.", true);
                    return;
                }
                using var service = new AzureDevOpsService(config);
                result = await devops(service, pr);
            }

            if (!result.Success)
            {
                ShowStatus($"{action} failed: {result.Error}", true);
                return;
            }

            ComposerBox.Text = "";
            ShowStatus($"{action} done.", false);
            if (terminal) StateChanged?.Invoke(this, EventArgs.Empty);
            else await LoadAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[pr-viewer] {Action} failed for {Reference}", action, pr.Reference);
            ShowStatus($"{action} failed: {ex.Message}", true);
        }
        finally
        {
            ActionPanel.IsHitTestVisible = true;
            ActionButtons.Opacity = 1;
        }
    }

    private void ShowStatus(string message, bool isError)
    {
        ActionStatus.Severity = isError ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        ActionStatus.Message = message;
        ActionStatus.IsOpen = true;
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirm)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirm,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}

/// <summary>One entry in the conversation.</summary>
public sealed class CommentRow(PullRequestComment comment)
{
    public string AuthorName => comment.AuthorName;
    public string Body => PullRequestPresentation.StripHtml(comment.Body);
    public string DateLabel => PullRequestPresentation.CommentDate(comment);

    /// <summary>Vote and status noise in the secondary colour: context, not conversation.</summary>
    public Brush BodyBrush => DevelopmentUi.Theme(comment.IsSystem ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush");
}

/// <summary>One chip in the checks strip; it opens the check's page when there is one.</summary>
public sealed class CheckRow(PullRequestCheck check)
{
    public PullRequestCheck Check => check;
    public string Name => PullRequestPresentation.CheckName(check);
    public string ToolTip => PullRequestPresentation.CheckToolTip(check);
    public string Glyph => PullRequestPresentation.CheckGlyph(check.State);
    public bool HasDetails => !string.IsNullOrWhiteSpace(check.DetailsUrl);

    public Brush Brush => check.State switch
    {
        PullRequestCheckState.Success => DevelopmentUi.Theme("SystemFillColorSuccessBrush"),
        PullRequestCheckState.Failure => DevelopmentUi.Theme("SystemFillColorCautionBrush"),
        PullRequestCheckState.Pending => DevelopmentUi.Theme("AccentTextFillColorPrimaryBrush"),
        _ => DevelopmentUi.Theme("TextFillColorSecondaryBrush"),
    };
}
