using FleetMate.GUI.Views.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Rebuilding the ticket rows (fold, sort, filter, Refresh) selects the open
/// ticket again. That must leave the detail pane alone, and only a different
/// ticket, or one picked after the pane was closed, loads.
/// </summary>
public class TicketReselectTests
{
    [Fact]
    public void TheOpenTicketSelectedAgainIsNotReloaded() =>
        Assert.True(TicketsPageSync.IsSameOpenTicket(42, 42, detailVisible: true));

    [Fact]
    public void ADifferentTicketLoads() =>
        Assert.False(TicketsPageSync.IsSameOpenTicket(43, 42, detailVisible: true));

    [Fact]
    public void ATicketPickedWhileThePaneIsClosedLoads()
    {
        Assert.False(TicketsPageSync.IsSameOpenTicket(42, 42, detailVisible: false));
        Assert.False(TicketsPageSync.IsSameOpenTicket(42, null, detailVisible: true));
    }
}
