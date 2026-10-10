namespace FleetMate.Core.Models.Tickets;

/// <summary>The Tickets list's filter categories, named as the macOS module filters name them.</summary>
public enum TicketFilterCategory
{
    Status,
    Priority,
    Group,
    Responsible,
    Age,
    Sla,
}

/// <summary>
/// The Tickets list's filter selections: any number of values per category.
/// Values within a category combine with OR, categories with AND. The filter
/// row, the list and the widgets all read and change this one state, so they
/// always agree.
/// </summary>
public sealed class TicketFilters
{
    public const string SlaViolated = "Violated";
    public const string SlaWithin = "Within SLA";

    private readonly Dictionary<TicketFilterCategory, HashSet<string>> _selected = new();

    /// <summary>Raised after any selection changes.</summary>
    public event Action? Changed;

    public static string Title(TicketFilterCategory category) => category switch
    {
        TicketFilterCategory.Sla => "SLA",
        _ => category.ToString(),
    };

    public bool HasActiveFilters => _selected.Values.Any(s => s.Count > 0);

    public IReadOnlySet<string> Selected(TicketFilterCategory category) =>
        _selected.TryGetValue(category, out var set) ? set : new HashSet<string>();

    public bool IsSelected(TicketFilterCategory category, string value) =>
        _selected.TryGetValue(category, out var set) && set.Contains(value);

    /// <summary>Add <paramref name="value"/> to the category's selection, or remove it if already there.</summary>
    public void Toggle(TicketFilterCategory category, string value)
    {
        if (!_selected.TryGetValue(category, out var set)) _selected[category] = set = new HashSet<string>();
        if (!set.Remove(value)) set.Add(value);
        Changed?.Invoke();
    }

    /// <summary>Replace the category's selection; an empty set clears it.</summary>
    public void Set(TicketFilterCategory category, IEnumerable<string> values)
    {
        _selected[category] = new HashSet<string>(values);
        Changed?.Invoke();
    }

    public void Clear(TicketFilterCategory category) => Set(category, Array.Empty<string>());

    public void ClearAll()
    {
        _selected.Clear();
        Changed?.Invoke();
    }

    /// <summary>The value a ticket has in a category, as the filter compares it.</summary>
    public static string ValueOf(TdxTicket ticket, TicketFilterCategory category, DateTime? now = null) => category switch
    {
        TicketFilterCategory.Status => ticket.StatusName ?? "",
        TicketFilterCategory.Priority => ticket.PriorityName ?? "",
        TicketFilterCategory.Group => TicketStats.IsBlank(ticket.ResponsibleGroupName) ? TicketStats.Unassigned : ticket.ResponsibleGroupName!,
        TicketFilterCategory.Responsible => TicketStats.IsBlank(ticket.ResponsibleFullName) ? TicketStats.Unassigned : ticket.ResponsibleFullName!,
        TicketFilterCategory.Age => TicketStats.AgeBucket(ticket, now),
        TicketFilterCategory.Sla => ticket.IsSlaViolated ? SlaViolated : SlaWithin,
        _ => "",
    };

    public bool Matches(TdxTicket ticket, DateTime? now = null) => Matches(ticket, null, now);

    /// <summary>
    /// Matches every selection except <paramref name="ignoring"/>'s, so a
    /// widget can count the values of its own category with the other filters
    /// applied, and its own values can still be added to.
    /// </summary>
    public bool Matches(TdxTicket ticket, TicketFilterCategory? ignoring, DateTime? now = null)
    {
        foreach (var (category, set) in _selected)
        {
            if (set.Count == 0 || category == ignoring) continue;
            if (!set.Contains(ValueOf(ticket, category, now))) return false;
        }
        return true;
    }

    /// <summary>The values a category offers, in the order the filter menu lists them.</summary>
    public static List<string> AvailableValues(IReadOnlyCollection<TdxTicket> tickets, TicketFilterCategory category, DateTime? now = null)
    {
        switch (category)
        {
            case TicketFilterCategory.Age:
                var ages = tickets.Select(t => TicketStats.AgeBucket(t, now)).ToHashSet();
                return TicketStats.AgeBuckets.Select(b => b.Label).Where(ages.Contains).ToList();
            case TicketFilterCategory.Sla:
                return new List<string> { SlaViolated, SlaWithin };
            default:
                // Unassigned is a real state to filter on, not the absence of
                // one: it is how you find the tickets nobody has picked up.
                var values = tickets.Select(t => ValueOf(t, category, now))
                    .Where(v => v.Length > 0 && v != TicketStats.Unassigned)
                    .Distinct()
                    .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (category is TicketFilterCategory.Group or TicketFilterCategory.Responsible
                    && tickets.Any(t => ValueOf(t, category, now) == TicketStats.Unassigned))
                {
                    values.Add(TicketStats.Unassigned);
                }
                return values;
        }
    }

    /// <summary>
    /// Whether <paramref name="ticket"/> is the signed-in person's: matched by
    /// TDX person id when both sides have one, otherwise by full name.
    /// </summary>
    public static bool IsAssignedTo(TdxTicket ticket, TdxPerson me)
    {
        if (me.Uid is { } uid && uid != Guid.Empty && ticket.ResponsibleUid is { } responsible && responsible != Guid.Empty)
            return uid == responsible;
        return !string.IsNullOrWhiteSpace(me.FullName)
               && string.Equals(ticket.ResponsibleFullName, me.FullName, StringComparison.OrdinalIgnoreCase);
    }
}
