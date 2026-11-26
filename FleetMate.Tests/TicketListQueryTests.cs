using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The Tickets list's selection, board layout, drop rules, widget figures
/// and shared cache, which the WPF and WinUI apps both read.
/// </summary>
public class TicketListQueryTests
{
    private static readonly Guid Me = Guid.NewGuid();

    private static TdxTicket Ticket(int id, string status, string title = "Printer", string? requestor = null,
        string? responsible = null, Guid? responsibleUid = null, string? group = null, int? groupId = null,
        string priority = "Medium", int? priorityId = null, int statusId = 0) => new()
    {
        Id = id, Title = title, StatusName = status, StatusId = statusId, PriorityName = priority, PriorityId = priorityId,
        RequestorName = requestor, ResponsibleFullName = responsible, ResponsibleUid = responsibleUid,
        ResponsibleGroupName = group, ResponsibleGroupId = groupId, CreatedDate = DateTime.UtcNow,
    };

    private static List<TdxTicket> Sample() => new()
    {
        Ticket(1, "New", title: "Laptop won't boot", requestor: "Avery", responsible: "Sam", responsibleUid: Me, group: "Desk", groupId: 10, statusId: 1),
        Ticket(2, "In Process", title: "Printer jam", requestor: "Blake", group: "Desk", groupId: 10, priority: "High", priorityId: 3, statusId: 2),
        Ticket(3, "Closed", title: "Old request", requestor: "Casey", responsible: "Kim", group: "Lab", groupId: 20, statusId: 5),
        Ticket(4, "Cancelled", title: "Never mind", statusId: 6),
    };

    [Fact]
    public void ClosedAndCancelledHideUntilShowClosed()
    {
        var hidden = TicketListQuery.Matching(Sample(), new TicketListOptions(new TicketFilters()));
        var shown = TicketListQuery.Matching(Sample(), new TicketListOptions(new TicketFilters(), ShowClosed: true));

        Assert.Equal(new[] { 1, 2 }, hidden.Select(t => t.Id));
        Assert.Equal(4, shown.Count);
    }

    [Fact]
    public void AssignedToMeKeepsTheSignedInPersonsTickets()
    {
        var mine = TicketListQuery.Matching(Sample(), new TicketListOptions(new TicketFilters(), AssignedTo: new TdxPerson { Uid = Me }));

        Assert.Equal(new[] { 1 }, mine.Select(t => t.Id));
    }

    [Theory]
    [InlineData("printer", 2)]
    [InlineData("AVERY", 1)]
    [InlineData("2", 2)]
    public void SearchMatchesTitleRequestorOrNumber(string search, int expected)
    {
        var found = TicketListQuery.Matching(Sample(), new TicketListOptions(new TicketFilters(), Search: $"  {search} "));

        Assert.Equal(new[] { expected }, found.Select(t => t.Id));
    }

    [Fact]
    public void ACategoryCanBeLeftOutForItsOwnWidget()
    {
        var filters = new TicketFilters();
        filters.Toggle(TicketFilterCategory.Status, "New");
        var options = new TicketListOptions(filters);

        Assert.Equal(new[] { 1 }, TicketListQuery.Matching(Sample(), options).Select(t => t.Id));
        Assert.Equal(new[] { 1, 2 }, TicketListQuery.Matching(Sample(), options, TicketFilterCategory.Status).Select(t => t.Id));
    }

    [Fact]
    public void CountLabelReadsListedOfLoaded() => Assert.Equal("12 of 340", TicketListQuery.CountLabel(12, 340));

    [Fact]
    public void StatusBoardRunsInWorkflowOrder()
    {
        var blocks = TicketBoardLayout.Blocks(Sample(), "Status");

        var block = Assert.Single(blocks);
        Assert.Null(block.Header);
        Assert.Equal(new[] { "New", "In Process", "Closed", "Cancelled" }, block.Columns.Select(c => c.Key));
    }

    [Fact]
    public void PriorityBoardPutsTheMostUrgentFirst()
    {
        var columns = Assert.Single(TicketBoardLayout.Blocks(Sample(), "Priority")).Columns;

        Assert.Equal(new[] { "High", "Medium" }, columns.Select(c => c.Key));
    }

    [Fact]
    public void ResponsibleBoardIsOneBlockPerGroup()
    {
        var blocks = TicketBoardLayout.Blocks(Sample(), TicketBoardLayout.DefaultGroupBy);

        Assert.Equal(new[] { "Desk", "Lab", TicketBoardLayout.NoGroup }, blocks.Select(b => b.Header));
        Assert.Equal(new[] { "Sam", TicketBoardLayout.Unassigned }, blocks[0].Columns.Select(c => c.Key));
    }

    [Fact]
    public void DropBackOnItsOwnColumnSendsNothing()
    {
        var ticket = Sample()[0];

        Assert.Null(TicketBoardLayout.DropUpdates(Sample(), ticket, "Status", "New"));
    }

    [Fact]
    public void DropReadsTheIdFromALoadedTicket()
    {
        var all = Sample();

        Assert.Equal(2, TicketBoardLayout.DropUpdates(all, all[0], "Status", "In Process")!["StatusID"]);
        Assert.Equal(3, TicketBoardLayout.DropUpdates(all, all[0], "Priority", "High")!["PriorityID"]);
        Assert.Equal(20, TicketBoardLayout.DropUpdates(all, all[0], "Group", "Lab")!["ResponsibleGroupID"]);
        Assert.Equal(0, TicketBoardLayout.DropUpdates(all, all[0], "Group", TicketBoardLayout.NoGroup)!["ResponsibleGroupID"]);
        Assert.Equal("", TicketBoardLayout.DropUpdates(all, all[0], "Responsible", TicketBoardLayout.Unassigned)!["ResponsibleUid"]);
        Assert.Null(TicketBoardLayout.DropUpdates(all, all[0], "Priority", "Emergency"));
    }

    [Fact]
    public void DropConfirmationNamesTheFieldAndBothValues()
    {
        var ticket = Sample()[0];

        Assert.Equal("Change the status of #1 from New to Closed?\n\nLaptop won't boot",
            TicketBoardLayout.DropConfirmation(ticket, "Status", "Closed"));
    }

    [Fact]
    public void TilesToggleTheSelectionTheyCount()
    {
        var filters = new TicketFilters();
        var options = new TicketListOptions(filters);
        List<TdxTicket> Tickets(TicketFilterCategory? ignoring) => TicketListQuery.Matching(Sample(), options, ignoring);

        var tiles = TicketWidgetRules.Tiles(Tickets, filters);
        Assert.Equal(new[] { "Showing", "Open", "On Hold", "Unassigned", "SLA Violated", "Over 30 days" }, tiles.Select(t => t.Title));
        Assert.Equal(2, tiles[0].Value);

        tiles.Single(t => t.Title == "Unassigned").Action();
        Assert.True(filters.IsSelected(TicketFilterCategory.Responsible, TicketStats.Unassigned));
        Assert.True(TicketWidgetRules.Tiles(Tickets, filters).Single(t => t.Title == "Unassigned").IsOn);

        tiles.Single(t => t.Title == "Showing").Action();
        Assert.False(filters.HasActiveFilters);
    }

    [Fact]
    public void ChartsCountWithoutTheirOwnCategory()
    {
        var filters = new TicketFilters();
        filters.Toggle(TicketFilterCategory.Priority, "High");
        var options = new TicketListOptions(filters);

        var charts = TicketWidgetRules.Charts(ignoring => TicketListQuery.Matching(Sample(), options, ignoring));

        Assert.Equal(new[] { "By Status", "By Priority", "By Age", "By Responsible", "By Group" }, charts.Select(c => c.Title));
        Assert.Equal(2, charts[1].Counts.Sum(c => c.Value));
        Assert.Equal(1, charts[0].Counts.Sum(c => c.Value));
    }

    [Theory]
    [InlineData("New", TicketTone.Blue)]
    [InlineData("In Process", TicketTone.Orange)]
    [InlineData("On Hold", TicketTone.Yellow)]
    [InlineData("Awaiting Input", TicketTone.Teal)]
    [InlineData("Pending", TicketTone.Teal)]
    [InlineData("Resolved", TicketTone.Green)]
    [InlineData("Closed", TicketTone.Gray)]
    [InlineData("Something else", TicketTone.Secondary)]
    public void StatusTonesMatchTheMac(string status, TicketTone tone) => Assert.Equal(tone, TicketTones.Status(status));

    [Fact]
    public void CacheHoldsTicketsUntilClearedOrReset()
    {
        var cache = new TicketBoardCache(() => null, () => null, () => Task.CompletedTask, () => TimeSpan.FromMinutes(5));
        var changes = 0;
        cache.Changed += () => changes++;

        Assert.False(cache.IsValid);
        cache.Update(Sample());
        Assert.True(cache.IsValid);
        Assert.NotNull(cache.LastRefreshUtc);

        var edited = Ticket(2, "Resolved");
        Assert.True(cache.Replace(edited));
        Assert.False(cache.Replace(Ticket(99, "New")));
        Assert.Same(edited, cache.Tickets.Single(t => t.Id == 2));

        cache.Invalidate();
        Assert.False(cache.IsValid);
        Assert.Equal(4, cache.Tickets.Count);

        cache.Reset();
        Assert.Empty(cache.Tickets);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void CacheExpiresAfterItsLifetime()
    {
        var cache = new TicketBoardCache(() => null, () => null, () => Task.CompletedTask, () => TimeSpan.Zero);

        cache.Update(Sample());

        Assert.False(cache.IsValid);
    }

    [Fact]
    public async Task CacheRefusesToLoadWhenNotSignedIn()
    {
        var cache = new TicketBoardCache(() => null, () => null, () => Task.CompletedTask, () => TimeSpan.FromMinutes(5));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => cache.LoadAsync());
        Assert.Equal("Sign in to TeamDynamix to refresh tickets.", error.Message);
    }
}
