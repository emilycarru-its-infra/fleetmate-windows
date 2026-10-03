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
}
