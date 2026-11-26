using FleetMate.Core.Development;
using FleetMate.Core.Models.Projects;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace FleetMate.WinUI.Pages.Development.Pulls;

/// <summary>One row in the Pulls list. The words come from <see cref="DevelopmentText"/>, shared with FleetMate.GUI.</summary>
public sealed class PullRequestRow
{
    public PullRequestRow(UnifiedPullRequest pullRequest, bool showRepository)
    {
        PullRequest = pullRequest;
        Subtitle = DevelopmentText.PullRequestSubtitle(pullRequest, showRepository);
        RelationLabel = DevelopmentText.RelationLabel(pullRequest);
    }

    public UnifiedPullRequest PullRequest { get; }

    public string Title => PullRequest.Title;
    public string SourceLabel => PullRequest.Source.ShortName();
    public string StateLabel => DevelopmentText.StateLabel(PullRequest);
    public Brush StateBrush => DevelopmentUi.StateBrush(PullRequest);
    public string Subtitle { get; }
    public string RelationLabel { get; }
    public Visibility RelationVisibility => DevelopmentUi.Show(RelationLabel.Length > 0);
    public string RepositoryKey => DevelopmentFilter.RepositoryKey(PullRequest);
    public string CopyIdLabel => $"Copy ID ({PullRequest.Number})";

    /// <summary>What a screen reader hears for the row.</summary>
    public string AccessibleName =>
        string.Join(", ", new[] { Title, StateLabel, RelationLabel, SourceLabel, Subtitle }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>
/// One repository section in Pulls' Group by Repository. A collapsed section
/// keeps its header and count but holds no rows, so the list stays one
/// virtualized ListView. Click the header to collapse or expand it, Ctrl-click
/// for every section (Option-click on the Mac).
/// </summary>
public sealed class PullRequestGroup : List<PullRequestRow>
{
    public PullRequestGroup(string key, IReadOnlyList<PullRequestRow> rows, bool collapsed)
        : base(collapsed ? Array.Empty<PullRequestRow>() : rows)
    {
        Key = key;
        Total = rows.Count;
        IsCollapsed = collapsed;
    }

    public string Key { get; }
    public int Total { get; }
    public bool IsCollapsed { get; }

    public string Header => $"{Key}  {Total}";
    public string Chevron => IsCollapsed ? "" : "";
    public string Tip => PullGroupCollapse.Tip(Key, IsCollapsed);
    public string Status => PullGroupCollapse.Status(IsCollapsed);
    public string ToggleLabel => PullGroupCollapse.ToggleLabel(IsCollapsed);
}

/// <summary>One thread in the Inbox popover.</summary>
public sealed class NotificationRow
{
    public NotificationRow(GitHubNotification notification, IEnumerable<UnifiedPullRequest> pullRequests)
    {
        Notification = notification;
        Byline = DevelopmentText.NotificationByline(notification,
            DevelopmentText.NotificationPullRequestAuthor(notification, pullRequests));
    }

    public GitHubNotification Notification { get; }

    public string Title => Notification.SubjectTitle;
    public string Byline { get; }
    public bool Unread => Notification.Unread;
    public FontWeight TitleWeight => Unread ? FontWeights.SemiBold : FontWeights.Normal;

    /// <summary>The unread dot keeps its space when read, so titles line up.</summary>
    public double UnreadOpacity => Unread ? 1 : 0;

    public Visibility MarkReadVisibility => DevelopmentUi.Show(Unread);
    public string TypeGlyph => DevelopmentText.NotificationGlyph(Notification.SubjectType);
    public string TypeLabel => DevelopmentText.NotificationTypeLabel(Notification.SubjectType);
    public string AccessibleName => $"{(Unread ? "Unread, " : "")}{TypeLabel}: {Title}, {Byline}";
}
