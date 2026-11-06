using FleetMate.Core.Models.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Assign to me, Reassign and Unassign wait for Save, as on the Mac, and go
/// in the same PATCH as the other held fields.
/// </summary>
public class TicketPendingResponsibleTests
{
    private static readonly Guid Owner = new("5a7c0d2e-0000-0000-0000-000000000001");
    private static readonly Guid Me = new("5a7c0d2e-0000-0000-0000-000000000002");
    private static readonly Guid Colleague = new("5a7c0d2e-0000-0000-0000-000000000003");

    private static TdxTicket Ticket(Guid? responsible = null, int groupId = 4) => new()
    {
        Id = 21,
        StatusId = 5,
        PriorityId = 7,
        ResponsibleGroupId = groupId,
        ResponsibleUid = responsible,
        ResponsibleFullName = responsible == null ? null : "Owner Person",
    };

    [Fact]
    public void AssignToMeIsHeldUntilSave()
    {
        var edits = new TicketPendingEdits(Ticket(Owner));

        Assert.True(edits.SetResponsible(Me, "Me Person"));

        Assert.True(edits.HasEdits);
        Assert.True(edits.IsResponsibleEdited);
        Assert.Equal(Me, edits.ResponsibleUid);
        Assert.Equal("Me Person", edits.ResponsibleName);
        Assert.Equal(new[] { TicketDetailFields.Responsible }, edits.Fields);
    }

    [Fact]
    public void TheResponsibleGoesInTheSamePatchAsTheOtherFields()
    {
        var edits = new TicketPendingEdits(Ticket(Owner));
        edits.Set(TicketDetailFields.Status, 6);
        edits.SetResponsible(Colleague, "Colleague Person");

        var patch = edits.Patch();

        Assert.NotNull(patch);
        Assert.Equal(2, patch!.Count);
        Assert.Equal(6, patch["StatusID"]);
        Assert.Equal(Colleague.ToString(), patch["ResponsibleUid"]);
    }

    [Fact]
    public void UnassignSendsAnEmptyUid()
    {
        var edits = new TicketPendingEdits(Ticket(Owner));

        Assert.True(edits.SetResponsible(null, null));

        Assert.Null(edits.ResponsibleUid);
        Assert.Null(edits.ResponsibleName);
        var patch = edits.Patch();
        Assert.Equal("", patch!["ResponsibleUid"]);
    }

    [Fact]
    public void UnassigningAnUnassignedTicketIsNoEdit()
    {
        var edits = new TicketPendingEdits(Ticket(Guid.Empty));

        Assert.False(edits.SetResponsible(null, null));

        Assert.False(edits.HasEdits);
        Assert.Null(edits.Patch());
    }

    [Fact]
    public void ChoosingTheSavedResponsibleAgainClearsTheEdit()
    {
        var edits = new TicketPendingEdits(Ticket(Owner));
        edits.SetResponsible(Me, "Me Person");

        Assert.True(edits.SetResponsible(Owner, "Owner Person"));

        Assert.False(edits.HasEdits);
        Assert.Equal("Owner Person", edits.ResponsibleName);
    }

    [Fact]
    public void AGroupTakesTheTicketOnItsOwn()
    {
        var edits = new TicketPendingEdits(Ticket(Owner));

        Assert.True(edits.AssignToGroup(40, "Second Group"));

        Assert.Equal("Second Group", edits.GroupName);
        Assert.Equal(40, edits.Value(TicketDetailFields.Group));
        Assert.Null(edits.ResponsibleUid);
        var patch = edits.Patch();
        Assert.Equal(40, patch!["ResponsibleGroupID"]);
        Assert.Equal("", patch["ResponsibleUid"]);
    }

    [Fact]
    public void ReassigningToTheCurrentGroupOnlyClearsThePerson()
    {
        var edits = new TicketPendingEdits(Ticket(Owner, groupId: 4));

        edits.AssignToGroup(4, "First Group");

        Assert.Null(edits.GroupName);
        Assert.Equal(new[] { TicketDetailFields.Responsible }, edits.Fields);
    }

    [Fact]
    public void NotifyStartsTickedAndAppliesOnlyToANewPerson()
    {
        var edits = new TicketPendingEdits(Ticket(Owner));
        Assert.True(edits.NotifyNewResponsible);
        Assert.False(edits.ShouldNotify());

        edits.Set(TicketDetailFields.Status, 6);
        Assert.False(edits.ShouldNotify());

        edits.SetResponsible(Colleague, "Colleague Person");
        Assert.True(edits.ShouldNotify());

        edits.NotifyNewResponsible = false;
        Assert.False(edits.ShouldNotify());
    }

    [Fact]
    public void NotifyIsDecidedAgainstTheFreshTicket()
    {
        var edits = new TicketPendingEdits(Ticket(Owner));
        edits.SetResponsible(Colleague, "Colleague Person");

        // Someone else already made the colleague responsible.
        var latest = Ticket(Colleague);

        Assert.False(edits.ShouldNotify(latest));
        Assert.Null(edits.Patch(latest));
    }

    [Fact]
    public void ARedrawThatAlreadyHoldsTheResponsibleDropsTheEdit()
    {
        var edits = new TicketPendingEdits(Ticket(Owner));
        edits.SetResponsible(Me, "Me Person");
        edits.Set(TicketDetailFields.Status, 6);

        edits.Rebase(Ticket(Me));

        Assert.False(edits.IsResponsibleEdited);
        Assert.Equal(new[] { TicketDetailFields.Status }, edits.Fields);
    }

    [Fact]
    public void DiscardRevertsTheResponsibleAndTicksNotifyAgain()
    {
        var edits = new TicketPendingEdits(Ticket(Owner));
        edits.AssignToGroup(40, "Second Group");
        edits.NotifyNewResponsible = false;

        edits.Clear();

        Assert.False(edits.HasEdits);
        Assert.Equal(Owner, edits.ResponsibleUid);
        Assert.Equal("Owner Person", edits.ResponsibleName);
        Assert.Null(edits.GroupName);
        Assert.Equal(4, edits.Value(TicketDetailFields.Group));
        Assert.True(edits.NotifyNewResponsible);
    }
}
