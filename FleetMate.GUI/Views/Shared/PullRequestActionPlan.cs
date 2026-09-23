using System.Windows;
using System.Windows.Media;
using FleetMate.Core.Models.Projects;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// Which actions the PR detail view offers, and what they are called, for one
/// pull request. The two providers name the same operation differently
/// (Merge/Complete, Close/Abandon) and GitHub alone picks a merge method, so
/// the rules live here where they can be tested rather than in XAML triggers.
/// </summary>
public sealed class PullRequestActionPlan
{
    public required UnifiedPullRequest PullRequest { get; init; }

    private bool IsGitHub => PullRequest.Source == PullRequestSource.GitHub;

    /// <summary>Open or draft — merged and closed PRs take no actions but browse/copy.</summary>
    public bool IsLive => PullRequest.State is PullRequestState.Open or PullRequestState.Draft;

    public bool IsDraft => PullRequest.State == PullRequestState.Draft;

    public bool CanReview => IsLive;
    public bool CanComment => IsLive;

    /// <summary>Neither provider merges a draft; the button would only ever error.</summary>
    public bool CanMerge => IsLive && !IsDraft;

    public bool CanClose => IsLive;

    /// <summary>GitHub's draft toggle is a GraphQL mutation keyed on the node id.</summary>
    public bool CanToggleDraft => IsLive && (!IsGitHub || !string.IsNullOrEmpty(PullRequest.NodeId));

    public string MergeLabel => IsGitHub ? "Merge" : "Complete";
    public string CloseLabel => IsGitHub ? "Close" : "Abandon";
    public string DraftLabel => IsDraft ? "Mark ready" : "Convert to draft";
    public bool ShowMergeMethod => IsGitHub && CanMerge;
}

/// <summary>One chip in the checks strip.</summary>
public sealed class PullRequestCheckViewModel
{
    public required PullRequestCheck Check { get; init; }

    public string Name => Check.IsRequired ? $"{Check.Name} *" : Check.Name;
    public string? DetailsUrl => Check.DetailsUrl;
    public string ToolTip => $"{Check.Name} — {Check.State}{(Check.IsRequired ? " (required)" : "")}";

    public string Glyph => Check.State switch
    {
        PullRequestCheckState.Success => "",
        PullRequestCheckState.Failure => "",
        PullRequestCheckState.Pending => "",
        _ => "",
    };

    public Brush Brush => Check.State switch
    {
        PullRequestCheckState.Success => new SolidColorBrush(Color.FromRgb(0x2D, 0xA4, 0x4E)),
        PullRequestCheckState.Failure => new SolidColorBrush(Color.FromRgb(0xE0, 0x7A, 0x1F)),
        PullRequestCheckState.Pending => new SolidColorBrush(Color.FromRgb(0x3A, 0x6E, 0xA5)),
        _ => Brushes.Gray,
    };

    /// <summary>"3 of 4 passed · 1 failing" — failing and pending named, since those are what need attention.</summary>
    public static string Summary(IReadOnlyCollection<PullRequestCheck> checks)
    {
        if (checks.Count == 0) return "No checks";

        var passed = checks.Count(c => c.State == PullRequestCheckState.Success);
        var failing = checks.Count(c => c.State == PullRequestCheckState.Failure);
        var pending = checks.Count(c => c.State == PullRequestCheckState.Pending);

        var parts = new List<string> { $"{passed} of {checks.Count} passed" };
        if (failing > 0) parts.Add($"{failing} failing");
        if (pending > 0) parts.Add($"{pending} pending");
        return string.Join(" · ", parts);
    }

    public static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
