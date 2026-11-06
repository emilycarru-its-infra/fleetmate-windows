using System.Globalization;
using System.Text.Json;

namespace FleetMate.Core.Models.Tickets;

/// <summary>A column of the ticket list's table, in the macOS app's default order.</summary>
public enum TicketListColumn
{
    Id,
    Title,
    Status,
    Priority,
    Requestor,
    Responsible,
    Age,
    Modified,
}

/// <summary>
/// The ticket list's columns: their headers, the sort field each one sorts by,
/// and their widths. The headers and widths are the macOS app's, so the two
/// tables read the same.
/// </summary>
public static class TicketListColumns
{
    /// <summary>Narrowest a column can be dragged, as on Mac.</summary>
    public const double MinWidth = 40;

    /// <summary>Widest a saved width is taken at face value; anything above is a corrupt save.</summary>
    public const double MaxWidth = 2000;

    /// <summary>Every column in its default left-to-right order.</summary>
    public static readonly IReadOnlyList<TicketListColumn> All = Enum.GetValues<TicketListColumn>();

    /// <summary>
    /// The header text. It is also the sort field name the toolbar's sort
    /// control uses, so a header click and the toolbar set the same state.
    /// </summary>
    public static string Header(TicketListColumn column) => column switch
    {
        TicketListColumn.Id => "ID",
        TicketListColumn.Title => "Title",
        TicketListColumn.Status => "Status",
        TicketListColumn.Priority => "Priority",
        TicketListColumn.Requestor => "Requestor",
        TicketListColumn.Responsible => "Responsible",
        TicketListColumn.Age => "Age",
        TicketListColumn.Modified => "Modified",
        _ => column.ToString(),
    };

    /// <summary>The sort field a header click selects.</summary>
    public static string SortField(TicketListColumn column) => Header(column);

    /// <summary>The column whose header names <paramref name="header"/>, if any.</summary>
    public static TicketListColumn? FromHeader(string? header) =>
        All.Cast<TicketListColumn?>().FirstOrDefault(c => string.Equals(Header(c!.Value), header, StringComparison.Ordinal));

    /// <summary>The macOS app's default widths.</summary>
    public static double DefaultWidth(TicketListColumn column) => column switch
    {
        TicketListColumn.Id => 130,
        TicketListColumn.Title => 500,
        TicketListColumn.Status => 110,
        TicketListColumn.Priority => 90,
        TicketListColumn.Requestor => 130,
        TicketListColumn.Responsible => 130,
        TicketListColumn.Age => 70,
        TicketListColumn.Modified => 120,
        _ => 100,
    };
}

/// <summary>
/// How the ticket list is ordered. The field and direction are the page's one
/// sort state; the toolbar's sort control and the table's headers both set it.
/// </summary>
public static class TicketListSort
{
    /// <summary>The fields the list can sort by, the toolbar's order.</summary>
    public static readonly IReadOnlyList<string> Fields = new[]
    {
        "Modified", "Created", "ID", "Age", "Title", "Status", "Priority", "Requestor", "Responsible",
    };

    /// <summary>Text compares ignoring case, and a missing value sorts as empty.</summary>
    private static readonly StringComparer Text = StringComparer.CurrentCultureIgnoreCase;

    /// <summary>
    /// Order tickets by a field. The sort is stable, so tickets that tie keep
    /// the order they came in. An unknown field sorts by Modified.
    /// </summary>
    public static IEnumerable<TdxTicket> Apply(IEnumerable<TdxTicket> tickets, string? field, bool ascending)
    {
        return field switch
        {
            "Created" => By(t => t.CreatedDate, Comparer<DateTime>.Default),
            "ID" => By(t => t.Id, Comparer<int>.Default),
            // Ascending age is youngest first; descending reads a queue by what
            // has waited longest, as on Mac. Age counts back from the created
            // date, so it is the created date in reverse.
            "Age" => ascending
                ? tickets.OrderByDescending(t => t.CreatedDate)
                : tickets.OrderBy(t => t.CreatedDate),
            "Title" => By(t => t.Title ?? "", Text),
            "Status" => By(t => t.StatusName ?? "", Text),
            "Priority" => By(t => t.PriorityName ?? "", Text),
            "Requestor" => By(t => t.RequestorName ?? "", Text),
            "Responsible" => By(t => t.ResponsibleFullName ?? "", Text),
            _ => By(t => t.ModifiedDate, Comparer<DateTime?>.Default),
        };

        IEnumerable<TdxTicket> By<TKey>(Func<TdxTicket, TKey> key, IComparer<TKey> comparer) =>
            ascending ? tickets.OrderBy(key, comparer) : tickets.OrderByDescending(key, comparer);
    }

    /// <summary>
    /// The sort after a header click: the active column flips direction, any
    /// other column becomes active ascending, as on Mac.
    /// </summary>
    public static (string Field, bool Ascending) AfterHeaderClick(string currentField, bool currentAscending, TicketListColumn clicked)
    {
        var field = TicketListColumns.SortField(clicked);
        return string.Equals(field, currentField, StringComparison.Ordinal)
            ? (field, !currentAscending)
            : (field, true);
    }

    /// <summary>The column a sort field belongs to; Created has none in the table.</summary>
    public static TicketListColumn? ColumnFor(string? field) => TicketListColumns.FromHeader(field);

    /// <summary>What a screen reader says for a header: its name and whether the list is sorted by it.</summary>
    public static string SpokenHeader(TicketListColumn column, string? sortField, bool ascending)
    {
        var name = TicketListColumns.Header(column);
        if (ColumnFor(sortField) != column) return $"{name}, not sorted";
        return ascending ? $"{name}, sorted ascending" : $"{name}, sorted descending";
    }
}

/// <summary>How the table's cells read.</summary>
public static class TicketListFormat
{
    /// <summary>A ticket this many days old or more shows its age in orange, as on Mac.</summary>
    public const int AgeHighlightDays = 14;

    public static bool IsAgeHighlighted(int? days) => days is { } d && d >= AgeHighlightDays;

    /// <summary>"Oct 15 at 7:01 PM" in local time, the detail pane's and the Mac's format.</summary>
    public static string Modified(DateTime? value, IFormatProvider? culture = null)
    {
        if (value is not { } date || date == default) return "-";
        var local = date.Kind == DateTimeKind.Utc ? date.ToLocalTime() : date;
        return local.ToString("MMM d 'at' h:mm tt", culture ?? CultureInfo.CurrentCulture);
    }
}

/// <summary>
/// The table's saved arrangement: the order its columns sit in and the width
/// each was dragged to. Anything unreadable falls back to the defaults, and a
/// column a later version adds keeps its default place.
/// </summary>
public sealed class TicketListColumnLayout
{
    /// <summary>Column headers, left to right.</summary>
    public List<string> Order { get; set; } = new();

    /// <summary>Column widths by header.</summary>
    public Dictionary<string, double> Widths { get; set; } = new();

    /// <summary>The saved layout, or the default one when there is none or it cannot be read.</summary>
    public static TicketListColumnLayout Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            var layout = JsonSerializer.Deserialize<TicketListColumnLayout>(json);
            return new TicketListColumnLayout
            {
                Order = layout?.Order?.Where(h => h != null).ToList() ?? new(),
                Widths = layout?.Widths ?? new(),
            };
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public string Serialize() => JsonSerializer.Serialize(this);

    /// <summary>The layout of a table as it stands, columns given left to right.</summary>
    public static TicketListColumnLayout Capture(IEnumerable<(TicketListColumn Column, double Width)> columns)
    {
        var layout = new TicketListColumnLayout();
        foreach (var (column, width) in columns)
        {
            var header = TicketListColumns.Header(column);
            layout.Order.Add(header);
            if (IsUsable(width)) layout.Widths[header] = Math.Round(width);
        }
        return layout;
    }

    /// <summary>
    /// Every column in saved order. Unknown and repeated names are dropped, and
    /// a column the save does not name goes after the column it follows by
    /// default, so the result always holds each column once.
    /// </summary>
    public IReadOnlyList<TicketListColumn> Arrange()
    {
        var result = Order
            .Select(TicketListColumns.FromHeader)
            .OfType<TicketListColumn>()
            .Distinct()
            .ToList();

        var defaults = TicketListColumns.All;
        for (var i = 0; i < defaults.Count; i++)
        {
            if (result.Contains(defaults[i])) continue;
            var after = i == 0 ? -1 : result.IndexOf(defaults[i - 1]);
            result.Insert(after + 1, defaults[i]);
        }
        return result;
    }

    /// <summary>The saved width, held to the drag limits, or the default when none is usable.</summary>
    public double WidthOf(TicketListColumn column)
    {
        if (Widths.TryGetValue(TicketListColumns.Header(column), out var width) && IsUsable(width))
            return Math.Clamp(width, TicketListColumns.MinWidth, TicketListColumns.MaxWidth);
        return TicketListColumns.DefaultWidth(column);
    }

    private static bool IsUsable(double width) => double.IsFinite(width) && width > 0 && width <= TicketListColumns.MaxWidth;
}
