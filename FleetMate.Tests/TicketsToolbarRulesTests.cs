using FleetMate.GUI.Views.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The Tickets toolbar is one row, in the Mac's order. What it shows depends on
/// the view and the width; these are the rules behind it.
/// </summary>
public class TicketsToolbarRulesTests
{
    [Theory]
    [InlineData(false, false, TicketsViewMode.List)]
    [InlineData(true, false, TicketsViewMode.Board)]
    [InlineData(false, true, TicketsViewMode.Inbox)]
    [InlineData(true, true, TicketsViewMode.Inbox)]
    public void TheModeFollowsTheRadios(bool board, bool inbox, TicketsViewMode expected) =>
        Assert.Equal(expected, TicketsToolbarRules.ModeOf(board, inbox));

    [Theory]
    [InlineData(TicketsViewMode.List, false)]
    [InlineData(TicketsViewMode.Board, true)]
    [InlineData(TicketsViewMode.Inbox, false)]
    public void SortControlsShowOnTheBoardOnly(TicketsViewMode mode, bool shown)
    {
        // List view sorts from its column headers, as on Mac.
        Assert.Equal(shown, TicketsToolbarRules.ShowsSortControls(mode));
        Assert.Equal(shown, TicketsToolbarRules.ShowsBoardGrouping(mode));
    }

    [Theory]
    [InlineData(TicketsViewMode.List, true)]
    [InlineData(TicketsViewMode.Board, true)]
    [InlineData(TicketsViewMode.Inbox, false)]
    public void FoldControlsShowWhereThereIsAnOutline(TicketsViewMode mode, bool shown) =>
        Assert.Equal(shown, TicketsToolbarRules.ShowsFoldControls(mode));

    [Theory]
    [InlineData(TicketsViewMode.Board)]
    [InlineData(TicketsViewMode.Inbox)]
    [InlineData(TicketsViewMode.List)]
    public void AssignedToMeOnSwitchesToList(TicketsViewMode from) =>
        Assert.Equal(TicketsViewMode.List, TicketsToolbarRules.ViewAfterAssignedToMe(from, true));

    [Theory]
    [InlineData(TicketsViewMode.Board)]
    [InlineData(TicketsViewMode.Inbox)]
    [InlineData(TicketsViewMode.List)]
    public void AssignedToMeOffLeavesTheView(TicketsViewMode from) =>
        Assert.Equal(from, TicketsToolbarRules.ViewAfterAssignedToMe(from, false));

    [Fact]
    public void LabelsDropToIconsOnlyWhenNarrow()
    {
        Assert.False(TicketsToolbarRules.UsesCompactLabels(0, TicketsViewMode.Board)); // not laid out yet
        Assert.False(TicketsToolbarRules.UsesCompactLabels(2000, TicketsViewMode.Board));
        Assert.True(TicketsToolbarRules.UsesCompactLabels(1200, TicketsViewMode.Board));
        Assert.True(TicketsToolbarRules.UsesCompactLabels(1200, TicketsViewMode.List));
    }

    [Fact]
    public void TheBoardGoesCompactSoonerThanTheList()
    {
        var width = (TicketsToolbarRules.CompactBelowOnBoard + TicketsToolbarRules.CompactBelowOnList) / 2;
        Assert.True(TicketsToolbarRules.UsesCompactLabels(width, TicketsViewMode.Board));
        Assert.False(TicketsToolbarRules.UsesCompactLabels(width, TicketsViewMode.List));
    }

    [Fact]
    public void TheSortDirectionSaysWhichWayItIs()
    {
        Assert.Equal("Ascending", TicketsToolbarRules.SortDirectionName(true));
        Assert.Equal("Descending", TicketsToolbarRules.SortDirectionName(false));
        Assert.StartsWith("Sorted ascending", TicketsToolbarRules.SortDirectionToolTip(true));
        Assert.StartsWith("Sorted descending", TicketsToolbarRules.SortDirectionToolTip(false));
    }

    [Fact]
    public void TheSortBarsGrowWhenAscendingAndShrinkWhenDescending()
    {
        var up = TicketsToolbarRules.SortBarWidths(true);
        var down = TicketsToolbarRules.SortBarWidths(false);
        Assert.True(up[0] < up[1] && up[1] < up[2]);
        Assert.True(down[0] > down[1] && down[1] > down[2]);
    }

    [Fact]
    public void FilterButtonsShrinkWhenCompact()
    {
        var none = new HashSet<string>();
        var one = new HashSet<string> { "New" };
        var two = new HashSet<string> { "New", "Open" };

        Assert.Equal("Status: All", TicketsToolbarRules.FilterButtonText("Status", none, compact: false));
        Assert.Equal("Status", TicketsToolbarRules.FilterButtonText("Status", none, compact: true));
        Assert.Equal("Status: New", TicketsToolbarRules.FilterButtonText("Status", one, compact: true));
        Assert.Equal("Status: 2 selected", TicketsToolbarRules.FilterButtonText("Status", two, compact: false));
        Assert.Equal("Status: 2", TicketsToolbarRules.FilterButtonText("Status", two, compact: true));
    }

    [Fact]
    public void AFilterButtonsAccessibleNameKeepsTheWords()
    {
        Assert.Equal("Status: All", TicketsToolbarRules.FilterButtonName("Status", new HashSet<string>()));
        Assert.DoesNotContain("▾", TicketsToolbarRules.FilterButtonName("Status", new HashSet<string> { "New" }));
    }

    [Fact]
    public void TheInboxTooltipCountsUnread()
    {
        Assert.Contains("3 unread", TicketsToolbarRules.InboxToolTip(3));
        Assert.DoesNotContain("unread", TicketsToolbarRules.InboxToolTip(0));
    }
}
