using FleetMate.Core.Shared;
using Xunit;

namespace FleetMate.Tests;

public class DiffSplitRowsTests
{
    private static DiffHunk Hunk(params DiffLine[] lines) =>
        new() { Header = "@@ -1 +1 @@", OldStart = 1, OldCount = 1, NewStart = 1, NewCount = 1, Lines = lines.ToList() };

    private static DiffLine Line(DiffLineKind kind, string content, int? oldLine = null, int? newLine = null) =>
        new() { Kind = kind, Content = content, OldLine = oldLine, NewLine = newLine };

    [Fact]
    public void ContextShowsOnBothSides()
    {
        var line = Line(DiffLineKind.Context, "same", 1, 1);
        var rows = DiffSplit.Rows(Hunk(line));
        Assert.Equal(new[] { new DiffSplitRow(line, line) }, rows);
    }

    [Fact]
    public void ChangedLineSitsOppositeItsReplacement()
    {
        var old = Line(DiffLineKind.Deletion, "old", oldLine: 7);
        var added = Line(DiffLineKind.Addition, "new", newLine: 7);
        Assert.Equal(new[] { new DiffSplitRow(old, added) }, DiffSplit.Rows(Hunk(old, added)));
    }

    [Fact]
    public void UnevenRunsLeaveTheShorterSideEmpty()
    {
        var removed = Enumerable.Range(1, 3).Select(n => Line(DiffLineKind.Deletion, $"r{n}", oldLine: n)).ToArray();
        var added = Line(DiffLineKind.Addition, "a1", newLine: 1);
        var rows = DiffSplit.Rows(Hunk(removed.Append(added).ToArray()));

        Assert.Equal(3, rows.Count);
        Assert.Same(added, rows[0].Right);
        Assert.Null(rows[1].Right);
        Assert.Null(rows[2].Right);
        Assert.Equal(removed, rows.Select(r => r.Left));
    }

    [Fact]
    public void PureAdditionHasNoLeftSide()
    {
        var added = Line(DiffLineKind.Addition, "a", newLine: 1);
        Assert.Equal(new[] { new DiffSplitRow(null, added) }, DiffSplit.Rows(Hunk(added)));
    }

    [Fact]
    public void NoNewlineMarkerStaysWithItsSide()
    {
        var old = Line(DiffLineKind.Deletion, "x", oldLine: 1);
        var marker = Line(DiffLineKind.NoNewline, "No newline at end of file");
        var added = Line(DiffLineKind.Addition, "x", newLine: 1);
        var rows = DiffSplit.Rows(Hunk(old, marker, added));

        Assert.Equal(new[] { new DiffSplitRow(old, added), new DiffSplitRow(marker, null) }, rows);
    }

    [Fact]
    public void MarkerAfterAnAdditionGoesRight()
    {
        var added = Line(DiffLineKind.Addition, "x", newLine: 1);
        var marker = Line(DiffLineKind.NoNewline, "No newline at end of file");
        Assert.Equal(new[] { new DiffSplitRow(null, added), new DiffSplitRow(null, marker) },
            DiffSplit.Rows(Hunk(added, marker)));
    }

    [Theory]
    [InlineData(null, DiffLayout.Split)]
    [InlineData("split", DiffLayout.Split)]
    [InlineData("inline", DiffLayout.Inline)]
    [InlineData("Inline", DiffLayout.Inline)]
    [InlineData("other", DiffLayout.Split)]
    public void LayoutDefaultsToSideBySide(string? stored, DiffLayout expected) =>
        Assert.Equal(expected, DiffLayouts.Parse(stored));

    [Fact]
    public void LayoutRoundTripsTheMacValues()
    {
        Assert.Equal("split", DiffLayouts.Format(DiffLayout.Split));
        Assert.Equal("inline", DiffLayouts.Format(DiffLayout.Inline));
        Assert.Equal("Side by Side", DiffLayouts.Title(DiffLayout.Split));
        Assert.Equal("Inline", DiffLayouts.Title(DiffLayout.Inline));
    }
}
