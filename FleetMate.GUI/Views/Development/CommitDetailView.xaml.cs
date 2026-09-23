using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
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
    private int _generation;

    public CommitDetailView()
    {
        InitializeComponent();
    }

    public async Task ShowAsync(RepositoryCommits repository, PullRequestCommit commit)
    {
        _commit = commit;
        var generation = ++_generation;

        SubjectText.Text = commit.Subject;
        BylineText.Text = $"{commit.AuthorName ?? "unknown"} · {commit.ShortSha} · " +
                          $"{commit.Date?.ToLocalTime():d MMM yyyy, HH:mm} · {repository.DisplayName}";

        LoadingPanel.Visibility = Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;
        ContentScroller.Visibility = Visibility.Collapsed;

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
            ErrorText.Visibility = Visibility.Visible;
            ErrorText.Text = $"Could not load this commit.\n\n{ex.Message}";
        }
    }

    private static async Task<CommitDetail> FetchAsync(RepositoryCommits repository, PullRequestCommit commit)
    {
        var app = Application.Current as App ?? throw new InvalidOperationException("Application is not available");

        if (repository.Source == PullRequestSource.AzureDevOps)
        {
            if (app.Config.AzureDevOps is not { } ado) throw new InvalidOperationException("Azure DevOps is not configured");
            using var devops = new AzureDevOpsService(ado);
            return await devops.GetCommitDetailAsync(repository.RepositoryId ?? repository.Repository, commit.Id, repository.Container);
        }

        using var github = new GitHubPullRequestService(app.Config.GitHubProviderOrDefault());
        return await github.GetCommitDetailAsync(repository.Container, repository.Repository, commit.Id);
    }

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
        FilesList.ItemsSource = hasDiffs ? detail.Files.Select(f => new DiffFileViewModel { File = f }).ToList() : null;
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
        try { Process.Start(new ProcessStartInfo(_commit.Url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warning(ex, "[commits] Could not open {Url}", _commit.Url); }
    }

    private void OnCopyLinkClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_commit?.Url)) return;
        try { Clipboard.SetText(_commit.Url); }
        catch (Exception ex) { Log.Warning(ex, "[commits] Clipboard unavailable"); }
    }
}
