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
    Created,
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
        TicketListColumn.Created => "Created",
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
        TicketListColumn.Created => 120,
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

    /// <summary>The column a sort field belongs to, if the table has one.</summary>
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

    /// <summary>The Created column reads like Modified.</summary>
    public static string Created(DateTime value, IFormatProvider? culture = null) => Modified(value, culture);

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

/// <summary>
/// Fits the table to its pane, as the Mac's table does: Title takes whatever
/// width the other columns leave, and when the pane is too narrow for the other
/// columns' widths they give way, down to a floor that keeps their values
/// readable, before anything scrolls sideways. The widths a person dragged are
/// the starting point and are never changed by fitting; fitting only decides
/// what is shown.
/// </summary>
public static class TicketListColumnFit
{
    /// <summary>The narrowest Title is squeezed to before the other columns give way.</summary>
    public const double TitleMinWidth = 180;

    /// <summary>
    /// The narrowest fitting takes each column. It never widens a column a
    /// person dragged narrower than this; it only stops shrinking there.
    /// </summary>
    public static double Floor(TicketListColumn column) => column switch
    {
        TicketListColumn.Id => 84,
        TicketListColumn.Title => TitleMinWidth,
        TicketListColumn.Status => 90,
        TicketListColumn.Priority => 64,
        TicketListColumn.Requestor => 90,
        TicketListColumn.Responsible => 90,
        TicketListColumn.Age => 48,
        TicketListColumn.Modified => 100,
        TicketListColumn.Created => 100,
        _ => TicketListColumns.MinWidth,
    };

    /// <summary>
    /// The width to show each column at in a pane <paramref name="available"/>
    /// wide. <paramref name="desired"/> holds each column's own width, saved or
    /// default. <paramref name="held"/> is a column being dragged right now: it
    /// keeps the width under the pointer while the others make room. Widths are
    /// whole pixels. A pane not yet laid out (zero or less) shows the desired
    /// widths unchanged.
    /// </summary>
    public static IReadOnlyDictionary<TicketListColumn, double> Fit(
        IReadOnlyDictionary<TicketListColumn, double> desired, double available, TicketListColumn? held = null)
    {
        var result = desired.ToDictionary(p => p.Key, p => Math.Round(Math.Max(p.Value, TicketListColumns.MinWidth)));
        if (!double.IsFinite(available) || available <= 0 || !result.ContainsKey(TicketListColumn.Title)) return result;

        var others = result.Keys.Where(c => c != TicketListColumn.Title).ToList();
        var othersWidth = others.Sum(c => result[c]);
        var overflow = othersWidth + TitleMinWidth - available;

        if (overflow > 0)
        {
            // Share the cut across the columns by how much each has to give,
            // so a wide column gives more than one already near its floor.
            var giving = others.Where(c => c != held).ToList();
            var slack = giving.ToDictionary(c => c, c => Math.Max(0, result[c] - Math.Min(result[c], Floor(c))));
            var totalSlack = slack.Values.Sum();
            if (totalSlack > 0)
            {
                var cut = Math.Min(overflow, totalSlack);
                foreach (var column in giving)
                    result[column] = Math.Floor(result[column] - cut * slack[column] / totalSlack);
            }
            othersWidth = others.Sum(c => result[c]);
        }

        result[TicketListColumn.Title] = Math.Max(TitleMinWidth, Math.Floor(available - othersWidth));
        return result;
    }
}
