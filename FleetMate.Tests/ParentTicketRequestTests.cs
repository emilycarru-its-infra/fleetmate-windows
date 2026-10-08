using FleetMate.Core.Models.Tickets;
using Xunit;

namespace FleetMate.Tests;

public class ParentTicketRequestTests
{
    [Fact]
    public void ParentCarriesTheChildsQueueFields()
    {
        var requestor = Guid.NewGuid();
        var responsible = Guid.NewGuid();
        var child = new TdxTicket
        {
            Id = 42, Title = "Printer offline", TypeId = 7, FormId = 3, AccountId = 11,
            PriorityId = 20, SourceId = 8, ServiceId = 5,
            RequestorUid = requestor, ResponsibleUid = responsible, ResponsibleGroupId = 9,
        };

        var request = ParentTicketRequest.For(child, "  Printers offline in one building  ");

        Assert.Equal("Printers offline in one building", request.Title);
        Assert.Equal(7, request.TypeId);
        Assert.Equal(3, request.FormId);
        Assert.Equal(11, request.AccountId);
        Assert.Equal(20, request.PriorityId);
        Assert.Equal(8, request.SourceId);
        Assert.Equal(5, request.ServiceId);
        Assert.Equal(requestor, request.RequestorUid);
        Assert.Equal(responsible, request.ResponsibleUid);
        Assert.Equal(9, request.ResponsibleGroupId);
        Assert.Equal("Parent of ticket 42.", request.Description);
    }
}
