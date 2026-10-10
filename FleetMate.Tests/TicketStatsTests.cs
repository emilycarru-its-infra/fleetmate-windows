using FleetMate.Core.Models.Tickets;
using FleetMate.GUI.Views.Shared.Widgets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The Tickets widgets' figures and the filters they drive: counts follow
/// the tickets given, each breakdown can leave its own category out, and
/// values combine within and across categories. Mirrors the macOS tests.
/// </summary>
public class TicketStatsTests
{
    private static TdxTicket Ticket(int id, string status, string priority = "Medium", string? responsible = null,
        string? group = null, int days = 0, bool onHold = false, bool sla = false) => new()
    {
        Id = id, StatusName = status, PriorityName = priority, ResponsibleFullName = responsible,
        ResponsibleGroupName = group, DaysOld = days, IsOnHold = onHold, IsSlaViolated = sla,
        // DaysOld 0 falls back to the created date, so "today" means created now.
        CreatedDate = DateTime.UtcNow.AddDays(-days),
    };

    private static List<TdxTicket> Sample() => new()
    {
        Ticket(1, "New", priority: "High", responsible: "A", group: "Desk", days: 0),
        Ticket(2, "In Process", responsible: "A", group: "Desk", days: 20, sla: true),
        Ticket(3, "On Hold", priority: "Low", days: 40, onHold: true),
        Ticket(4, "Closed", responsible: "B", days: 3),
    };

    [Fact]
    public void CountsFollowTheTicketsGiven()
    {
        var stats = new TicketStats(Sample());

        Assert.Equal(4, stats.Total);
        Assert.Equal(2, stats.Open);
        Assert.Equal(1, stats.OnHold);
        Assert.Equal(1, stats.UnassignedCount);
        Assert.Equal(1, stats.SlaViolated);
        Assert.Equal(1, stats.Aging);
        Assert.Equal(new[] { "Low", "Medium", "High" }, stats.ByPriority.Select(c => c.Label));
        Assert.Equal(new TicketStats.Count("A", 2), stats.ByResponsible[0]);
        Assert.Contains(new TicketStats.Count(TicketStats.Unassigned, 1), stats.ByResponsible);
        Assert.DoesNotContain(stats.ByResponsible, c => c.Label == "B");
        Assert.Equal(new[] { "Today", "8–30 days", "Over 30 days" }, stats.ByAge.Select(c => c.Label));
    }

    [Fact]
    public void NoTicketsGivesEmptyFigures()
    {
        var stats = new TicketStats(Array.Empty<TdxTicket>());
        Assert.Equal(0, stats.Total);
        Assert.Empty(stats.ByStatus);
        Assert.Empty(stats.ByAge);
    }

    [Theory]
    [InlineData(0, "Today")]
    [InlineData(1, "1–7 days")]
    [InlineData(7, "1–7 days")]
    [InlineData(8, "8–30 days")]
    [InlineData(30, "8–30 days")]
    [InlineData(31, "Over 30 days")]
    public void AgeBandsMatchTheMacApp(int days, string expected) =>
        Assert.Equal(expected, TicketStats.AgeBucket(Ticket(1, "New", days: days)));

    [Fact]
    public void ValuesCombineWithinACategoryAndAcrossCategories()
    {
        var filters = new TicketFilters();
        filters.Toggle(TicketFilterCategory.Status, "New");
        filters.Toggle(TicketFilterCategory.Status, "In Process");
        var tickets = Sample();

        Assert.Equal(new[] { 1, 2 }, tickets.Where(t => filters.Matches(t)).Select(t => t.Id));

        filters.Toggle(TicketFilterCategory.Sla, TicketFilters.SlaViolated);
        Assert.Equal(new[] { 2 }, tickets.Where(t => filters.Matches(t)).Select(t => t.Id));

        filters.Toggle(TicketFilterCategory.Sla, TicketFilters.SlaViolated);
        Assert.Equal(new[] { 1, 2 }, tickets.Where(t => filters.Matches(t)).Select(t => t.Id));
    }

    [Fact]
    public void ABreakdownCountsWithItsOwnCategoryLeftOut()
    {
        var filters = new TicketFilters();
        filters.Toggle(TicketFilterCategory.Responsible, "A");
        filters.Toggle(TicketFilterCategory.Status, "New");
        var tickets = Sample();

        // Ignoring Status leaves A's two tickets, so In Process stays clickable.
        Assert.Equal(new[] { 1, 2 }, tickets.Where(t => filters.Matches(t, TicketFilterCategory.Status)).Select(t => t.Id));
        Assert.Equal(new[] { 1 }, tickets.Where(t => filters.Matches(t)).Select(t => t.Id));
    }

    [Fact]
    public void UnassignedAndAgeAreFilterValues()
    {
        var filters = new TicketFilters();
        filters.Toggle(TicketFilterCategory.Responsible, TicketStats.Unassigned);
        filters.Toggle(TicketFilterCategory.Age, TicketStats.AgingBucket);

        Assert.Equal(new[] { 3 }, Sample().Where(t => filters.Matches(t)).Select(t => t.Id));
        Assert.Contains(TicketStats.Unassigned, TicketFilters.AvailableValues(Sample(), TicketFilterCategory.Group));
        Assert.Equal(new[] { "Today", "1–7 days", "8–30 days", "Over 30 days" },
            TicketFilters.AvailableValues(Sample(), TicketFilterCategory.Age));
    }

    [Fact]
    public void ClearAllRemovesEverySelectionAndSignalsOnce()
    {
        var filters = new TicketFilters();
        filters.Toggle(TicketFilterCategory.Priority, "High");
        var changes = 0;
        filters.Changed += () => changes++;

        filters.ClearAll();

        Assert.False(filters.HasActiveFilters);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void AssignedToMeMatchesByPersonIdThenByName()
    {
        var uid = Guid.NewGuid();
        var me = new TdxPerson { Uid = uid, FullName = "Sam Example" };

        Assert.True(TicketFilters.IsAssignedTo(new TdxTicket { ResponsibleUid = uid, ResponsibleFullName = "Other" }, me));
        Assert.False(TicketFilters.IsAssignedTo(new TdxTicket { ResponsibleUid = Guid.NewGuid(), ResponsibleFullName = "Sam Example" }, me));
        Assert.True(TicketFilters.IsAssignedTo(new TdxTicket { ResponsibleFullName = "Sam Example" }, me));
        Assert.False(TicketFilters.IsAssignedTo(new TdxTicket(), me));
    }

    [Fact]
    public void OpenFigureSelectsEveryStatusButOnHold() =>
        Assert.Equal(new HashSet<string> { "New", "In Process", "Closed" }, TicketWidgets.OpenStatuses(Sample()));

    [Fact]
    public void UnpickedValuesFadeOnlyWhileTheCategoryHasASelection()
    {
        var filters = new TicketFilters();
        Assert.False(TicketWidgets.Faded(filters, TicketFilterCategory.Priority, "Low"));

        filters.Toggle(TicketFilterCategory.Priority, "High");
        Assert.True(TicketWidgets.Faded(filters, TicketFilterCategory.Priority, "Low"));
        Assert.False(TicketWidgets.Faded(filters, TicketFilterCategory.Priority, "High"));
    }
}
