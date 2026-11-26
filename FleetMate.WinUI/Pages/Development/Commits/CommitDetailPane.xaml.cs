using FleetMate.Core.Development;
using FleetMate.Core.Links;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace FleetMate.WinUI.Pages.Development.Commits;

/// <summary>
/// One commit: its message, then per-file diffs (GitHub) or the change list
/// (Azure DevOps). An Azure DevOps commit opened before sign-in finishes waits
/// for it, the same for a link and for a row picked early.
/// </summary>
public sealed partial class CommitDetailPane : UserControl
{
    private PullRequestCommit? _commit;
    private RepositoryCommits? _repository;
    private int _generation;
    private bool _awaitingSignIn;

    public CommitDetailPane()
    {
        InitializeComponent();
        Loaded += (_, _) => App.Services.DevOpsSignedIn += OnDevOpsSignedIn;
        Unloaded += (_, _) => App.Services.DevOpsSignedIn -= OnDevOpsSignedIn;
    }

    public PullRequestCommit? Commit => _commit;

    private async void OnDevOpsSignedIn()
    {
        if (_awaitingSignIn && _repository is { } repository && _commit is { } commit) await ShowAsync(repository, commit);
    }

    public async Task ShowAsync(RepositoryCommits repository, PullRequestCommit commit)
    {
        _commit = commit;
        _repository = repository;
        _awaitingSignIn = false;
        var generation = ++_generation;

        SubjectText.Text = commit.Subject.Length > 0 ? commit.Subject : $"Commit {commit.ShortSha}";
        BylineText.Text = CommitDetailText.Byline(commit, repository);
        Show(LoadingPanel);

        var services = App.Services;
        var startedSignedIn = services.IsDevOpsSignedIn;
        try
        {
            var detail = services.DemoMode
                ? DevelopmentDemoData.CommitDetail(repository, commit)
                : await CommitsAndPipelinesService.CommitDetailAsync(services.Config, services.DevOpsService, repository, commit);
            if (generation != _generation) return;
            Render(detail);
        }
        catch (Exception ex)
        {
            if (generation != _generation) return;
            Log.Error(ex, "[commits] Failed to load {Sha}", commit.ShortSha);

            // Opened from a cold launch, an Azure DevOps commit races sign-in
            // and loses. Hold it rather than report the failure. GitHub never waits.
            var retry = repository.Source == PullRequestSource.AzureDevOps ? services.DevOpsRetry(startedSignedIn) : SignInRetry.ShowError;
            switch (retry)
            {
                case SignInRetry.RetryNow:
                    await ShowAsync(repository, commit);
                    break;
                case SignInRetry.WaitForSignIn:
                    ShowWaiting(ex.Message);
                    break;
                default:
                    ErrorText.Text = $"Could not load this commit.\n\n{ex.Message}";
                    Show(ErrorPanel);
                    break;
            }
        }
    }

    private void Show(FrameworkElement panel)
    {
        foreach (var each in new FrameworkElement[] { LoadingPanel, ErrorPanel, WaitHost, ContentScroller })
            each.Visibility = DevelopmentUi.Show(ReferenceEquals(each, panel));
    }

    private void ShowWaiting(string? error)
    {
        _awaitingSignIn = true;
        WaitHost.Content = new DevOpsSignInWaitPanel($"commit {_commit?.ShortSha}", error);
        Show(WaitHost);
    }

    private void Render(CommitDetail detail)
    {
        Show(ContentScroller);
        ContentScroller.ChangeView(null, 0, null, disableAnimation: true);

        var body = CommitDetailText.Body(detail.Message);
        BodySection.Visibility = DevelopmentUi.Show(body.Length > 0);
        BodyText.Text = body;

        var hasDiffs = detail.Files.Count > 0;
        Diff.Visibility = DevelopmentUi.Show(hasDiffs);
        Diff.Header = hasDiffs ? CommitDetailText.ChangesHeader(detail) : "";
        Diff.ShowFiles(hasDiffs ? detail.Files : Array.Empty<DiffFile>(), detail.Truncated);

        ChangesSection.Visibility = DevelopmentUi.Show(!hasDiffs && detail.Changes.Count > 0);
        ChangesHeader.Text = CommitDetailText.ChangesHeader(detail);
        ChangesList.ItemsSource = hasDiffs ? null : detail.Changes;
    }

    /// <summary>Retry signs in first when sign-in is the problem; the sign-in is silent only.</summary>
    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_repository is not { } repository || _commit is not { } commit) return;
        if (repository.Source != PullRequestSource.AzureDevOps)
        {
            await ShowAsync(repository, commit);
            return;
        }
        if (!await App.Services.RetryDevOpsRequestAsync(() => ShowAsync(repository, commit))) ShowWaiting(null);
    }

    private void CopySha_Click(object sender, RoutedEventArgs e) => DevelopmentUi.Copy(_commit?.Id);

    private void CopyLink_Click(object sender, RoutedEventArgs e) => DevelopmentUi.Copy(_commit?.Url);

    private void OpenInBrowser_Click(object sender, RoutedEventArgs e) => DevelopmentUi.OpenUrl(_commit?.Url);

    private void CopyForAgent_Click(object sender, RoutedEventArgs e)
    {
        if (_commit is { } commit && _repository is { } repository) DevelopmentUi.Copy(AgentContexts.Commit(commit, repository).Markdown);
    }
}
