namespace FleetMate.Core.Models.Tickets;

/// <summary>
/// What narrows the Tickets list: closed tickets hidden or shown, Assigned to
/// Me, the filter selections and the search text. The list, the board and
/// the widgets all read the same options.
/// </summary>
public sealed record TicketListOptions(TicketFilters Filters, bool ShowClosed = false, TdxPerson? AssignedTo = null, string? Search = null);

/// <summary>
/// The Tickets list's selection, shared by the WPF and WinUI apps so both
/// show the same tickets for the same choices.
/// </summary>
public static class TicketListQuery
{
    /// <summary>
    /// The tickets the options leave, unsorted, with every filter applied
    /// except <paramref name="ignoring"/>'s selections. The widgets count
    /// each category this way, so its other values stay clickable.
    /// </summary>
    public static List<TdxTicket> Matching(IEnumerable<TdxTicket> tickets, TicketListOptions options,
        TicketFilterCategory? ignoring = null, DateTime? nowUtc = null)
    {
        var filtered = tickets;
        if (!options.ShowClosed) filtered = filtered.Where(t => !IsClosedOrCancelled(t));
        if (options.AssignedTo is { } me) filtered = filtered.Where(t => TicketFilters.IsAssignedTo(t, me));
        if (options.Filters.HasActiveFilters)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            filtered = filtered.Where(t => options.Filters.Matches(t, ignoring, now));
        }
        var search = options.Search?.Trim();
        if (!string.IsNullOrEmpty(search)) filtered = filtered.Where(t => MatchesSearch(t, search));
        return filtered.ToList();
    }

    /// <summary>Closed and cancelled tickets, which the list hides until Show Closed is on.</summary>
    public static bool IsClosedOrCancelled(TdxTicket ticket) =>
        ticket.StatusName?.ToLowerInvariant() is "closed" or "cancelled" or "canceled";

    /// <summary>The search matches the title, the requestor or the ticket number.</summary>
    public static bool MatchesSearch(TdxTicket ticket, string search) =>
        ticket.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) == true
        || ticket.RequestorName?.Contains(search, StringComparison.OrdinalIgnoreCase) == true
        || ticket.Id.ToString().Contains(search, StringComparison.Ordinal);

    /// <summary>"12 of 340": tickets listed, not rows, so a folded subtree never reads as missing work.</summary>
    public static string CountLabel(int listed, int loaded) => $"{listed} of {loaded}";
}
