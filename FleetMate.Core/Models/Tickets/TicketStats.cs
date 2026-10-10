namespace FleetMate.Core.Models.Tickets;

/// <summary>
/// The figures behind the Tickets widgets, computed from whichever tickets the
/// list is showing, so every widget follows the current filters and search.
/// The same rules as the macOS app's TicketStats.
/// </summary>
public sealed class TicketStats
{
    /// <summary>One value and how many tickets carry it.</summary>
    public sealed record Count(string Label, int Value);

    /// <summary>Tickets older than this many days count as aging: the oldest age band.</summary>
    public const int AgingDays = 30;

    /// <summary>Responsible people and groups beyond this many are left off the bars.</summary>
    public const int TopCount = 6;

    public const string Unassigned = "Unassigned";
    public const string OnHoldStatus = "On Hold";

    /// <summary>Age bands, youngest first. The labels double as filter values.</summary>
    public static readonly IReadOnlyList<(string Label, int MinDays, int MaxDays)> AgeBuckets = new[]
    {
        ("Today", 0, 0),
        ("1–7 days", 1, 7),
        ("8–30 days", 8, 30),
        ("Over 30 days", 31, int.MaxValue),
    };

    /// <summary>The oldest band's label, which the Over 30 days figure filters on.</summary>
    public static string AgingBucket => AgeBuckets[^1].Label;

    private static readonly HashSet<string> ClosedStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "closed", "cancelled", "canceled", "resolved", "completed" };

    private static readonly Dictionary<string, int> PriorityOrder = new()
    {
        ["Low"] = 0, ["Medium"] = 1, ["High"] = 2, ["Emergency"] = 3,
    };

    public int Total { get; }
    public int Open { get; }
    public int OnHold { get; }
    public int UnassignedCount { get; }
    public int SlaViolated { get; }
    public int Aging { get; }
    public IReadOnlyList<Count> ByStatus { get; }
    public IReadOnlyList<Count> ByPriority { get; }
    public IReadOnlyList<Count> ByAge { get; }
    public IReadOnlyList<Count> ByResponsible { get; }
    public IReadOnlyList<Count> ByGroup { get; }

    public TicketStats(IEnumerable<TdxTicket> tickets, DateTime? now = null)
    {
        var all = tickets.ToList();
        var active = all.Where(t => !IsClosed(t)).ToList();

        Total = all.Count;
        OnHold = active.Count(t => t.IsOnHold);
        Open = active.Count - OnHold;
        UnassignedCount = active.Count(t => IsBlank(t.ResponsibleFullName));
        SlaViolated = all.Count(t => t.IsSlaViolated);
        Aging = active.Count(t => (t.AgeInDays(now) ?? 0) > AgingDays);

        ByStatus = Counts(all, t => t.StatusName ?? "None")
            .OrderByDescending(c => c.Value).ThenBy(c => c.Label, StringComparer.Ordinal).ToList();
        ByPriority = Counts(active, t => t.PriorityName ?? "None")
            .OrderBy(c => PriorityOrder.GetValueOrDefault(c.Label, 99)).ThenBy(c => c.Label, StringComparer.Ordinal).ToList();
        ByAge = AgeBuckets
            .Select(b => new Count(b.Label, active.Count(t => AgeBucket(t, now) == b.Label)))
            .Where(c => c.Value > 0)
            .ToList();
        ByResponsible = Top(Counts(active, t => IsBlank(t.ResponsibleFullName) ? Unassigned : t.ResponsibleFullName!));
        ByGroup = Top(Counts(active, t => IsBlank(t.ResponsibleGroupName) ? Unassigned : t.ResponsibleGroupName!));
    }

    /// <summary>The age band a ticket falls in.</summary>
    public static string AgeBucket(TdxTicket ticket, DateTime? now = null)
    {
        var days = Math.Max(ticket.AgeInDays(now) ?? 0, 0);
        foreach (var b in AgeBuckets)
        {
            if (days >= b.MinDays && days <= b.MaxDays) return b.Label;
        }
        return AgeBuckets[0].Label;
    }

    public static bool IsClosed(TdxTicket ticket) =>
        ticket.StatusName is { } s && ClosedStatuses.Contains(s);

    internal static bool IsBlank(string? s) => string.IsNullOrWhiteSpace(s);

    private static IEnumerable<Count> Counts(IEnumerable<TdxTicket> tickets, Func<TdxTicket, string> key) =>
        tickets.GroupBy(key).Select(g => new Count(g.Key, g.Count()));

    private static List<Count> Top(IEnumerable<Count> counts) =>
        counts.OrderByDescending(c => c.Value).ThenBy(c => c.Label, StringComparer.Ordinal).Take(TopCount).ToList();
}
