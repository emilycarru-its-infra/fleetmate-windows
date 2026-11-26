using FleetMate.Core.Models.Projects;

namespace FleetMate.Core.Development;

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

    /// <summary>The merge confirmation's explanation.</summary>
    public string MergeExplanation(PullRequestMergeMethod method) => IsGitHub
        ? $"This {method.GitHubValue()}-merges into {PullRequest.TargetBranch}."
        : $"This completes the pull request into {PullRequest.TargetBranch} with its own merge settings.";
}

/// <summary>A section's cell in the pull request detail grid.</summary>
public readonly record struct GridPlacement(int Row, int Column, int ColumnSpan);

/// <summary>
/// Where each section of the pull request detail sits. The grid has three
/// columns: left, a gap, right; a full-width section spans all three.
/// Wide: Overview | Handbook, Comments | Checks and Commits, then Changes
/// across the full width. A missing side gives its row to the other.
/// Narrow: one column in the order Overview, Handbook, Comments, Checks and
/// Commits, Changes. Mirrors the Mac app's PullRequestDetailView.
/// </summary>
public sealed record PullRequestDetailLayout(
    GridPlacement Overview,
    GridPlacement Handbook,
    GridPlacement Comments,
    GridPlacement Side,
    GridPlacement Changes)
{
    /// <summary>Pane width from which two columns read well.</summary>
    public const double WideWidth = 900;

    public const int ColumnCount = 3;
    public const int RowCount = 5;

    public static bool IsWide(double width) => width >= WideWidth;

    public static PullRequestDetailLayout For(bool wide, bool hasOverview, bool hasHandbook, bool hasComments)
    {
        static GridPlacement Full(int row) => new(row, 0, ColumnCount);
        static GridPlacement Left(int row) => new(row, 0, 1);
        static GridPlacement Right(int row) => new(row, 2, 1);

        if (!wide)
            return new(Full(0), Full(1), Full(2), Full(3), Full(4));

        var splitTop = hasOverview && hasHandbook;
        var splitMiddle = hasComments;
        return new(
            Overview: splitTop ? Left(0) : Full(0),
            Handbook: splitTop ? Right(0) : Full(0),
            Comments: splitMiddle ? Left(1) : Full(1),
            Side: splitMiddle ? Right(1) : Full(1),
            Changes: Full(2));
    }
}

/// <summary>The PR detail's checks strip and conversation wording, shared by both apps.</summary>
public static class PullRequestPresentation
{
    public static string CheckName(PullRequestCheck check) => check.IsRequired ? $"{check.Name} *" : check.Name;

    public static string CheckToolTip(PullRequestCheck check) =>
        $"{check.Name} — {check.State}{(check.IsRequired ? " (required)" : "")}";

    public static string CheckGlyph(PullRequestCheckState state) => state switch
    {
        PullRequestCheckState.Success => "",
        PullRequestCheckState.Failure => "",
        PullRequestCheckState.Pending => "",
        _ => "",
    };

    /// <summary>Failing first, then pending, then the rest, each by name.</summary>
    public static List<PullRequestCheck> OrderChecks(IEnumerable<PullRequestCheck> checks) => checks
        .OrderBy(c => c.State == PullRequestCheckState.Failure ? 0 : c.State == PullRequestCheckState.Pending ? 1 : 2)
        .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>"3 of 4 passed · 1 failing" — failing and pending named, since those are what need attention.</summary>
    public static string ChecksSummary(IReadOnlyCollection<PullRequestCheck> checks)
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

    /// <summary>
    /// Azure DevOps sends comment bodies as HTML. Stripping the tags is crude,
    /// but a wall of raw markup is worse than plain text, and the alternative is
    /// hosting a browser control for a two-line comment.
    /// </summary>
    public static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        if (!html.Contains('<')) return html.Trim();

        var text = System.Text.RegularExpressions.Regex.Replace(html, "<br\\s*/?>", "\n");
        text = System.Text.RegularExpressions.Regex.Replace(text, "</p>", "\n");
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "");

        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }

    public static string CommentDate(PullRequestComment comment) => comment.Date?.ToLocalTime().ToString("d MMM, HH:mm") ?? "";

    /// <summary>"owner/repo" or "Project/Repo".</summary>
    public static string RepositoryLabel(UnifiedPullRequest pr) => $"{pr.Container}/{pr.Repository}";

    public static string BranchLabel(UnifiedPullRequest pr) => $"{pr.SourceBranch} → {pr.TargetBranch}";

    /// <summary>Reviewer initials, e.g. "AL · GH". Empty when nobody is on it.</summary>
    public static string ReviewerLabel(UnifiedPullRequest pr) => string.Join(" · ", pr.Reviewers.Select(r => r.Initials));

    public static string ChangesHeader(PullRequestDetail detail) =>
        $"Changes ({detail.Files.Count} files, +{detail.Insertions} −{detail.Deletions})";
}
