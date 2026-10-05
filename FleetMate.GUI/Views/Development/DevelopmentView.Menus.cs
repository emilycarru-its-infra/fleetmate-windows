using System.Diagnostics;
using System.Windows;
using System.Windows.Controls.Primitives;
using Serilog;

namespace FleetMate.GUI.Views.Development;

/// <summary>Right-click menus on pull request, inbox, commit and pipeline rows, and the inbox filter.</summary>
public partial class DevelopmentView
{
    private bool _showReadNotifications;

    private static T? RowOf<T>(object sender) where T : class =>
        (sender as FrameworkElement)?.DataContext as T ?? (sender as FrameworkElement)?.Tag as T;

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warning(ex, "[development] Could not open {Url}", url); }
    }

    private static void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); }
        catch (Exception ex) { Log.Warning(ex, "[development] Clipboard unavailable"); }
    }

    // Pull requests
    private void OnPullRequestOpenInBrowser(object sender, RoutedEventArgs e) =>
        OpenUrl(RowOf<DevelopmentPullRequestRowViewModel>(sender)?.PullRequest.WebUrl);

    private void OnPullRequestCopyLink(object sender, RoutedEventArgs e) =>
        Copy(RowOf<DevelopmentPullRequestRowViewModel>(sender)?.PullRequest.WebUrl);

    private void OnPullRequestCopyId(object sender, RoutedEventArgs e) =>
        Copy(RowOf<DevelopmentPullRequestRowViewModel>(sender)?.PullRequest.Number.ToString());

    // Commits
    private void OnCommitOpenInBrowser(object sender, RoutedEventArgs e) =>
        OpenUrl(RowOf<CommitRowViewModel>(sender)?.Commit.Url);

    private void OnCommitCopySha(object sender, RoutedEventArgs e) =>
        Copy(RowOf<CommitRowViewModel>(sender)?.Commit.Id);

    // Pipelines
    private void OnRunOpenInBrowser(object sender, RoutedEventArgs e) =>
        OpenUrl(RowOf<PipelineRunRowViewModel>(sender)?.Run.WebUrl);

    private void OnRunCopyLink(object sender, RoutedEventArgs e) =>
        Copy(RowOf<PipelineRunRowViewModel>(sender)?.Run.WebUrl);

    // Inbox
    private void OnNotificationOpenInBrowser(object sender, RoutedEventArgs e) =>
        OpenUrl(RowOf<DevelopmentNotificationRowViewModel>(sender)?.Notification.WebUrl);

    private void OnNotificationCopyLink(object sender, RoutedEventArgs e) =>
        Copy(RowOf<DevelopmentNotificationRowViewModel>(sender)?.Notification.WebUrl);

    private async void OnDoneClicked(object sender, RoutedEventArgs e)
    {
        if (RowOf<DevelopmentNotificationRowViewModel>(sender) is not { } row || AppInstance is not { } app) return;
        var result = await app.Inbox.MarkDoneAsync(row.Notification);
        if (!result.Success) InboxStatus.Text = $"Done failed — {result.Error}";
    }

    private void OnInboxFilterClicked(object sender, RoutedEventArgs e)
    {
        _showReadNotifications = (sender as ToggleButton)?.Tag as string == "All";
        InboxUnreadChip.IsChecked = !_showReadNotifications;
        InboxAllChip.IsChecked = _showReadNotifications;
        RenderInbox();
    }
}
