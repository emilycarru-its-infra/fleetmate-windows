namespace FleetMate.Core.Models.Tickets;

/// <summary>One board column: its key (also the drop target) and its tickets.</summary>
public sealed record TicketBoardColumn(string Key, IReadOnlyList<TdxTicket> Tickets);

/// <summary>
/// A run of columns under an optional header. Responsible mode draws one block
/// per responsible group; every other mode is a single headerless block.
/// </summary>
public sealed record TicketBoardBlock(string? Header, IReadOnlyList<TicketBoardColumn> Columns)
{
    public int Count => Columns.Sum(c => c.Tickets.Count);
}

/// <summary>
/// Column layout for the Tickets board, matching the macOS board: a groupless
/// ticket always has somewhere to go, so it can never fall off the board.
/// </summary>
public static class TicketBoardLayout
{
    public const string NoGroup = "No Group";
    public const string Unassigned = "Unassigned";

    /// <summary>
    /// Responsible mode: people arranged inside the group they belong to —
    /// group blocks alphabetical, people alphabetical inside, Unassigned last
    /// within each block, and groupless tickets in a trailing "No Group" block.
    /// </summary>
    public static List<TicketBoardBlock> ResponsibleBlocks(IEnumerable<TdxTicket> tickets)
    {
        return tickets
            .GroupBy(GroupKey)
            .OrderBy(g => g.Key == NoGroup ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TicketBoardBlock(g.Key, Columns(g, ResponsibleKey, Unassigned)))
            .ToList();
    }

    /// <summary>
    /// Columns keyed by <paramref name="key"/>, alphabetical, with
    /// <paramref name="fallback"/> (the empty bucket) last.
    /// </summary>
    public static List<TicketBoardColumn> Columns(
        IEnumerable<TdxTicket> tickets, Func<TdxTicket, string> key, string fallback)
    {
        return tickets
            .GroupBy(key)
            .OrderBy(g => g.Key == fallback ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TicketBoardColumn(g.Key, g.ToList()))
            .ToList();
    }

    public static string GroupKey(TdxTicket t) =>
        string.IsNullOrWhiteSpace(t.ResponsibleGroupName) ? NoGroup : t.ResponsibleGroupName!;

    public static string ResponsibleKey(TdxTicket t) =>
        string.IsNullOrWhiteSpace(t.ResponsibleFullName) ? Unassigned : t.ResponsibleFullName!;

    public static string StatusKey(TdxTicket t) => t.StatusName ?? "Unknown";

    public static string PriorityKey(TdxTicket t) => t.PriorityName ?? "No Priority";

    /// <summary>The column a ticket sits in when the board is grouped by <paramref name="groupBy"/>.</summary>
    public static string ColumnKey(TdxTicket t, string groupBy) => groupBy switch
    {
        "Status" => StatusKey(t),
        "Priority" => PriorityKey(t),
        "Group" => GroupKey(t),
        _ => ResponsibleKey(t),
    };

    /// <summary>
    /// Whether dropping <paramref name="t"/> on <paramref name="columnKey"/>
    /// would change anything. A card dropped back on its own column is not an
    /// edit, and must never reach the service desk as one.
    /// </summary>
    public static bool IsMove(TdxTicket t, string groupBy, string columnKey) =>
        !string.Equals(ColumnKey(t, groupBy), columnKey, StringComparison.Ordinal);

    /// <summary>The field a drop changes, in words, for the confirmation prompt.</summary>
    public static string FieldLabel(string groupBy) => groupBy switch
    {
        "Status" => "status",
        "Priority" => "priority",
        "Group" => "responsible group",
        _ => "responsible person",
    };

    /// <summary>The board's column dimensions, as the Mac's BoardGroupBy offers them; Responsible is the default.</summary>
    public static readonly IReadOnlyList<string> GroupByOptions = new[] { "Status", "Responsible", "Priority", "Group" };

    public const string DefaultGroupBy = "Responsible";

    /// <summary>
    /// The board's blocks for <paramref name="groupBy"/>: Status and Priority
    /// in their workflow order, Group alphabetical with No Group last, and
    /// Responsible as people inside their group's block. Every mode but
    /// Responsible is one headerless block.
    /// </summary>
    public static List<TicketBoardBlock> Blocks(IEnumerable<TdxTicket> tickets, string groupBy)
    {
        var list = tickets as IReadOnlyCollection<TdxTicket> ?? tickets.ToList();
        return groupBy switch
        {
            "Status" => new() { new(null, list.GroupBy(StatusKey).OrderBy(g => StatusOrder(g.Key))
                .Select(g => new TicketBoardColumn(g.Key, g.ToList())).ToList()) },
            "Priority" => new() { new(null, list.GroupBy(PriorityKey).OrderBy(g => PriorityOrder(g.Key))
                .Select(g => new TicketBoardColumn(g.Key, g.ToList())).ToList()) },
            "Group" => new() { new(null, Columns(list, GroupKey, NoGroup)) },
            _ => ResponsibleBlocks(list),
        };
    }

    /// <summary>Where a status sits on the board, left to right: new work first, finished work last.</summary>
    public static int StatusOrder(string status) => status.ToLowerInvariant() switch
    {
        "new" => 0,
        "open" => 1,
        "in progress" or "in process" => 2,
        "awaiting response" => 3,
        "on hold" => 4,
        "resolved" => 5,
        "closed" => 6,
        "cancelled" or "canceled" => 7,
        _ => 5,
    };

    /// <summary>Where a priority sits on the board, most urgent first.</summary>
    public static int PriorityOrder(string priority) => priority.ToLowerInvariant() switch
    {
        "emergency" => 0,
        "high" => 1,
        "medium" => 2,
        "low" => 3,
        _ => 4,
    };

    /// <summary>
    /// The update a drop on <paramref name="columnKey"/> sends, as the macOS
    /// board does: StatusID, PriorityID, ResponsibleGroupID or ResponsibleUid,
    /// with the id read from a loaded ticket that already holds the value. Null
    /// when nothing would change or no loaded ticket carries the value's id.
    /// </summary>
    public static Dictionary<string, object?>? DropUpdates(
        IEnumerable<TdxTicket> loaded, TdxTicket ticket, string groupBy, string columnKey)
    {
        if (!IsMove(ticket, groupBy, columnKey)) return null;
        var all = loaded as IReadOnlyCollection<TdxTicket> ?? loaded.ToList();
        switch (groupBy)
        {
            case "Status":
                var statusId = all.FirstOrDefault(t => t.StatusName == columnKey)?.StatusId;
                return statusId is null or 0 ? null : new() { ["StatusID"] = statusId };
            case "Priority":
                var priorityId = all.FirstOrDefault(t => t.PriorityName == columnKey)?.PriorityId;
                return priorityId == null ? null : new() { ["PriorityID"] = priorityId };
            case "Group":
                if (columnKey == NoGroup) return new() { ["ResponsibleGroupID"] = 0 };
                var groupId = all.FirstOrDefault(t => t.ResponsibleGroupName == columnKey)?.ResponsibleGroupId;
                return groupId == null ? null : new() { ["ResponsibleGroupID"] = groupId };
            default:
                if (columnKey == Unassigned) return new() { ["ResponsibleUid"] = "" };
                var uid = all.FirstOrDefault(t => t.ResponsibleFullName == columnKey)?.ResponsibleUid;
                return uid == null ? null : new() { ["ResponsibleUid"] = uid };
        }
    }

    /// <summary>The question a drop asks before it writes to the shared service desk.</summary>
    public static string DropConfirmation(TdxTicket ticket, string groupBy, string columnKey) =>
        $"Change the {FieldLabel(groupBy)} of #{ticket.Id} from {ColumnKey(ticket, groupBy)} to {columnKey}?\n\n{ticket.Title}";
}
