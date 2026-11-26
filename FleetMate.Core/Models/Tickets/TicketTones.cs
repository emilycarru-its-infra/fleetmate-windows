namespace FleetMate.Core.Models.Tickets;

/// <summary>
/// The named colours the Tickets widgets use, matching FleetMate for Mac.
/// Each app maps a tone to its own brush, so the choice of colour is made
/// once here. There is no red: High and failures read orange.
/// </summary>
public enum TicketTone
{
    Blue,
    Green,
    Orange,
    Yellow,
    Purple,
    Teal,
    Indigo,
    Gray,
    Secondary,
}

public static class TicketTones
{
    public static TicketTone Status(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("new") || n.Contains("open")) return TicketTone.Blue;
        if (n.Contains("progress") || n.Contains("process")) return TicketTone.Orange;
        if (n.Contains("hold") || n.Contains("pending") || n.Contains("waiting")) return TicketTone.Yellow;
        if (n.Contains("resolved") || n.Contains("completed")) return TicketTone.Green;
        if (n.Contains("closed")) return TicketTone.Gray;
        if (n.Contains("cancel")) return TicketTone.Orange;
        return TicketTone.Secondary;
    }

    public static TicketTone Priority(string name) => name switch
    {
        "Low" => TicketTone.Green,
        "Medium" => TicketTone.Blue,
        "High" => TicketTone.Orange,
        "Emergency" => TicketTone.Purple,
        _ => TicketTone.Gray,
    };

    public static TicketTone Age(string bucket) => bucket switch
    {
        "Today" => TicketTone.Green,
        "1–7 days" => TicketTone.Blue,
        "8–30 days" => TicketTone.Orange,
        "Over 30 days" => TicketTone.Purple,
        _ => TicketTone.Gray,
    };

    public static TicketTone Responsible(string name) => name == TicketStats.Unassigned ? TicketTone.Gray : TicketTone.Teal;

    public static TicketTone Group(string name) => name == TicketStats.Unassigned ? TicketTone.Gray : TicketTone.Indigo;

    /// <summary>The tone for a value of a widget's category.</summary>
    public static TicketTone For(TicketFilterCategory category, string value) => category switch
    {
        TicketFilterCategory.Status => Status(value),
        TicketFilterCategory.Priority => Priority(value),
        TicketFilterCategory.Age => Age(value),
        TicketFilterCategory.Responsible => Responsible(value),
        TicketFilterCategory.Group => Group(value),
        _ => TicketTone.Secondary,
    };
}

/// <summary>
/// One headline figure over the Tickets list. Each is also a filter: it
/// toggles the selection it counts and lights up while that selection is on.
/// </summary>
public sealed record TicketWidgetTile(string Title, int Value, string Glyph, TicketTone Tone, bool IsOn, string Help, Action Action)
{
    /// <summary>The tooltip and accessible name: the figure, then what clicking it does.</summary>
    public string Description => $"{Title}: {Value:N0}. {Help}";
}

/// <summary>One breakdown over the list: its title, the category it filters and its counts.</summary>
public sealed record TicketWidgetChart(string Title, TicketFilterCategory Category, IReadOnlyList<TicketStats.Count> Counts);

/// <summary>
/// The Tickets widgets: six figures and five breakdowns. Every breakdown
/// counts the list's tickets with every filter applied except its own
/// category's, so its other values stay on screen and can be added to the
/// selection; clicking a value toggles it.
/// </summary>
public static class TicketWidgetRules
{
    /// <summary>
    /// Statuses that count as open: every status the list could show except
    /// On Hold. The Open figure selects exactly these.
    /// </summary>
    public static HashSet<string> OpenStatuses(IEnumerable<TdxTicket> ticketsIgnoringStatus) =>
        new TicketStats(ticketsIgnoringStatus).ByStatus.Select(c => c.Label)
            .Where(l => l != TicketStats.OnHoldStatus).ToHashSet();

    /// <param name="tickets">The list's tickets with every filter applied except the given category's (null: every filter).</param>
    public static List<TicketWidgetTile> Tiles(Func<TicketFilterCategory?, List<TdxTicket>> tickets, TicketFilters filters)
    {
        var stats = new TicketStats(tickets(null));
        var openStatuses = OpenStatuses(tickets(TicketFilterCategory.Status));
        var openOn = openStatuses.Count > 0 && filters.Selected(TicketFilterCategory.Status).SetEquals(openStatuses);

        TicketWidgetTile Single(string title, int value, string glyph, TicketTone tone, string help, TicketFilterCategory category, string v) =>
            new(title, value, glyph, tone, filters.IsSelected(category, v), help, () => filters.Toggle(category, v));

        return new List<TicketWidgetTile>
        {
            new("Showing", stats.Total, "", TicketTone.Secondary, false, "Clear every filter", filters.ClearAll),
            new("Open", stats.Open, "", TicketTone.Purple, openOn, "Show open tickets",
                () => filters.Set(TicketFilterCategory.Status, openOn ? Array.Empty<string>() : openStatuses)),
            Single("On Hold", stats.OnHold, "", TicketTone.Yellow, "Show tickets on hold",
                TicketFilterCategory.Status, TicketStats.OnHoldStatus),
            Single("Unassigned", stats.UnassignedCount, "", TicketTone.Teal, "Show tickets nobody is responsible for",
                TicketFilterCategory.Responsible, TicketStats.Unassigned),
            Single("SLA Violated", stats.SlaViolated, "", TicketTone.Orange, "Show tickets past their SLA",
                TicketFilterCategory.Sla, TicketFilters.SlaViolated),
            Single($"Over {TicketStats.AgingDays} days", stats.Aging, "", TicketTone.Indigo,
                $"Show tickets older than {TicketStats.AgingDays} days", TicketFilterCategory.Age, TicketStats.AgingBucket),
        };
    }

    /// <summary>The five breakdowns, left to right, each counted without its own category's filter.</summary>
    public static List<TicketWidgetChart> Charts(Func<TicketFilterCategory?, List<TdxTicket>> tickets) => new()
    {
        new("By Status", TicketFilterCategory.Status, new TicketStats(tickets(TicketFilterCategory.Status)).ByStatus),
        new("By Priority", TicketFilterCategory.Priority, new TicketStats(tickets(TicketFilterCategory.Priority)).ByPriority),
        new("By Age", TicketFilterCategory.Age, new TicketStats(tickets(TicketFilterCategory.Age)).ByAge),
        new("By Responsible", TicketFilterCategory.Responsible, new TicketStats(tickets(TicketFilterCategory.Responsible)).ByResponsible),
        new("By Group", TicketFilterCategory.Group, new TicketStats(tickets(TicketFilterCategory.Group)).ByGroup),
    };

    /// <summary>Values outside an active selection fade, so a chart shows what is picked and what else could be.</summary>
    public static bool Faded(TicketFilters filters, TicketFilterCategory category, string value)
    {
        var picked = filters.Selected(category);
        return picked.Count > 0 && !picked.Contains(value);
    }
}
