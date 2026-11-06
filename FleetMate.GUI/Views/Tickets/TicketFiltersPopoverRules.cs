using FleetMate.Core.Models.Tickets;

namespace FleetMate.GUI.Views.Tickets;

/// <summary>
/// One section of the Filters popover's left column: a ticket filter
/// category, or the Time Period section that holds the created-date window
/// and Show Closed. The Mac's FilterPanelView lists the same categories in
/// the same order, with Time Period last.
/// </summary>
internal sealed record TicketFilterSection(TicketFilterCategory? Category)
{
    public bool IsTimePeriod => Category == null;

    public string Title => Category is { } category ? TicketFilters.Title(category) : TicketFiltersPopoverRules.TimePeriodTitle;
}

/// <summary>
/// What the Tickets Filters button and its popover show: the active-filter
/// count on the button, the per-section counts, the value list and its
/// search. Kept apart from the page so it is testable without a window.
/// </summary>
internal static class TicketFiltersPopoverRules
{
    public const string TimePeriodTitle = "Time Period";

    /// <summary>The popover's sections, top to bottom, as on Mac.</summary>
    public static IReadOnlyList<TicketFilterSection> Sections { get; } = new[]
    {
        new TicketFilterSection(TicketFilterCategory.Status),
        new TicketFilterSection(TicketFilterCategory.Priority),
        new TicketFilterSection(TicketFilterCategory.Group),
        new TicketFilterSection(TicketFilterCategory.Responsible),
        new TicketFilterSection(TicketFilterCategory.Age),
        new TicketFilterSection(TicketFilterCategory.Sla),
        new TicketFilterSection(null),
    };

    /// <summary>
    /// How many filters narrow the list: every picked value, plus Show Closed
    /// when on and a date window other than the current term, since Clear All
    /// resets those two as well.
    /// </summary>
    public static int ActiveCount(TicketFilters filters, bool showClosed, bool defaultDateRange) =>
        Sections.Where(s => !s.IsTimePeriod).Sum(s => filters.Selected(s.Category!.Value).Count)
        + TimePeriodCount(showClosed, defaultDateRange);

    /// <summary>The Time Period section's count: Show Closed on, and a date window other than the current term.</summary>
    public static int TimePeriodCount(bool showClosed, bool defaultDateRange) =>
        (showClosed ? 1 : 0) + (defaultDateRange ? 0 : 1);

    /// <summary>A section's count in the left column.</summary>
    public static int SectionCount(TicketFilterSection section, TicketFilters filters, bool showClosed, bool defaultDateRange) =>
        section.Category is { } category
            ? filters.Selected(category).Count
            : TimePeriodCount(showClosed, defaultDateRange);

    /// <summary>The count badge on the Filters button and beside a section; empty hides it.</summary>
    public static string BadgeText(int count) => count switch
    {
        <= 0 => "",
        > 99 => "99+",
        _ => count.ToString(),
    };

    /// <summary>The Filters button's accessible name, which says how many are on.</summary>
    public static string ButtonName(int active) => active switch
    {
        <= 0 => "Filters",
        1 => "Filters, 1 active",
        _ => $"Filters, {active} active",
    };

    public static string ButtonToolTip(int active) => active switch
    {
        <= 0 => "Filter the tickets",
        1 => "1 filter is on. Click to change or clear it.",
        _ => $"{active} filters are on. Click to change or clear them.",
    };

    /// <summary>A section row's accessible name, with its count when it has one.</summary>
    public static string SectionName(string title, int count) =>
        count > 0 ? $"{title}, {count} selected" : title;

    /// <summary>Clear All shows only when something is on, as on Mac.</summary>
    public static bool ShowsClearAll(int active) => active > 0;

    /// <summary>
    /// A category's values: those the loaded tickets offer, then any picked
    /// value no longer among them, so it can still be cleared.
    /// </summary>
    public static List<string> Values(IReadOnlyList<string> available, IReadOnlySet<string> selected)
    {
        var values = available.ToList();
        values.AddRange(selected.Where(v => !available.Contains(v)).OrderBy(v => v, StringComparer.OrdinalIgnoreCase));
        return values;
    }

    /// <summary>The values matching the popover's search, case-insensitive; a blank search keeps them all.</summary>
    public static List<string> Search(IEnumerable<string> values, string? query)
    {
        var q = query?.Trim();
        return string.IsNullOrEmpty(q)
            ? values.ToList()
            : values.Where(v => v.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
