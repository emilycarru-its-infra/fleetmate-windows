using FleetMate.Core.Models.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>A card dropped back on its own column is not an edit.</summary>
public class TicketBoardDropTests
{
    private static TdxTicket Ticket(string? responsible = null, string? group = null, string? status = "Open", string? priority = "Medium") => new()
    {
        Id = 1,
        ResponsibleFullName = responsible,
        ResponsibleGroupName = group,
        StatusName = status,
        PriorityName = priority,
    };

    [Theory]
    [InlineData("Responsible", "Unassigned")]
    [InlineData("Group", "No Group")]
    [InlineData("Status", "Open")]
    [InlineData("Priority", "Medium")]
    public void DroppingOnItsOwnColumnIsNotAMove(string groupBy, string column)
    {
        Assert.False(TicketBoardLayout.IsMove(Ticket(), groupBy, column));
    }

    [Theory]
    [InlineData("Responsible", "A. Person")]
    [InlineData("Group", "Service Desk")]
    [InlineData("Status", "Resolved")]
    [InlineData("Priority", "High")]
    public void DroppingOnAnotherColumnIsAMove(string groupBy, string column)
    {
        Assert.True(TicketBoardLayout.IsMove(Ticket(), groupBy, column));
    }

    [Fact]
    public void AnAssignedTicketDroppedOnItsPersonIsNotAMove()
    {
        Assert.False(TicketBoardLayout.IsMove(Ticket(responsible: "A. Person"), "Responsible", "A. Person"));
        Assert.True(TicketBoardLayout.IsMove(Ticket(responsible: "A. Person"), "Responsible", "Unassigned"));
    }

    [Fact]
    public void TheConfirmationNamesTheFieldBeingChanged()
    {
        Assert.Equal("responsible person", TicketBoardLayout.FieldLabel("Responsible"));
        Assert.Equal("status", TicketBoardLayout.FieldLabel("Status"));
    }
}
