using FleetMate.Core.Models.Tickets;

namespace FleetMate.GUI.Views.Tickets;

/// <summary>What a ticket menu item does when chosen.</summary>
public enum TicketMenuCommand
{
    OpenInWeb,
    CopyNumber,
    CopyLink,
    CopyTitle,
    AssignToMe,
    Reassign,
    Unassign,
    Status,
    Priority,
    SetParent,
    CreateParent,
    RestartWorkflow,
    ChangeWorkflow,
    Refresh,
    CopyForAgent,
    SendToAgent,
}

/// <summary>One choice in the Status or Priority submenu.</summary>
public sealed record TicketMenuChoice(int Id, string Name, bool IsCurrent);

/// <summary>
/// One line of a ticket's right-click menu: a command, or a separator when
/// <see cref="Command"/> is null. Status and Priority carry their choices.
/// </summary>
public sealed record TicketMenuEntry(
    TicketMenuCommand? Command,
    string Header,
    string AutomationId,
    bool IsEnabled = true,
    IReadOnlyList<TicketMenuChoice>? Choices = null)
{
    public bool IsSeparator => Command == null;

    public static readonly TicketMenuEntry Separator = new(null, "", "");
}

/// <summary>
/// The right-click menu of a ticket row or board card, as data. The page turns
/// it into WPF menu items; keeping the composition here lets the order, the
/// enabled states and the edition rules be tested without a window.
/// </summary>
public static class TicketMenuPlan
{
    /// <param name="ticket">The ticket under the pointer, or the focused row's.</param>
    /// <param name="myUid">The signed-in person's TDX uid; null while unknown.</param>
    /// <param name="ticketsOnly">TicketsMate: no agent terminal, so no agent items.</param>
    /// <param name="canWrite">False when there is no TDX service to save through.</param>
    /// <param name="hasWebUrl">False when no TeamDynamix web address is configured.</param>
    /// <param name="statuses">Known statuses by id.</param>
    /// <param name="priorities">Known priorities by id.</param>
    public static IReadOnlyList<TicketMenuEntry> Build(
        TdxTicket ticket,
        Guid? myUid,
        bool ticketsOnly,
        bool canWrite,
        bool hasWebUrl,
        IReadOnlyDictionary<int, string> statuses,
        IReadOnlyDictionary<int, string> priorities)
    {
        var statusChoices = Choices(statuses, ticket.StatusId > 0 ? ticket.StatusId : null, ticket.StatusName, StatusOrder);
        var priorityChoices = Choices(priorities, ticket.PriorityId, ticket.PriorityName, PriorityOrder);

        var entries = new List<TicketMenuEntry>
        {
            new(TicketMenuCommand.OpenInWeb, "Open in TeamDynamix", "TicketMenuOpenInWeb", hasWebUrl),
            new(TicketMenuCommand.CopyNumber, "Copy Ticket Number", "TicketMenuCopyNumber"),
            new(TicketMenuCommand.CopyLink, "Copy Link", "TicketMenuCopyLink", hasWebUrl),
            new(TicketMenuCommand.CopyTitle, "Copy Title", "TicketMenuCopyTitle", !string.IsNullOrEmpty(ticket.Title)),
            TicketMenuEntry.Separator,
            new(TicketMenuCommand.AssignToMe, "Assign to Me", "TicketMenuAssignToMe", canWrite && CanAssignToMe(ticket, myUid)),
            new(TicketMenuCommand.Reassign, "Reassign…", "TicketMenuReassign", canWrite),
            new(TicketMenuCommand.Unassign, "Unassign", "TicketMenuUnassign", canWrite && CanUnassign(ticket)),
            new(TicketMenuCommand.Status, "Status", "TicketMenuStatus", canWrite && statusChoices.Count > 0, statusChoices),
            new(TicketMenuCommand.Priority, "Priority", "TicketMenuPriority", canWrite && priorityChoices.Count > 0, priorityChoices),
            TicketMenuEntry.Separator,
            new(TicketMenuCommand.SetParent, "Set Parent…", "TicketMenuSetParent", canWrite),
            new(TicketMenuCommand.CreateParent, "Create Parent…", "TicketMenuCreateParent", canWrite),
            TicketMenuEntry.Separator,
            // Whether the ticket has a workflow, and which, is only known once
            // it is open; its Workflow section then decides what is possible.
            new(TicketMenuCommand.RestartWorkflow, "Restart Workflow…", "TicketMenuRestartWorkflow", canWrite),
            new(TicketMenuCommand.ChangeWorkflow, "Change Workflow…", "TicketMenuChangeWorkflow", canWrite),
            new(TicketMenuCommand.Refresh, "Refresh", "TicketMenuRefresh", canWrite),
        };

        if (!ticketsOnly)
        {
            entries.Add(TicketMenuEntry.Separator);
            entries.Add(new(TicketMenuCommand.CopyForAgent, "Copy for Agent", "CopyForAgentMenuItem"));
            entries.Add(new(TicketMenuCommand.SendToAgent, "Send to Agent", "SendToAgentMenuItem"));
        }

        return entries;
    }

    /// <summary>Not when the signed-in person is unknown, or already responsible.</summary>
    public static bool CanAssignToMe(TdxTicket ticket, Guid? myUid) =>
        myUid is { } me && me != Guid.Empty && ticket.ResponsibleUid != me;

    /// <summary>Only when somebody is responsible.</summary>
    public static bool CanUnassign(TdxTicket ticket) =>
        ticket.ResponsibleUid is { } uid && uid != Guid.Empty;

    /// <summary>Statuses in the order a ticket moves through them, as the board's columns run.</summary>
    public static readonly IReadOnlyList<string> StatusOrder =
        ["new", "open", "in pro", "on hold", "pending", "waiting", "resolved", "completed", "closed", "cancel"];

    /// <summary>Priorities from least to most urgent, as the board's columns run.</summary>
    public static readonly IReadOnlyList<string> PriorityOrder =
        ["low", "medium", "high", "urgent", "critical", "emergency"];

    /// <summary>
    /// The known values in <paramref name="order"/> (the first fragment each
    /// name contains; names matching none go last, by name), the ticket's own
    /// checked. The ticket's value is kept even when the known list lacks it,
    /// so the submenu never hides where the ticket stands.
    /// </summary>
    public static IReadOnlyList<TicketMenuChoice> Choices(
        IReadOnlyDictionary<int, string> known, int? currentId, string? currentName,
        IReadOnlyList<string>? order = null)
    {
        var all = new Dictionary<int, string>(known.Where(kv => kv.Key > 0));
        if (currentId is int id && id > 0 && !all.ContainsKey(id))
            all[id] = string.IsNullOrWhiteSpace(currentName) ? $"#{id}" : currentName;
        return all
            .OrderBy(kv => Rank(kv.Value, order))
            .ThenBy(kv => kv.Value, StringComparer.CurrentCultureIgnoreCase)
            .Select(kv => new TicketMenuChoice(kv.Key, kv.Value, kv.Key == currentId))
            .ToList();
    }

    private static int Rank(string name, IReadOnlyList<string>? order)
    {
        if (order == null) return 0;
        for (var i = 0; i < order.Count; i++)
            if (name.Contains(order[i], StringComparison.OrdinalIgnoreCase)) return i;
        return order.Count;
    }

    /// <summary>
    /// Every status (or priority) the loaded tickets carry, by id, merged over
    /// what TeamDynamix listed. Tickets fill in while the TDX list is still loading.
    /// </summary>
    public static Dictionary<int, string> Known(
        IReadOnlyDictionary<int, string>? listed, IEnumerable<(int? Id, string? Name)> seen)
    {
        var known = listed == null ? new Dictionary<int, string>() : new Dictionary<int, string>(listed);
        foreach (var (id, name) in seen)
            if (id is int value && value > 0 && !string.IsNullOrWhiteSpace(name)) known.TryAdd(value, name);
        return known;
    }
}
