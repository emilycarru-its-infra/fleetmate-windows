using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Shared;
using FleetMate.GUI.Views.Shared;
using FleetMate.GUI.Views.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The right-click menu of ticket rows and board cards: its order, when the
/// immediate-save items are offered, and that TicketsMate shows no agent items.
/// </summary>
public class TicketContextMenuTests
{
    private static readonly Guid Me = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Someone = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly Dictionary<int, string> Statuses = new() { [1] = "New", [2] = "In Process", [3] = "Resolved" };
    private static readonly Dictionary<int, string> Priorities = new() { [10] = "Low", [20] = "High" };

    private static TdxTicket Ticket(Guid? responsible = null) => new()
    {
        Id = 42,
        Title = "Printer jammed",
        StatusId = 2,
        StatusName = "In Process",
        PriorityId = 20,
        PriorityName = "High",
        ResponsibleUid = responsible,
    };

    private static IReadOnlyList<TicketMenuEntry> Build(TdxTicket ticket, Guid? me = null, bool ticketsOnly = false,
        bool canWrite = true, bool hasWebUrl = true) =>
        TicketMenuPlan.Build(ticket, me, ticketsOnly, canWrite, hasWebUrl, Statuses, Priorities);

    private static TicketMenuEntry Entry(IReadOnlyList<TicketMenuEntry> plan, TicketMenuCommand command) =>
        plan.Single(e => e.Command == command);

    [Fact]
    public void FleetMate_ListsEveryActionInOrder_WithTheAgentItemsLast()
    {
        var plan = Build(Ticket(Someone), Me);
        Assert.Equal(new TicketMenuCommand?[]
        {
            TicketMenuCommand.OpenInWeb, TicketMenuCommand.CopyNumber, TicketMenuCommand.CopyLink, TicketMenuCommand.CopyTitle,
            null,
            TicketMenuCommand.AssignToMe, TicketMenuCommand.Reassign, TicketMenuCommand.Unassign,
            TicketMenuCommand.Status, TicketMenuCommand.Priority,
            null,
            TicketMenuCommand.SetParent, TicketMenuCommand.CreateParent, TicketMenuCommand.Refresh,
            null,
            TicketMenuCommand.CopyForAgent, TicketMenuCommand.SendToAgent,
        }, plan.Select(e => e.Command));
    }

    [Fact]
    public void TicketsMate_ShowsNoAgentItems()
    {
        var plan = Build(Ticket(Someone), Me, ticketsOnly: true);
        Assert.DoesNotContain(plan, e => e.Command is TicketMenuCommand.CopyForAgent or TicketMenuCommand.SendToAgent);
        Assert.Equal(TicketMenuCommand.Refresh, plan[^1].Command);
    }

    [Fact]
    public void EveryItemHasItsOwnAutomationId()
    {
        var ids = Build(Ticket(Someone), Me).Where(e => !e.IsSeparator).Select(e => e.AutomationId).ToList();
        Assert.All(ids, id => Assert.False(string.IsNullOrEmpty(id)));
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void AssignToMe_NeedsAKnownPersonWhoIsNotAlreadyResponsible()
    {
        Assert.True(Entry(Build(Ticket(Someone), Me), TicketMenuCommand.AssignToMe).IsEnabled);
        Assert.True(Entry(Build(Ticket(), Me), TicketMenuCommand.AssignToMe).IsEnabled);
        Assert.False(Entry(Build(Ticket(Me), Me), TicketMenuCommand.AssignToMe).IsEnabled);
        Assert.False(Entry(Build(Ticket(Someone), me: null), TicketMenuCommand.AssignToMe).IsEnabled);
        Assert.False(Entry(Build(Ticket(Someone), Guid.Empty), TicketMenuCommand.AssignToMe).IsEnabled);
    }

    [Fact]
    public void Unassign_NeedsSomebodyResponsible()
    {
        Assert.True(Entry(Build(Ticket(Someone), Me), TicketMenuCommand.Unassign).IsEnabled);
        Assert.False(Entry(Build(Ticket(), Me), TicketMenuCommand.Unassign).IsEnabled);
        Assert.False(Entry(Build(Ticket(Guid.Empty), Me), TicketMenuCommand.Unassign).IsEnabled);
    }

    [Fact]
    public void StatusAndPriority_CheckTheTicketsCurrentValue()
    {
        var plan = Build(Ticket(Someone), Me);
        var statuses = Entry(plan, TicketMenuCommand.Status).Choices!;
        Assert.Equal(new[] { "New", "In Process", "Resolved" }, statuses.Select(c => c.Name));
        Assert.Equal(2, Assert.Single(statuses, c => c.IsCurrent).Id);
        var priorities = Entry(plan, TicketMenuCommand.Priority).Choices!;
        Assert.Equal(20, Assert.Single(priorities, c => c.IsCurrent).Id);
    }

    [Fact]
    public void Choices_KeepTheTicketsValueWhenTheListLacksIt()
    {
        var choices = TicketMenuPlan.Choices(new Dictionary<int, string> { [1] = "New" }, 9, "Waiting on Vendor");
        Assert.Equal(new[] { "New", "Waiting on Vendor" }, choices.Select(c => c.Name));
        Assert.True(choices.Single(c => c.Id == 9).IsCurrent);
    }

    [Fact]
    public void Choices_RunInWorkflowAndUrgencyOrder()
    {
        var statuses = TicketMenuPlan.Choices(
            new Dictionary<int, string> { [1] = "Closed", [2] = "In Process", [3] = "New", [4] = "On Hold", [5] = "Triage" },
            null, null, TicketMenuPlan.StatusOrder);
        Assert.Equal(new[] { "New", "In Process", "On Hold", "Closed", "Triage" }, statuses.Select(c => c.Name));

        var priorities = TicketMenuPlan.Choices(
            new Dictionary<int, string> { [1] = "Emergency", [2] = "High", [3] = "Low", [4] = "Medium" },
            null, null, TicketMenuPlan.PriorityOrder);
        Assert.Equal(new[] { "Low", "Medium", "High", "Emergency" }, priorities.Select(c => c.Name));
    }

    [Fact]
    public void Known_FillsInFromTheLoadedTickets()
    {
        var known = TicketMenuPlan.Known(null, new (int?, string?)[] { (1, "New"), (2, "Open"), (null, "None"), (3, null), (1, "Other") });
        Assert.Equal(new Dictionary<int, string> { [1] = "New", [2] = "Open" }, known);
        var merged = TicketMenuPlan.Known(new Dictionary<int, string> { [1] = "New (TDX)" }, new (int?, string?)[] { (1, "New"), (4, "Closed") });
        Assert.Equal("New (TDX)", merged[1]);
        Assert.Equal("Closed", merged[4]);
    }

    [Fact]
    public void WithoutAServiceOnlyTheReadItemsWork()
    {
        var plan = Build(Ticket(Someone), Me, canWrite: false);
        var writes = new[]
        {
            TicketMenuCommand.AssignToMe, TicketMenuCommand.Reassign, TicketMenuCommand.Unassign, TicketMenuCommand.Status,
            TicketMenuCommand.Priority, TicketMenuCommand.SetParent, TicketMenuCommand.CreateParent, TicketMenuCommand.Refresh,
        };
        Assert.All(writes, c => Assert.False(Entry(plan, c).IsEnabled));
        Assert.True(Entry(plan, TicketMenuCommand.CopyNumber).IsEnabled);
    }

    [Fact]
    public void WithoutAWebAddress_OpenAndCopyLinkAreDisabled()
    {
        var plan = Build(Ticket(Someone), Me, hasWebUrl: false);
        Assert.False(Entry(plan, TicketMenuCommand.OpenInWeb).IsEnabled);
        Assert.False(Entry(plan, TicketMenuCommand.CopyLink).IsEnabled);
        Assert.True(Entry(plan, TicketMenuCommand.CopyNumber).IsEnabled);
    }

    [Fact]
    public void AgentRowItems_AreNoneInTicketsMate_AndBothInFleetMate()
    {
        IReadOnlyList<AgentContext> None() => [];
        Assert.Empty(AgentContextMenu.Items(None, ticketsOnly: true));
        RunSta(() => Assert.Equal(2, AgentContextMenu.Items(None, ticketsOnly: false).Count()));
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
