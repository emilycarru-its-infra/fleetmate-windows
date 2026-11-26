using FleetMate.Core.Development;

namespace FleetMate.Core.Models.Tickets;

/// <summary>
/// The Tickets widgets as the shared strip's <see cref="GraphCard"/>s: the six
/// figures, then By Status as a donut and the other four breakdowns as bars.
/// A click comes back as a <see cref="GraphFilter"/>; <see cref="Apply"/> turns
/// it into the same filter change the WPF widgets make.
/// </summary>
public static class TicketGraphCards
{
    /// <summary>The filter category a figure's click carries; its value is the figure's title.</summary>
    public const string Figure = "TicketFigure";

    public static List<GraphCard> For(IReadOnlyList<TicketWidgetTile> tiles, IReadOnlyList<TicketWidgetChart> charts, bool loading)
    {
        var waiting = loading && tiles.Count > 0 && tiles[0].Value == 0;
        // Two cards of three figures rather than one of six: a narrow strip
        // stacks a card's figures, and three keep a row short enough to show whole.
        GraphCard Figures(string title, IEnumerable<TicketWidgetTile> group) => new(GraphCardKind.Kpis, title)
        {
            Loading = waiting,
            Kpis = group.Select(t => new GraphKpi(
                t.IsOn ? $"{t.Title} (on)" : t.Title,
                t.Value.ToString("N0"),
                Tone(t.Tone),
                waiting,
                new GraphFilter(Figure, t.Title))).ToList(),
        };
        var cards = new List<GraphCard>
        {
            Figures("Tickets", tiles.Take(3)),
            Figures("Needs Attention", tiles.Skip(3)),
        };
        foreach (var chart in charts)
        {
            var body = chart.Counts.Count > 0 ? GraphCardBody.Chart : waiting ? GraphCardBody.Loading : GraphCardBody.Empty;
            cards.Add(new GraphCard(chart.Category == TicketFilterCategory.Status ? GraphCardKind.Donut : GraphCardKind.Bars, chart.Title)
            {
                Loading = waiting,
                Body = body,
                Message = "No tickets match",
                Bars = chart.Counts.Take(TicketStats.TopCount).Select(c => new GraphBar(
                    c.Label,
                    c.Value,
                    Tone(TicketTones.For(chart.Category, c.Label)),
                    new GraphFilter(chart.Category.ToString(), c.Label),
                    $"{chart.Title}, {c.Label}: {c.Value}")).ToList(),
            });
        }
        return cards;
    }

    /// <summary>
    /// Act on a click: a figure runs its own toggle; a breakdown value toggles
    /// that value in its category. False when the filter is not a Tickets one.
    /// </summary>
    public static bool Apply(GraphFilter filter, IReadOnlyList<TicketWidgetTile> tiles, TicketFilters filters)
    {
        if (filter.Category == Figure)
        {
            if (tiles.FirstOrDefault(t => t.Title == filter.Value) is not { } tile) return false;
            tile.Action();
            return true;
        }
        if (!Enum.TryParse<TicketFilterCategory>(filter.Category, out var category)) return false;
        filters.Toggle(category, filter.Value);
        return true;
    }

    public static ChartTone Tone(TicketTone tone) => tone switch
    {
        TicketTone.Blue => ChartTone.Blue,
        TicketTone.Green => ChartTone.Green,
        TicketTone.Orange => ChartTone.Orange,
        TicketTone.Yellow => ChartTone.Yellow,
        TicketTone.Purple => ChartTone.Purple,
        TicketTone.Teal => ChartTone.Teal,
        TicketTone.Indigo => ChartTone.Indigo,
        _ => ChartTone.Gray,
    };
}
