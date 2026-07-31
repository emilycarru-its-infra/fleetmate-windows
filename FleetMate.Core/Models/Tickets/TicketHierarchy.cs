namespace FleetMate.Core.Models.Tickets;

/// <summary>
/// A comment TeamDynamix did not accept.
///
/// Exists so the failure cannot be mistaken for a slow one: reporting a rejected
/// comment as a <c>false</c> return looked identical to a successful post the
/// feed had not caught up with, and the operator retyped work that was gone.
/// </summary>
public sealed class TdxCommentException : Exception
{
    public int TicketId { get; }

    public TdxCommentException(int ticketId, string detail, Exception? inner = null)
        : base($"Could not add a comment to ticket {ticketId} — {detail}", inner)
    {
        TicketId = ticketId;
    }
}

/// <summary>One ticket and its nested children.</summary>
public sealed class TicketNode
{
    public required TdxTicket Ticket { get; init; }
    public List<TicketNode> Children { get; init; } = new();

    public int Id => Ticket.Id;

    /// <summary>Every descendant, not just direct children.</summary>
    public int DescendantCount => Children.Count + Children.Sum(c => c.DescendantCount);
}

/// <summary>One rendered row: a ticket, its indent level, and its fold state.</summary>
public sealed class TicketRow
{
    public required TdxTicket Ticket { get; init; }
    public int Depth { get; init; }
    public int ChildCount { get; init; }
    public bool IsExpanded { get; init; }

    public int Id => Ticket.Id;
    public bool HasChildren => ChildCount > 0;
}

/// <summary>
/// Builds the parent/child outline the ticket list and board both render.
///
/// Kept as pure functions over a ticket collection so the list and the board can
/// share one collapse state and stay in step — the two views disagreeing about
/// what is folded is the bug this shape prevents.
/// </summary>
public static class TicketHierarchy
{
    /// <summary>
    /// Nest children under their parents, preserving input order at every level.
    ///
    /// A ticket whose parent is not in the collection stays visible as a root.
    /// The parent is usually just filtered out of the current view — closed, out
    /// of range, in another group — and dropping its children would hide real
    /// work rather than tidy the display.
    /// </summary>
    public static List<TicketNode> Build(IEnumerable<TdxTicket> tickets)
    {
        var ordered = tickets.ToList();

        // Last one wins on duplicate IDs, which mirrors how the views dedupe.
        var nodes = new Dictionary<int, TicketNode>();
        foreach (var ticket in ordered) nodes[ticket.Id] = new TicketNode { Ticket = ticket };

        var roots = new List<TicketNode>();

        foreach (var ticket in ordered)
        {
            var node = nodes[ticket.Id];
            var parentId = ParentTicketId(ticket);

            if (parentId is { } pid && pid != ticket.Id && nodes.TryGetValue(pid, out var parent)
                && !CreatesCycle(nodes, ticket.Id, pid))
            {
                parent.Children.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        return roots;
    }

    /// <summary>
    /// Walk the parent chain to see whether attaching <paramref name="childId"/>
    /// under <paramref name="parentId"/> would close a loop.
    ///
    /// TDX has no constraint preventing a ticket being its own ancestor, and a
    /// cycle here would recurse until the stack gave out — a hang rather than a
    /// visible error, which is the worst way for bad data to surface.
    /// </summary>
    private static bool CreatesCycle(
        IReadOnlyDictionary<int, TicketNode> nodes, int childId, int parentId)
    {
        var seen = new HashSet<int>();
        int? cursor = parentId;

        while (cursor is { } id && seen.Add(id))
        {
            if (id == childId) return true;
            cursor = nodes.TryGetValue(id, out var node) ? ParentTicketId(node.Ticket) : null;
        }

        return false;
    }

    /// <summary>
    /// The parent ticket ID, or null when there is none.
    ///
    /// TDX sends 0 rather than null for "no parent", so a naive null check leaves
    /// every root claiming to be a child of ticket zero.
    /// </summary>
    public static int? ParentTicketId(TdxTicket ticket) =>
        ticket.ParentId is { } id && id > 0 ? id : null;

    /// <summary>
    /// Flatten the tree into display rows, skipping the subtrees of collapsed
    /// parents. Expanded is the default: a queue that opens folded hides the
    /// work it exists to show.
    /// </summary>
    public static List<TicketRow> Flatten(IEnumerable<TicketNode> roots, ISet<int> collapsed)
    {
        var rows = new List<TicketRow>();
        Walk(roots, 0);
        return rows;

        void Walk(IEnumerable<TicketNode> nodes, int depth)
        {
            foreach (var node in nodes)
            {
                var expanded = !collapsed.Contains(node.Id);

                rows.Add(new TicketRow
                {
                    Ticket = node.Ticket,
                    Depth = depth,
                    ChildCount = node.Children.Count,
                    IsExpanded = expanded,
                });

                if (expanded && node.Children.Count > 0) Walk(node.Children, depth + 1);
            }
        }
    }

    /// <summary>Every parent ID in the tree — what "Collapse All" folds.</summary>
    public static HashSet<int> AllParentIds(IEnumerable<TicketNode> roots)
    {
        var ids = new HashSet<int>();
        Walk(roots);
        return ids;

        void Walk(IEnumerable<TicketNode> nodes)
        {
            foreach (var node in nodes.Where(n => n.Children.Count > 0))
            {
                ids.Add(node.Id);
                Walk(node.Children);
            }
        }
    }
}

/// <summary>
/// Age and activity, read in days.
///
/// A queue is read in days: "1 yr" tells you a ticket is old, "412d" tells you
/// how bad it is. Both are shown, and they answer different questions — an old
/// ticket touched today is healthy, a young one untouched for three weeks is not.
/// </summary>
public static class TdxTicketAgeExtensions
{
    /// <summary>
    /// Days since the ticket opened. Prefers the server's own count, which
    /// accounts for the tenant's timezone and business calendar in ways a local
    /// subtraction does not.
    /// </summary>
    public static int? AgeInDays(this TdxTicket ticket, DateTime? now = null)
    {
        if (ticket.DaysOld > 0) return ticket.DaysOld;
        if (ticket.CreatedDate == default) return null;

        return (int)((now ?? DateTime.UtcNow) - ticket.CreatedDate).TotalDays;
    }

    public static string AgeLabel(this TdxTicket ticket, DateTime? now = null) =>
        ticket.AgeInDays(now) is { } days ? $"{days}d" : "-";

    /// <summary>
    /// Days since the ticket was last touched, falling back to its age when it
    /// has never been modified.
    /// </summary>
    public static int? DaysSinceLastActivity(this TdxTicket ticket, DateTime? now = null)
    {
        if (ticket.ModifiedDate is { } modified && modified != default)
            return (int)((now ?? DateTime.UtcNow) - modified).TotalDays;

        return ticket.AgeInDays(now);
    }

    public static string LastActivityLabel(this TdxTicket ticket, DateTime? now = null) =>
        ticket.DaysSinceLastActivity(now) is { } days ? $"{days}d" : "-";
}
