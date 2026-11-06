using FleetMate.Core.Models.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Detail field picks wait for Save, as on the Mac, and Save sends them as
/// one PATCH of only the fields that still differ from the ticket.
/// </summary>
public class TicketPendingEditsTests
{
    private static readonly Guid FirstPerson = new("8b1d6f0e-0000-0000-0000-000000000001");
    private static readonly Guid SecondPerson = new("8b1d6f0e-0000-0000-0000-000000000002");

    private static TdxTicket Ticket(int statusId = 5, int priorityId = 7) => new()
    {
        Id = 12,
        StatusId = statusId,
        PriorityId = priorityId,
        Classification = 46,
        FormId = 3,
        ServiceId = 9,
        TypeId = 2,
        ResponsibleGroupId = 4,
        RequestorUid = FirstPerson,
    };

    [Fact]
    public void StartsWithNoEdits()
    {
        var edits = new TicketPendingEdits(Ticket());

        Assert.False(edits.HasEdits);
        Assert.Null(edits.Patch());
        Assert.Equal(12, edits.TicketId);
    }

    [Fact]
    public void APickIsHeldNotSent()
    {
        var edits = new TicketPendingEdits(Ticket());

        Assert.True(edits.Set(TicketDetailFields.Status, 6));

        Assert.True(edits.HasEdits);
        Assert.True(edits.IsEdited(TicketDetailFields.Status));
        Assert.Equal(6, edits.Value(TicketDetailFields.Status));
        Assert.Equal(7, edits.Value(TicketDetailFields.Priority));
    }

    [Fact]
    public void SaveIsOnePatchOfEveryChangedField()
    {
        var edits = new TicketPendingEdits(Ticket());
        edits.Set(TicketDetailFields.Status, 6);
        edits.Set(TicketDetailFields.Priority, 8);
        edits.Set(TicketDetailFields.Classification, 47);
        edits.Set(TicketDetailFields.Form, 31);
        edits.Set(TicketDetailFields.Service, 90);
        edits.Set(TicketDetailFields.Type, 20);
        edits.Set(TicketDetailFields.Group, 40);
        edits.Set(TicketDetailFields.Requestor, SecondPerson, "Second Person");

        var patch = edits.Patch();

        Assert.NotNull(patch);
        Assert.Equal(8, patch!.Count);
        Assert.Equal(6, patch["StatusID"]);
        Assert.Equal(8, patch["PriorityID"]);
        Assert.Equal(47, patch["Classification"]);
        Assert.Equal(31, patch["FormID"]);
        Assert.Equal(90, patch["ServiceID"]);
        Assert.Equal(20, patch["TypeID"]);
        Assert.Equal(40, patch["ResponsibleGroupID"]);
        Assert.Equal(SecondPerson, patch["RequestorUid"]);
        Assert.Equal("Second Person", edits.RequestorName);
    }

    [Fact]
    public void PickingTheSavedValueAgainIsNoLongerAnEdit()
    {
        var edits = new TicketPendingEdits(Ticket());
        edits.Set(TicketDetailFields.Status, 6);

        Assert.True(edits.Set(TicketDetailFields.Status, 5));

        Assert.False(edits.HasEdits);
        Assert.Null(edits.Patch());
    }

    [Fact]
    public void PickingTheSameValueTwiceChangesNothing()
    {
        var edits = new TicketPendingEdits(Ticket());
        edits.Set(TicketDetailFields.Priority, 8);

        Assert.False(edits.Set(TicketDetailFields.Priority, 8));
        Assert.Single(edits.Fields);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ABlankPickIsIgnored(int id)
    {
        var edits = new TicketPendingEdits(Ticket());
        edits.Set(TicketDetailFields.Status, 6);

        Assert.False(edits.Set(TicketDetailFields.Status, id));
        Assert.Equal(6, edits.Value(TicketDetailFields.Status));
    }

    [Fact]
    public void AnEmptyRequestorIsIgnored()
    {
        var edits = new TicketPendingEdits(Ticket());

        Assert.False(edits.Set(TicketDetailFields.Requestor, Guid.Empty));
        Assert.False(edits.HasEdits);
    }

    [Fact]
    public void AnUnknownFieldIsRefused()
    {
        var edits = new TicketPendingEdits(Ticket());

        Assert.Throws<ArgumentOutOfRangeException>(() => edits.Set("Title", "x"));
    }

    [Fact]
    public void SaveLeavesOutWhatTheLatestTicketAlreadyHolds()
    {
        // Someone else set the same status meanwhile: only the priority goes.
        var edits = new TicketPendingEdits(Ticket());
        edits.Set(TicketDetailFields.Status, 6);
        edits.Set(TicketDetailFields.Priority, 8);

        var patch = edits.Patch(Ticket(statusId: 6));

        Assert.Equal(new[] { "PriorityID" }, patch!.Keys);
    }

    [Fact]
    public void SaveIsNothingWhenTheLatestTicketHoldsEveryPick()
    {
        var edits = new TicketPendingEdits(Ticket());
        edits.Set(TicketDetailFields.Status, 6);

        Assert.Null(edits.Patch(Ticket(statusId: 6)));
    }

    [Fact]
    public void ATicketFromAnotherIdIsRefused()
    {
        var edits = new TicketPendingEdits(Ticket());
        var other = Ticket();
        other.Id = 99;

        Assert.Throws<ArgumentException>(() => edits.Patch(other));
        Assert.Throws<ArgumentException>(() => edits.Rebase(other));
    }

    [Fact]
    public void ARedrawOfTheSameTicketKeepsThePicks()
    {
        var edits = new TicketPendingEdits(Ticket());
        edits.Set(TicketDetailFields.Status, 6);
        edits.Set(TicketDetailFields.Priority, 8);

        // A newer copy after another change saved; its priority now matches.
        edits.Rebase(Ticket(priorityId: 8));

        Assert.True(edits.HasEdits);
        Assert.Equal(new[] { TicketDetailFields.Status }, edits.Fields);
        Assert.Equal(8, edits.Value(TicketDetailFields.Priority));
    }

    [Fact]
    public void ClearDropsEveryPick()
    {
        var edits = new TicketPendingEdits(Ticket());
        edits.Set(TicketDetailFields.Type, 20);
        edits.Set(TicketDetailFields.Requestor, SecondPerson, "Second Person");

        edits.Clear();

        Assert.False(edits.HasEdits);
        Assert.Null(edits.RequestorName);
        Assert.Equal(FirstPerson, edits.Value(TicketDetailFields.Requestor));
    }
}
