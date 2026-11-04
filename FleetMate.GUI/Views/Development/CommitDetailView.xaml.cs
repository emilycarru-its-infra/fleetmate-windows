using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Shared;
using FleetMate.GUI.Views.Shared;
using Serilog;

namespace FleetMate.GUI.Views.Development;

/// <summary>
/// One commit in the Development centre pane: message, then per-file diffs
/// (GitHub) or the change list (Azure DevOps, which has no patch).
/// </summary>
public partial class CommitDetailView : UserControl
{
    private PullRequestCommit? _commit;
    private RepositoryCommits? _repository;
    private int _generation;

    /// <summary>
    /// The load failed before Azure DevOps sign-in finished; it runs again once
    /// sign-in completes.
    /// </summary>
    private bool _awaitingSignIn;

    public CommitDetailView()
    {
        InitializeComponent();
        if (Application.Current is App app)
            app.DevOpsSignedIn += async () =>
            {
                if (_awaitingSignIn && _repository is { } r && _commit is { } c) await ShowAsync(r, c);
            };
    }

    public async Task ShowAsync(RepositoryCommits repository, PullRequestCommit commit)
    {
        _commit = commit;
        _repository = repository;
        _awaitingSignIn = false;
        var generation = ++_generation;
        AgentButton.Context = AgentContexts.Commit(commit, repository);

        SubjectText.Text = commit.Subject;
        BylineText.Text = $"{commit.AuthorName ?? "unknown"} · {commit.ShortSha} · " +
                          $"{commit.Date?.ToLocalTime():d MMM yyyy, HH:mm} · {repository.DisplayName}";

        LoadingPanel.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
        WaitHost.Visibility = Visibility.Collapsed;
        ContentScroller.Visibility = Visibility.Collapsed;

        var app = Application.Current as App;
        var isDevOps = repository.Source == PullRequestSource.AzureDevOps;
        var startedSignedIn = app?.IsDevOpsSsoAuthenticated ?? false;
        try
        {
            var detail = await FetchAsync(repository, commit);
            if (generation != _generation) return;
            Render(detail);
        }
        catch (Exception ex)
        {
            if (generation != _generation) return;
            Log.Error(ex, "[commits] Failed to load {Sha}", commit.ShortSha);
            LoadingPanel.Visibility = Visibility.Collapsed;

            // Opened from a cold launch, an Azure DevOps commit races sign-in
            // and loses. Hold it rather than report the failure. GitHub never waits.
            var retry = isDevOps && app != null ? app.DevOpsRetry(startedSignedIn) : FleetMate.Core.Links.SignInRetry.ShowError;
            switch (retry)
            {
                case FleetMate.Core.Links.SignInRetry.RetryNow:
                    await ShowAsync(repository, commit);
                    break;
                case FleetMate.Core.Links.SignInRetry.WaitForSignIn:
                    ShowWaiting(ex.Message);
                    break;
                default:
                    ErrorPanel.Visibility = Visibility.Visible;
                    ErrorText.Text = $"Could not load this commit.\n\n{ex.Message}";
                    break;
            }
        }
    }

    private void ShowWaiting(string? error)
    {
        _awaitingSignIn = true;
        ErrorPanel.Visibility = Visibility.Collapsed;
        WaitHost.Content = new DevOpsSignInWaitPanel($"commit {_commit?.ShortSha}", error);
        WaitHost.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Retry signs in first when sign-in is the problem, since a plain reload
    /// while signed out fails the same way. The sign-in is silent only.
    /// </summary>
    private async void OnRetryClicked(object sender, RoutedEventArgs e)
    {
        if (_repository is not { } repository || _commit is not { } commit) return;
        if (repository.Source != PullRequestSource.AzureDevOps || Application.Current is not App app)
        {
            await ShowAsync(repository, commit);
            return;
        }
        if (!await app.RetryDevOpsRequestAsync(() => ShowAsync(repository, commit)))
            ShowWaiting(null);
    }

    private static async Task<CommitDetail> FetchAsync(RepositoryCommits repository, PullRequestCommit commit)
    {
        var app = Application.Current as App ?? throw new InvalidOperationException("Application is not available");

        if (repository.Source == PullRequestSource.AzureDevOps)
        {
            // The app's service carries the sign-in token; a fresh one could only
            // use the broker, which misses a sign-in made through az CLI or the
            // hidden browser.
            if (app.DevOpsService is { } shared)
                return await shared.GetCommitDetailAsync(repository.RepositoryId ?? repository.Repository, commit.Id, repository.Container);
            if (app.Config.AzureDevOps is not { } ado) throw new InvalidOperationException("Azure DevOps is not configured");
            using var devops = new AzureDevOpsService(ado);
            return await devops.GetCommitDetailAsync(repository.RepositoryId ?? repository.Repository, commit.Id, repository.Container);
        }

        using var github = new GitHubPullRequestService(app.Config.GitHubProviderOrDefault());
        return await github.GetCommitDetailAsync(repository.Container, repository.Repository, commit.Id);
    }

    /// <summary>The diff on screen, kept so a layout change can redraw it.</summary>
    private IReadOnlyList<DiffFile> _files = Array.Empty<DiffFile>();

    private void ShowFiles()
    {
        var layout = UserPreferences.DiffLayout;
        DiffLayoutPicker.Show(layout);
        FilesList.ItemsSource = _files.Count > 0
            ? _files.Select(f => new DiffFileViewModel { File = f, Layout = layout }).ToList()
            : null;
    }

    private void OnDiffLayoutChanged(DiffLayout layout) => ShowFiles();

    private void Render(CommitDetail detail)
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        ContentScroller.Visibility = Visibility.Visible;
        ContentScroller.ScrollToTop();

        // The subject is already the title; show the message body only when there is one.
        var newline = detail.Message.IndexOfAny(new[] { '\n', '\r' });
        var body = newline < 0 ? "" : detail.Message[newline..].Trim();
        BodySection.Visibility = body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        BodyText.Text = body;

        var hasDiffs = detail.Files.Count > 0;
        _files = hasDiffs ? detail.Files : Array.Empty<DiffFile>();
        DiffLayoutPicker.Visibility = hasDiffs ? Visibility.Visible : Visibility.Collapsed;
        ShowFiles();
        ChangesCard.Visibility = hasDiffs || detail.Changes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ChangesList.ItemsSource = hasDiffs ? null : detail.Changes;

        var count = hasDiffs ? detail.Files.Count : detail.Changes.Count;
        ChangesHeader.Text = hasDiffs
            ? $"Changes ({count} files, +{detail.Additions} −{detail.Deletions})"
            : $"Changes ({count} files)";

        TruncationNotice.Visibility = detail.Truncated ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpenInBrowserClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_commit?.Url)) return;
        FleetMate.GUI.Links.ExternalBrowser.Open(_commit.Url);
    }

    private void OnCopyShaClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_commit?.Id)) return;
        try { Clipboard.SetText(_commit.Id); }
        catch (Exception ex) { Log.Warning(ex, "[commits] Clipboard unavailable"); }
    }

    private void OnCopyLinkClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_commit?.Url)) return;
        try { Clipboard.SetText(_commit.Url); }
        catch (Exception ex) { Log.Warning(ex, "[commits] Clipboard unavailable"); }
    }
}
