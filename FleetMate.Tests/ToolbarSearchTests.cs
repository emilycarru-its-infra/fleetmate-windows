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

    // Tabs 800 wide (400 with inactive names hidden), 560 of right-hand
    // group including search (440 once it yields), gap 16. The right group
    // gets half the width less half the tab bar.
    [Theory]
    [InlineData(2000, false, false, 0)]
    [InlineData(1900, true, false, 0)]   // 400 + 16 + 560 = 976 > 950; with 440, 856 fits
    [InlineData(1600, true, true, 0)]    // 400 + 16 + 440 = 856 > 800; tab names go
    [InlineData(1200, true, true, 56)]   // 200 + 16 + 440 = 656, half is 600
    public void ToolbarYieldsTheRightGroupBeforeTabNamesThenShiftsTheTabs(
        double width, bool compactRight, bool compactTabs, double shift) =>
        Assert.Equal((compactRight, compactTabs, shift),
            FleetMate.GUI.Views.Shared.MainWindow.Fit(width, 800, 400, 560, 440, 16));

    [Fact]
    public void SearchFieldNarrowsInANarrowWindow() =>
        Assert.True(FleetMate.GUI.Views.Shared.MainWindow.SearchCompactWidth < FleetMate.GUI.Views.Shared.MainWindow.SearchFullWidth);

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
