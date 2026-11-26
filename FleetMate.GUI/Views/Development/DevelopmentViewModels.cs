using System.Windows;
using System.Windows.Media;
using FleetMate.Core.Models.Projects;
using FleetMate.GUI.Views.Shared;

namespace FleetMate.GUI.Views.Development;

/// <summary>One row in the Development pull request list.</summary>
public sealed class DevelopmentPullRequestRowViewModel
{
    public required UnifiedPullRequest PullRequest { get; init; }

    private PullRequestRowViewModel Row => new() { PullRequest = PullRequest };

    public string Title => PullRequest.Title;
    public string Byline => Row.Byline;
    public string StateLabel => Row.StateLabel;
    public Brush StateBrush => Row.StateBrush;
    public string SourceLabel => Row.SourceLabel;
    public string RepositoryKey => DevelopmentFilter.RepositoryKey(PullRequest);

    /// <summary>Names the repository on the row in the ungrouped list, where no section header does.</summary>
    public bool ShowRepository { get; init; }

    /// <summary>"owner/repo · ada · #42 · Updated 3h ago" ungrouped; the plain byline under a section header.</summary>
    public string Subtitle => DevelopmentText.PullRequestSubtitle(PullRequest, ShowRepository);

    /// <summary>
    /// Why this PR is on the operator's plate, strongest reason first — "Review"
    /// beats "Mine" beats "Involved". Empty for organization-only rows.
    /// </summary>
    public string RelationLabel => DevelopmentText.RelationLabel(PullRequest);

    public Visibility RelationVisibility => RelationLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string CopyIdLabel => $"Copy ID ({PullRequest.Number})";
}

/// <summary>One comment or review in the activity sidebar.</summary>
public sealed class DevelopmentActivityRowViewModel
{
    public required UnifiedPullRequest PullRequest { get; init; }
    public required PullRequestComment Comment { get; init; }

    public string AuthorName => Comment.AuthorName;
    public string Age => DevelopmentText.Age(Comment.Date);
    public string PullRequestLabel => $"{PullRequest.Repository} {PullRequest.Reference} · {PullRequest.Title}";
    public string PullRequestTitle => PullRequest.Title;

    /// <summary>Review verbs ("approved") read as events, so they render italic.</summary>
    public FontStyle SnippetStyle => Comment.IsSystem ? FontStyles.Italic : FontStyles.Normal;

    /// <summary>First 240 characters, HTML stripped (DevOps), whitespace collapsed.</summary>
    public string Snippet
    {
        get
        {
            var text = PullRequestCommentViewModel.Strip(Comment.Body);
            text = System.Text.RegularExpressions.Regex.Replace(text, "\\s+", " ").Trim();
            return text.Length > 240 ? text[..240] + "…" : text;
        }
    }
}

/// <summary>One row in the Development inbox.</summary>
public sealed class DevelopmentNotificationRowViewModel
{
    public required GitHubNotification Notification { get; init; }

    /// <summary>
    /// The author of the pull request the thread is about, from the pull
    /// requests already loaded. GitHub's notification does not say who acted,
    /// so this is labelled as the PR's author, never as the sender.
    /// </summary>
    public string? PullRequestAuthor { get; init; }

    public string AuthorLabel => DevelopmentText.AuthorLabel(PullRequestAuthor);

    /// <summary>A row for <paramref name="notification"/>, naming its PR's author when that PR is loaded.</summary>
    public static DevelopmentNotificationRowViewModel Create(
        GitHubNotification notification, IEnumerable<UnifiedPullRequest> pullRequests) => new()
    {
        Notification = notification,
        PullRequestAuthor = DevelopmentText.NotificationPullRequestAuthor(notification, pullRequests),
    };

    public string Title => Notification.SubjectTitle;
    public bool Unread => Notification.Unread;
    public FontWeight TitleWeight => Unread ? FontWeights.SemiBold : FontWeights.Normal;
    public Visibility UnreadVisibility => Unread ? Visibility.Visible : Visibility.Hidden;
    public Visibility MarkReadVisibility => Unread ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>"Review requested · owner/repo · 3h ago".</summary>
    public string Byline => DevelopmentText.NotificationByline(Notification, PullRequestAuthor);

    public string TypeGlyph => DevelopmentText.NotificationGlyph(Notification.SubjectType);

    public string TypeLabel => DevelopmentText.NotificationTypeLabel(Notification.SubjectType);

    public static string ReasonLabel(string reason) => DevelopmentText.ReasonLabel(reason);

    public static string Age(DateTime? when) => DevelopmentText.Age(when);
}
