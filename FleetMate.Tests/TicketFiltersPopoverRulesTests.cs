using FleetMate.Core.Models.Tickets;
using FleetMate.GUI.Views.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The Tickets filters sit in one Filters popover, as on Mac. These are the
/// rules behind the button's count and the popover's lists.
/// </summary>
public class TicketFiltersPopoverRulesTests
{
    [Fact]
    public void TheSectionsFollowTheMacOrderWithTimePeriodLast()
    {
        Assert.Equal(
            new[] { "Status", "Priority", "Group", "Responsible", "Age", "SLA", "Time Period" },
            TicketFiltersPopoverRules.Sections.Select(s => s.Title));
        Assert.True(TicketFiltersPopoverRules.Sections[^1].IsTimePeriod);
        Assert.Single(TicketFiltersPopoverRules.Sections, s => s.IsTimePeriod);
    }

    [Fact]
    public void NothingIsActiveByDefault()
    {
        var filters = new TicketFilters();
        Assert.Equal(0, TicketFiltersPopoverRules.ActiveCount(filters, showClosed: false, defaultDateRange: true));
        Assert.False(TicketFiltersPopoverRules.ShowsClearAll(0));
    }

    [Fact]
    public void EveryPickedValueCounts()
    {
        var filters = new TicketFilters();
        filters.Set(TicketFilterCategory.Status, new[] { "New", "In Process" });
        filters.Toggle(TicketFilterCategory.Sla, TicketFilters.SlaViolated);

        Assert.Equal(3, TicketFiltersPopoverRules.ActiveCount(filters, showClosed: false, defaultDateRange: true));
        Assert.True(TicketFiltersPopoverRules.ShowsClearAll(3));
    }

    [Fact]
    public void ShowClosedAndAnotherDateWindowCountToo()
    {
        var filters = new TicketFilters();
        Assert.Equal(1, TicketFiltersPopoverRules.ActiveCount(filters, showClosed: true, defaultDateRange: true));
        Assert.Equal(1, TicketFiltersPopoverRules.ActiveCount(filters, showClosed: false, defaultDateRange: false));
        Assert.Equal(2, TicketFiltersPopoverRules.ActiveCount(filters, showClosed: true, defaultDateRange: false));
    }

    [Fact]
    public void EachSectionCountsItsOwn()
    {
        var filters = new TicketFilters();
        filters.Set(TicketFilterCategory.Priority, new[] { "High" });
        var priority = TicketFiltersPopoverRules.Sections.Single(s => s.Category == TicketFilterCategory.Priority);
        var status = TicketFiltersPopoverRules.Sections.Single(s => s.Category == TicketFilterCategory.Status);
        var time = TicketFiltersPopoverRules.Sections.Single(s => s.IsTimePeriod);

        Assert.Equal(1, TicketFiltersPopoverRules.SectionCount(priority, filters, true, false));
        Assert.Equal(0, TicketFiltersPopoverRules.SectionCount(status, filters, true, false));
        Assert.Equal(2, TicketFiltersPopoverRules.SectionCount(time, filters, true, false));
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "1")]
    [InlineData(12, "12")]
    [InlineData(150, "99+")]
    public void TheBadgeHidesAtZeroAndCaps(int count, string expected) =>
        Assert.Equal(expected, TicketFiltersPopoverRules.BadgeText(count));

    [Fact]
    public void TheButtonsNameSaysHowManyAreOn()
    {
        Assert.Equal("Filters", TicketFiltersPopoverRules.ButtonName(0));
        Assert.Equal("Filters, 1 active", TicketFiltersPopoverRules.ButtonName(1));
        Assert.Equal("Filters, 4 active", TicketFiltersPopoverRules.ButtonName(4));
        Assert.Contains("4 filters", TicketFiltersPopoverRules.ButtonToolTip(4));
        Assert.Equal("Filter the tickets", TicketFiltersPopoverRules.ButtonToolTip(0));
    }

    [Fact]
    public void ASectionsNameCarriesItsCount()
    {
        Assert.Equal("Status", TicketFiltersPopoverRules.SectionName("Status", 0));
        Assert.Equal("Status, 2 selected", TicketFiltersPopoverRules.SectionName("Status", 2));
    }

    [Fact]
    public void APickedValueNoLongerLoadedIsStillListed()
    {
        var values = TicketFiltersPopoverRules.Values(
            new[] { "New", "Open" }, new HashSet<string> { "Open", "Resolved" });
        Assert.Equal(new[] { "New", "Open", "Resolved" }, values);
    }

    [Fact]
    public void SearchMatchesAnyPartIgnoringCase()
    {
        var values = new[] { "In Process", "New", "On Hold" };
        Assert.Equal(new[] { "In Process", "On Hold" }, TicketFiltersPopoverRules.Search(values, " o"));
        Assert.Equal(new[] { "New" }, TicketFiltersPopoverRules.Search(values, "NEW"));
        Assert.Equal(values, TicketFiltersPopoverRules.Search(values, "  "));
        Assert.Equal(values, TicketFiltersPopoverRules.Search(values, null));
    }
}
