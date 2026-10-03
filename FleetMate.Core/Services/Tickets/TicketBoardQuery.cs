using FleetMate.Core.Models.Tickets;

namespace FleetMate.Core.Services.Tickets;

/// <summary>
/// The date window the ticket board loads. Mirrors the macOS presets: the
/// current academic term is the default, with the calendar shortcuts beside it.
/// </summary>
public enum TicketDateRangePreset
{
    CurrentTerm,
    PreviousTerm,
    ThisMonth,
    LastMonth,
    ThisWeek,
    Today,
}

/// <summary>
/// Builds and runs the query behind the Tickets board, so the board, the
/// startup preload and the dashboard all load the same set the macOS app does:
///
/// 1. One ticket search over a created-date range, MaxResults 5000, filtered
///    by the configured responsible group only when one is set.
/// 2. When a group <em>is</em> set, a second search for tickets whose
///    responsible is the signed-in operator, keeping only those with no
///    group. A ticket someone logged by hand and never routed has no group,
///    so the group filter alone hides it from the person working it.
///
/// The two results are merged by ticket ID.
/// </summary>
public static class TicketBoardQuery
{
    /// <summary>Same ceiling as the macOS board.</summary>
    public const int MaxResults = 5000;

    public static string Label(TicketDateRangePreset preset, DateTime now) => preset switch
    {
        TicketDateRangePreset.CurrentTerm => TermLabel(now),
        TicketDateRangePreset.PreviousTerm => TermLabel(Range(TicketDateRangePreset.CurrentTerm, now).From.AddDays(-1)),
        TicketDateRangePreset.ThisMonth => "This Month",
        TicketDateRangePreset.LastMonth => "Last Month",
        TicketDateRangePreset.ThisWeek => "This Week",
        TicketDateRangePreset.Today => "Today",
        _ => preset.ToString(),
    };

    /// <summary>
    /// Local-time [From, To) window for a preset. Terms follow the macOS
    /// definition: Spring Jan–Apr, Summer May–Aug, Fall Sep–Dec.
    /// </summary>
    public static (DateTime From, DateTime To) Range(TicketDateRangePreset preset, DateTime now)
    {
        var today = now.Date;
        switch (preset)
        {
            case TicketDateRangePreset.Today:
                return (today, today.AddDays(1));
            case TicketDateRangePreset.ThisWeek:
            {
                var first = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
                var start = today.AddDays(-(((int)today.DayOfWeek - (int)first + 7) % 7));
                return (start, start.AddDays(7));
            }
            case TicketDateRangePreset.ThisMonth:
            {
                var start = new DateTime(today.Year, today.Month, 1);
                return (start, start.AddMonths(1));
            }
            case TicketDateRangePreset.LastMonth:
            {
                var thisMonth = new DateTime(today.Year, today.Month, 1);
                return (thisMonth.AddMonths(-1), thisMonth);
            }
            case TicketDateRangePreset.PreviousTerm:
                return TermRange(Range(TicketDateRangePreset.CurrentTerm, now).From.AddDays(-1));
            default:
                return TermRange(today);
        }
    }

    private static (DateTime From, DateTime To) TermRange(DateTime day)
    {
        var y = day.Year;
        return day.Month switch
        {
            <= 4 => (new DateTime(y, 1, 1), new DateTime(y, 5, 1)),
            <= 8 => (new DateTime(y, 5, 1), new DateTime(y, 9, 1)),
            // Matches macOS, which ends Fall at Dec 31 23:59:59 rather than Jan 1.
            _ => (new DateTime(y, 9, 1), new DateTime(y, 12, 31, 23, 59, 59)),
        };
    }

    private static string TermLabel(DateTime day) => day.Month switch
    {
        <= 4 => $"Spring {day.Year}",
        <= 8 => $"Summer {day.Year}",
        _ => $"Fall {day.Year}",
    };

    /// <summary>The main board search.</summary>
    public static TicketSearchRequest BoardSearch(TdxConfig config, DateTime from, DateTime to)
    {
        var search = new TicketSearchRequest
        {
            CreatedDateFrom = DateTime.SpecifyKind(from, DateTimeKind.Local),
            CreatedDateTo = DateTime.SpecifyKind(to, DateTimeKind.Local),
            MaxResults = MaxResults,
        };
        if (config.ResponsibleGroupId > 0)
            search.ResponsibleGroupIds = new List<int> { config.ResponsibleGroupId };
        return search;
    }

    /// <summary>
    /// The operator's own tickets over the same window, or null when it is not
    /// needed (no group filter, so the main search already has them) or there
    /// is no signed-in person to ask about.
    /// </summary>
    public static TicketSearchRequest? MineSearch(TdxConfig config, Guid? me, DateTime from, DateTime to)
    {
        if (config.ResponsibleGroupId <= 0 || me is null || me == Guid.Empty) return null;
        return new TicketSearchRequest
        {
            CreatedDateFrom = DateTime.SpecifyKind(from, DateTimeKind.Local),
            CreatedDateTo = DateTime.SpecifyKind(to, DateTimeKind.Local),
            ResponsibleUids = new List<Guid> { me.Value },
            MaxResults = MaxResults,
        };
    }

    /// <summary>A ticket with no responsible group.</summary>
    public static bool IsGroupless(TdxTicket t) =>
        (t.ResponsibleGroupId ?? 0) <= 0 && string.IsNullOrWhiteSpace(t.ResponsibleGroupName);

    /// <summary>
    /// The board set: every primary ticket, plus the operator's groupless
    /// tickets the group filter left out. TDX search cannot ask for "no group"
    /// directly, so the second result is filtered here.
    /// </summary>
    public static List<TdxTicket> Merge(IEnumerable<TdxTicket> primary, IEnumerable<TdxTicket>? mine)
    {
        var result = new List<TdxTicket>();
        var seen = new HashSet<int>();
        foreach (var t in primary)
            if (seen.Add(t.Id)) result.Add(t);
        foreach (var t in mine ?? Enumerable.Empty<TdxTicket>())
            if (IsGroupless(t) && seen.Add(t.Id)) result.Add(t);
        return result;
    }

    /// <summary>Run both searches and merge them.</summary>
    public static async Task<List<TdxTicket>> LoadAsync(
        TdxService service, TdxConfig config, TicketDateRangePreset preset, DateTime? now = null)
    {
        var (from, to) = Range(preset, now ?? DateTime.Now);
        // Sequential on purpose: TdxService sets its bearer on the shared
        // client's default headers per call, which must not race a request.
        var primary = await service.SearchTicketsAsync(BoardSearch(config, from, to), MaxResults);

        List<TdxTicket>? mine = null;
        if (config.ResponsibleGroupId > 0)
        {
            var me = await service.GetMeAsync();
            var mineSearch = MineSearch(config, me?.Uid, from, to);
            if (mineSearch != null) mine = await service.SearchTicketsAsync(mineSearch, MaxResults);
        }

        return Merge(primary, mine);
    }
}
