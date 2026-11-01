namespace FleetMate.GUI.Views.Shared;

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
