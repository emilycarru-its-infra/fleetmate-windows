using FleetMate.GUI.Views.Shared;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Where the pull request detail places each section: two columns in a wide
/// pane, one column in a narrow one, and a missing side gives its row away.
/// </summary>
public class PullRequestDetailLayoutTests
{
    private static readonly GridPlacement FullTop = new(0, 0, PullRequestDetailLayout.ColumnCount);

    [Theory]
    [InlineData(899.9, false)]
    [InlineData(900, true)]
    [InlineData(1400, true)]
    [InlineData(0, false)]
    public void TwoColumnsFromAbout900Pixels(double width, bool wide) =>
        Assert.Equal(wide, PullRequestDetailLayout.IsWide(width));

    [Fact]
    public void WidePaneSplitsTheFirstTwoRowsAndGivesChangesTheFullWidth()
    {
        var layout = PullRequestDetailLayout.For(wide: true, hasOverview: true, hasHandbook: true, hasComments: true);

        Assert.Equal(new GridPlacement(0, 0, 1), layout.Overview);
        Assert.Equal(new GridPlacement(0, 2, 1), layout.Handbook);
        Assert.Equal(new GridPlacement(1, 0, 1), layout.Comments);
        Assert.Equal(new GridPlacement(1, 2, 1), layout.Side);
        Assert.Equal(new GridPlacement(2, 0, PullRequestDetailLayout.ColumnCount), layout.Changes);
    }

    [Fact]
    public void OverviewSpansTheRowWhenThereAreNoHandbookPages()
    {
        var layout = PullRequestDetailLayout.For(wide: true, hasOverview: true, hasHandbook: false, hasComments: true);

        Assert.Equal(FullTop, layout.Overview);
    }

    [Fact]
    public void HandbookSpansTheRowWhenThereIsNoOverview()
    {
        var layout = PullRequestDetailLayout.For(wide: true, hasOverview: false, hasHandbook: true, hasComments: true);

        Assert.Equal(FullTop, layout.Handbook);
    }

    [Fact]
    public void ChecksAndCommitsFillTheRowWhenThereAreNoComments()
    {
        var layout = PullRequestDetailLayout.For(wide: true, hasOverview: true, hasHandbook: true, hasComments: false);

        Assert.Equal(new GridPlacement(1, 0, PullRequestDetailLayout.ColumnCount), layout.Side);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    public void NarrowPaneIsOneColumnInReadingOrder(bool hasOverview, bool hasHandbook, bool hasComments)
    {
        var layout = PullRequestDetailLayout.For(wide: false, hasOverview, hasHandbook, hasComments);
        var order = new[] { layout.Overview, layout.Handbook, layout.Comments, layout.Side, layout.Changes };

        Assert.All(order, p => Assert.Equal((0, PullRequestDetailLayout.ColumnCount), (p.Column, p.ColumnSpan)));
        Assert.Equal(Enumerable.Range(0, order.Length), order.Select(p => p.Row));
        Assert.True(order.Length <= PullRequestDetailLayout.RowCount);
    }
}
