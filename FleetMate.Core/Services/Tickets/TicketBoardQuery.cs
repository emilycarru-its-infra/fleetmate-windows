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
/// startup preload and the dashboard all load the same set the macOS app does.
/// Three searches, merged by ticket ID:
///
/// 1. Tickets created in the date window, MaxResults 5000, in the configured
///    responsible group only when one is set.
/// 2. Every open ticket (status classes New, In Process, On Hold, Requested)
///    in that group, with no date window, MaxResults 5000.
/// 3. The signed-in operator's own open tickets in any group or none, with no
///    date window, MaxResults 1000.
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

    /// <summary>
    /// TDX status classes for open work: New, In Process, On Hold, Requested.
    /// Same list the macOS board asks for.
    /// </summary>
    public static readonly IReadOnlyList<int> OpenStatusClassIds = new[] { 1, 2, 5, 6 };

    /// <summary>Ceiling for the operator's own open tickets, as on macOS.</summary>
    public const int MineMaxResults = 1000;

    /// <summary>Tickets created in the window, in the configured group if any.</summary>
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
    /// Every open ticket in the configured group (or everywhere, with no
    /// group), whenever it was created. Old open tickets sit outside any date
    /// window, and missing them made people look as if they had no work.
    /// </summary>
    public static TicketSearchRequest OpenSearch(TdxConfig config)
    {
        var search = new TicketSearchRequest
        {
            StatusClassIds = OpenStatusClassIds.ToList(),
            MaxResults = MaxResults,
        };
        if (config.ResponsibleGroupId > 0)
            search.ResponsibleGroupIds = new List<int> { config.ResponsibleGroupId };
        return search;
    }

    /// <summary>
    /// The operator's own open tickets in any group, or none, with no date
    /// window — so a ticket logged by hand and never routed to the group
    /// still shows up for the person working it. Null when nobody is signed in.
    /// </summary>
    public static TicketSearchRequest? MineSearch(Guid? me)
    {
        if (me is null || me == Guid.Empty) return null;
        return new TicketSearchRequest
        {
            StatusClassIds = OpenStatusClassIds.ToList(),
            ResponsibleUids = new List<Guid> { me.Value },
            MaxResults = MineMaxResults,
        };
    }

    /// <summary>Merge result sets by ticket ID, first occurrence wins.</summary>
    public static List<TdxTicket> Merge(params IEnumerable<TdxTicket>?[] sets)
    {
        var result = new List<TdxTicket>();
        var seen = new HashSet<int>();
        foreach (var set in sets)
            foreach (var t in set ?? Enumerable.Empty<TdxTicket>())
                if (seen.Add(t.Id)) result.Add(t);
        return result;
    }

    /// <summary>Run the three searches and merge them.</summary>
    public static async Task<List<TdxTicket>> LoadAsync(
        TdxService service, TdxConfig config, TicketDateRangePreset preset, DateTime? now = null)
    {
        var (from, to) = Range(preset, now ?? DateTime.Now);

        // Sequential on purpose: TdxService sets its bearer on the shared
        // client's default headers per call, which must not race a request.
        var dated = await service.SearchTicketsAsync(BoardSearch(config, from, to), MaxResults);
        var open = await service.SearchTicketsAsync(OpenSearch(config), MaxResults);

        List<TdxTicket>? mine = null;
        var mineSearch = MineSearch((await service.GetMeAsync())?.Uid);
        if (mineSearch != null) mine = await service.SearchTicketsAsync(mineSearch, MineMaxResults);

        return Merge(dated, open, mine);
    }
}
