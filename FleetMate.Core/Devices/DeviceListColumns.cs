using FleetMate.Core.Models.Devices;

namespace FleetMate.Core.Devices;

/// <summary>One column of the Devices list: its header, default width, whether it starts shown and how it sorts.</summary>
public sealed record DeviceColumn(string Header, double Width, bool Optional, bool ShownByDefault, bool Sortable = true)
{
    /// <summary>The cell text for <paramref name="row"/>.</summary>
    public string Text(DeviceListRow row) => DeviceListColumns.Text(Header, row);
}

/// <summary>
/// The Devices list's columns, shared by both Windows apps: the same columns
/// for every row (a dash where the row's sources have no value), the optional
/// ones a header menu shows or hides, and how each sorts. The layout people
/// choose is kept in <see cref="DeviceColumnLayout"/>.
/// </summary>
public static class DeviceListColumns
{
    public static readonly IReadOnlyList<DeviceColumn> All = new[]
    {
        new DeviceColumn("Name", 170, false, true),
        new DeviceColumn("Serial", 120, false, true),
        new DeviceColumn("Platform", 90, false, true),
        new DeviceColumn("OS", 200, false, true),
        new DeviceColumn("User", 150, false, true),
        new DeviceColumn("Compliance", 140, false, true),
        new DeviceColumn("Last Sync", 140, false, true),
        new DeviceColumn("Management Service", 150, false, true),
        new DeviceColumn("Org Status", 190, false, true),
        new DeviceColumn("Group / Order", 150, false, true),
        new DeviceColumn("Model", 160, true, false),
        new DeviceColumn("Manufacturer", 120, true, false),
        new DeviceColumn("Ownership", 100, true, false),
        new DeviceColumn("Migration", 100, true, false),
        new DeviceColumn("Purchase Source", 120, true, false),
        new DeviceColumn("Added", 100, true, false),
        // Read per device on selection, so most rows read "—": neither sortable nor a filter.
        new DeviceColumn("Activation Lock", 120, true, false, Sortable: false),
    };

    /// <summary>The headers of the columns a person can show or hide.</summary>
    public static readonly IReadOnlyList<string> Optional = All.Where(c => c.Optional).Select(c => c.Header).ToList();

    /// <summary>The default order, before any saved layout moves it.</summary>
    public static IReadOnlyList<string> DefaultOrder => All.Select(c => c.Header).ToList();

    public static DeviceColumn? Find(string header) => All.FirstOrDefault(c => c.Header == header);

    public static string Text(string header, DeviceListRow row) => header switch
    {
        "Name" => row.NameText,
        "Serial" => row.SerialText,
        "Platform" => row.PlatformText,
        "OS" => row.OsText,
        "User" => row.UserText,
        "Compliance" => row.ComplianceText,
        "Last Sync" => row.LastSyncText,
        "Management Service" => row.ServiceText,
        "Org Status" => row.OrgStatusText,
        "Group / Order" => row.GroupOrOrderText,
        "Model" => row.ModelText,
        "Manufacturer" => row.ManufacturerText,
        "Ownership" => row.OwnershipText,
        "Migration" => row.MigrationText,
        "Purchase Source" => row.PurchaseSourceText,
        "Added" => row.AddedText,
        "Activation Lock" => row.ActivationLockText,
        _ => DeviceListRow.Missing,
    };

    /// <summary>
    /// The rows sorted by <paramref name="header"/>, as the WPF grid sorts
    /// them: Last Sync by time, Added by date, everything else by its text,
    /// ignoring case. A column that cannot sort returns the rows as they are.
    /// </summary>
    public static List<DeviceListRow> Sort(IEnumerable<DeviceListRow> rows, string header, bool descending)
    {
        if (Find(header) is not { Sortable: true }) return rows.ToList();
        IOrderedEnumerable<DeviceListRow> ordered = header switch
        {
            "Last Sync" => descending ? rows.OrderByDescending(r => r.LastSyncSort) : rows.OrderBy(r => r.LastSyncSort),
            "Added" => descending
                ? rows.OrderByDescending(r => r.AddedSort, StringComparer.Ordinal)
                : rows.OrderBy(r => r.AddedSort, StringComparer.Ordinal),
            _ => descending
                ? rows.OrderByDescending(r => Text(header, r), StringComparer.CurrentCultureIgnoreCase)
                : rows.OrderBy(r => Text(header, r), StringComparer.CurrentCultureIgnoreCase),
        };
        return ordered.ToList();
    }

    /// <summary>
    /// Whether the connected services fill an optional column for any device,
    /// so the header menu offers only columns that would show something. With
    /// no rows yet, every column is offered.
    /// </summary>
    public static bool CanFill(string header, IReadOnlyCollection<DeviceListRow> rows)
    {
        if (rows.Count == 0) return true;
        Func<DeviceListRow, bool> filled = header switch
        {
            "Model" => r => r.ModelText != DeviceListRow.Missing,
            "Manufacturer" => r => r.ManufacturerText != DeviceListRow.Missing,
            "Ownership" => r => r.OwnershipText != DeviceListRow.Missing,
            "Migration" => r => r.Apple != null,
            "Purchase Source" => r => r.PurchaseSourceText != DeviceListRow.Missing,
            "Added" => r => r.AddedText != DeviceListRow.Missing,
            // Read per device on selection, so any Apple organization device can fill it.
            "Activation Lock" => r => r.Apple != null,
            _ => _ => true,
        };
        return rows.Any(filled);
    }

    /// <summary>The shown optional columns and the order of every column, for <see cref="DeviceColumnLayout"/>.</summary>
    public static DeviceColumnLayout Layout(IEnumerable<string> shownOptional, IEnumerable<string> order) => new()
    {
        Shown = shownOptional.Where(h => Optional.Contains(h)).ToList(),
        Order = order.ToList(),
    };

    /// <summary>
    /// The columns to draw, in order: every required column and the optional
    /// ones <paramref name="layout"/> shows, arranged as it saved them. No
    /// layout shows the defaults.
    /// </summary>
    public static List<DeviceColumn> Visible(DeviceColumnLayout? layout)
    {
        var order = layout?.Arrange(DefaultOrder.ToList()) ?? DefaultOrder.ToList();
        return order.Select(Find).OfType<DeviceColumn>()
            .Where(c => !c.Optional || (layout?.Shown.Contains(c.Header) ?? c.ShownByDefault))
            .ToList();
    }

    /// <summary>
    /// <paramref name="order"/> with <paramref name="header"/> moved one place
    /// left (<paramref name="step"/> −1) or right (+1); unchanged at either end.
    /// </summary>
    public static List<string> Move(IReadOnlyList<string> order, string header, int step)
    {
        var list = order.ToList();
        var i = list.IndexOf(header);
        var j = i + Math.Sign(step);
        if (i < 0 || j < 0 || j >= list.Count) return list;
        (list[i], list[j]) = (list[j], list[i]);
        return list;
    }
}

/// <summary>The list's count line and the selection summary, worded as on the Mac.</summary>
public static class DeviceListText
{
    /// <summary>
    /// "12 devices", "3 of 12 devices", or with a serial lookup
    /// "4 of 5 looked-up serials found, 1 unknown".
    /// </summary>
    public static string Count(int visible, int total, SerialLookup.Result? lookup, int lookedUp) =>
        lookup != null
            ? $"{lookup.Matched} of {lookedUp} looked-up serials found"
              + (lookup.Unknown.Count > 0 ? $", {lookup.Unknown.Count} unknown" : "")
            : visible == total
                ? $"{total} devices"
                : $"{visible} of {total} devices";

    /// <summary>A device's name, or its serial when it has none yet.</summary>
    public static string Label(DeviceListRow row) => row.NameText != DeviceListRow.Missing ? row.NameText : row.SerialText;

    /// <summary>"3 device(s) selected".</summary>
    public static string SelectedTitle(int count) => $"{count} device(s) selected";

    /// <summary>Up to three names, else the first two "and N more...".</summary>
    public static string SelectedNames(IReadOnlyList<DeviceListRow> selected) =>
        selected.Count <= 3
            ? string.Join(", ", selected.Select(Label))
            : $"{string.Join(", ", selected.Take(2).Select(Label))} and {selected.Count - 2} more...";

    /// <summary>The Filters button: "Filters", or "Filters (2)" with two values ticked.</summary>
    public static string FiltersButton(int active) => active == 0 ? "Filters" : $"Filters ({active})";

    /// <summary>The Serials button: "Serials", or "Serials (5)" while a lookup narrows the list.</summary>
    public static string SerialsButton(int? lookedUp) => lookedUp is { } n ? $"Serials ({n})" : "Serials";

    /// <summary>Under the serials box: "", "1 serial" or "5 serials".</summary>
    public static string ParsedSerials(int count) => count == 0 ? "" : count == 1 ? "1 serial" : $"{count} serials";
}
