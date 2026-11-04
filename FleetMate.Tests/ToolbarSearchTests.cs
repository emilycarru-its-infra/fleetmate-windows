using FleetMate.GUI.Views.Shared;
using Xunit;

namespace FleetMate.Tests;

/// <summary>The toolbar search dropdown's grouping.</summary>
public class ToolbarSearchTests
{
    private static ToolbarSearchResult Hit(string category, string title) => new(category, title, "", () => { });

    [Fact]
    public void Group_KeepsFirstSeenCategoryOrderAndCapsEachGroup()
    {
        var hits = new List<ToolbarSearchResult>
        {
            Hit("Pull Requests", "a"), Hit("Commits", "b"), Hit("Pull Requests", "c"), Hit("Pull Requests", "d"),
        };

        var groups = ToolbarSearch.Group(hits, perCategory: 2);

        Assert.Equal(new[] { "Pull Requests", "Commits" }, groups.Select(g => g.Category));
        Assert.Equal(new[] { "a", "c" }, groups[0].Hits.Select(h => h.Title));
    }

    [Fact]
    public void SearchFieldKeepsItsFullWidthInANarrowWindow() =>
        Assert.Equal(MainWindow.SearchFullWidth, MainWindow.SearchCompactWidth);

    [Theory]
    [InlineData(700, false, false)]
    [InlineData(400, true, false)]
    [InlineData(140, true, true)]
    [InlineData(0, true, true)]
    public void NavigationYieldsBeforeTheSearchField(double available, bool compact, bool picker) =>
        Assert.Equal((compact, picker), MainWindow.NavigationFit(available, 600, 300));

    [Theory]
    [InlineData(true, true, "Searching…")]
    [InlineData(true, false, "No matches.")]
    [InlineData(false, false, "Search is not available yet.")]
    public void EmptyDropdownSaysSearchingUntilSourcesHaveLoaded(bool provider, bool searching, string expected) =>
        Assert.Equal(expected, ToolbarSearch.EmptyMessage(provider, searching));

    [Theory]
    [InlineData(5, 380, 400, false)] // room for every segment
    [InlineData(5, 380, 300, true)]  // too narrow: one pill
    [InlineData(2, 380, 100, false)] // a two-way switch never folds
    [InlineData(4, 300, 0, false)]   // not laid out yet
    public void SegmentSwitchFoldsOnlyWhenItDoesNotFit(int count, double needed, double available, bool fold) =>
        Assert.Equal(fold, SegmentMenuButton.ShouldFold(count, needed, available));
}
