using System.Windows;
using System.Windows.Media;
using FleetMate.Core.Models.Projects;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// One row in the pull request queue, with everything the template needs already
/// formatted.
///
/// The formatting lives here rather than in converters because most of it is
/// conditional on more than one field — "Updated 3d ago" vs "Created 3d ago"
/// depends on both timestamps, and the action buttons depend on source and state
/// together. A converter per rule would scatter that logic across the XAML.
/// </summary>
public sealed class PullRequestRowViewModel
{
    public required UnifiedPullRequest PullRequest { get; init; }

    public string Title => PullRequest.Title;
    public string Reference => PullRequest.Reference;
    public string SourceLabel => PullRequest.Source.ShortName();
    public string RepositoryLabel => $"{PullRequest.Container}/{PullRequest.Repository}";
    public string BranchLabel => $"{PullRequest.SourceBranch} → {PullRequest.TargetBranch}";
    public string WebUrl => PullRequest.WebUrl;

    /// <summary>
    /// "Ada Lovelace · !10716 · Updated 3d ago". One line so a dense queue stays
    /// scannable; the Azure DevOps queue reads the same way.
    /// </summary>
    public string Byline
    {
        get
        {
            var when = PullRequest.WasUpdatedAfterCreation
                ? $"Updated {RelativeAge(PullRequest.UpdatedAt)}"
                : $"Created {RelativeAge(PullRequest.CreatedAt)}";

            return $"{PullRequest.AuthorName} · {PullRequest.Reference} · {when}";
        }
    }

    public string CommentLabel => PullRequest.CommentCount > 0 ? PullRequest.CommentCount.ToString() : "";
    public Visibility CommentVisibility =>
        PullRequest.CommentCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string StateLabel => PullRequest.State switch
    {
        PullRequestState.Draft => "Draft",
        PullRequestState.Merged => "Merged",
        PullRequestState.Closed => "Closed",
        _ => PullRequest.HasConflicts ? "Conflicts" : "Open",
    };

    public Brush StateBrush => PullRequest.State switch
    {
        PullRequestState.Draft => Brushes.Gray,
        PullRequestState.Merged => new SolidColorBrush(Color.FromRgb(0x6E, 0x54, 0x94)),
        PullRequestState.Closed => new SolidColorBrush(Color.FromRgb(0xD1, 0x3A, 0x3A)),
        _ => PullRequest.HasConflicts
            ? new SolidColorBrush(Color.FromRgb(0xD1, 0x3A, 0x3A))
            : new SolidColorBrush(Color.FromRgb(0x2D, 0xA4, 0x4E)),
    };

    /// <summary>Reviewer initials, e.g. "AL · GH". Empty when nobody is on it.</summary>
    public string ReviewerLabel =>
        string.Join(" · ", PullRequest.Reviewers.Select(r => r.Initials));

    public Visibility ReviewerVisibility =>
        PullRequest.Reviewers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Complete and Abandon are Azure DevOps only, and only while the PR is still
    /// live — offering them on a merged PR would be a button that always errors.
    /// </summary>
    public Visibility ActionVisibility =>
        PullRequest.Source == PullRequestSource.AzureDevOps
        && PullRequest.State is PullRequestState.Open or PullRequestState.Draft
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>Relative age in the queue's own units — days, not "last month".</summary>
    private static string RelativeAge(DateTime? when)
    {
        if (when is not { } value) return "recently";

        var elapsed = DateTime.UtcNow - value;
        if (elapsed.TotalMinutes < 1) return "just now";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours}h ago";
        return $"{(int)elapsed.TotalDays}d ago";
    }
}
