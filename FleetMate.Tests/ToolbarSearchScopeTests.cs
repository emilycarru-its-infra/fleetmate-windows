using FleetMate.GUI.Views.Shared;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The one toolbar search field: which scope a tab opens in, what Ctrl+F and the
/// chip do, and what the field says.
/// </summary>
public class ToolbarSearchScopeTests
{
    [Theory]
    [InlineData(true, ToolbarSearchMode.Tab)]
    [InlineData(false, ToolbarSearchMode.All)]
    public void A_tab_opens_in_its_own_scope_when_it_has_a_filter(bool hasFilter, ToolbarSearchMode expected)
    {
        Assert.Equal(expected, ToolbarSearchScopes.ForTab(hasFilter));
        Assert.Equal(expected, ToolbarSearchScopes.ForFind(hasFilter));
    }

    [Theory]
    [InlineData(ToolbarSearchMode.Tab, true, ToolbarSearchMode.All)]
    [InlineData(ToolbarSearchMode.All, true, ToolbarSearchMode.Tab)]
    [InlineData(ToolbarSearchMode.All, false, ToolbarSearchMode.All)]
    public void The_chip_switches_scope_and_a_tab_without_a_filter_stays_on_everything(
        ToolbarSearchMode current, bool hasFilter, ToolbarSearchMode expected) =>
        Assert.Equal(expected, ToolbarSearchScopes.Toggle(current, hasFilter));

    [Fact]
    public void The_chip_names_the_tab_or_says_All()
    {
        Assert.Equal("Tickets", ToolbarSearchScopes.ChipLabel(ToolbarSearchMode.Tab, "Tickets"));
        Assert.Equal("All", ToolbarSearchScopes.ChipLabel(ToolbarSearchMode.All, "Tickets"));
    }

    [Theory]
    [InlineData(ToolbarSearchMode.Tab, "Search tickets or requestor", "Search tickets or requestor")]
    [InlineData(ToolbarSearchMode.All, "Search tickets or requestor", "Search everything")]
    [InlineData(ToolbarSearchMode.Tab, null, "Search everything")]
    public void The_placeholder_follows_the_scope(ToolbarSearchMode mode, string? prompt, string expected) =>
        Assert.Equal(expected, ToolbarSearchScopes.Placeholder(mode, prompt));
}
