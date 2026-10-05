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
    // group including search, gap 16. The right group gets half the width
    // less half the tab bar.
    [Theory]
    [InlineData(2000, false, 0)]
    [InlineData(1600, true, 0)]
    [InlineData(1400, true, 76)] // 200 + 16 + 560 = 776, half is 700
    public void ToolbarCollapsesTabNamesThenShiftsTheTabs(double width, bool compact, double shift) =>
        Assert.Equal((compact, shift), FleetMate.GUI.Views.Shared.MainWindow.Fit(width, 800, 400, 560, 16));
}
