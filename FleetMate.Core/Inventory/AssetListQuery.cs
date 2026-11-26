using FleetMate.Core.Models.Inventory;

namespace FleetMate.Core.Inventory;

/// <summary>The Inventory list's filters, in the order they show.</summary>
public enum AssetFilter { Status, StatusType, Category, Platform, Manufacturer, Model, Usage, Catalog, Area, Location }

/// <summary>
/// The Inventory list: the search, the filters and the sort, shared by both
/// Windows apps. Status holds each status name and Status Type the types they
/// share, as on the Mac; the Asset Status graph sets Status Type, so a wedge
/// selects every asset of its type. A filter set to <see cref="All"/> (or
/// null) lets everything through.
/// </summary>
public sealed class AssetListQuery
{
    public const string All = "All";

    /// <summary>The default sort: most recently touched first (macOS parity).</summary>
    public const string RecentSort = "Recent";

    /// <summary>The list's sortable columns, by header, in the order <see cref="InventoryColumns.All"/> shows them.</summary>
    public static IReadOnlyList<string> SortFields => InventoryColumns.All;

    private readonly Dictionary<AssetFilter, string?> _filters = new();

    public string Search { get; set; } = "";
    public string SortField { get; private set; } = RecentSort;
    public bool SortDescending { get; private set; } = true;

    public static IReadOnlyList<AssetFilter> Filters { get; } = Enum.GetValues<AssetFilter>();

    /// <summary>The filter's label: "Status Type", "Manufacturer".</summary>
    public static string Label(AssetFilter filter) => filter == AssetFilter.StatusType ? "Status Type" : filter.ToString();

    public string? this[AssetFilter filter]
    {
        get => _filters.GetValueOrDefault(filter);
        set => _filters[filter] = value;
    }

    /// <summary>Any filter is set to something other than <see cref="All"/>.</summary>
    public bool HasActiveFilters => _filters.Values.Any(IsSet);

    public void ClearFilters() => _filters.Clear();

    /// <summary>
    /// A column header was clicked: the same column flips its direction, a new
    /// one sorts ascending, or newest first for Last Activity.
    /// </summary>
    public void SortBy(string field)
    {
        if (SortField == field)
        {
            SortDescending = !SortDescending;
            return;
        }
        SortField = field;
        SortDescending = field == "Last Activity";
    }

    /// <summary>The value an asset has for <paramref name="filter"/>, or null when it has none.</summary>
    public static string? Value(SnipeAsset asset, AssetFilter filter) => filter switch
    {
        AssetFilter.Status => InventoryFigures.AssetStatusNameFilterValue(asset),
        AssetFilter.StatusType => InventoryFigures.AssetStatusTypeFilterValue(asset),
        AssetFilter.Category => asset.Category?.Name,
        AssetFilter.Platform => asset.Platform,
        AssetFilter.Manufacturer => asset.Manufacturer?.Name,
        AssetFilter.Model => asset.Model?.Name,
        AssetFilter.Usage => asset.Usage,
        AssetFilter.Catalog => asset.Catalog,
        AssetFilter.Area => asset.Area,
        _ => asset.LocationName,
    };

    /// <summary>"All", then every value the assets have for <paramref name="filter"/>, sorted.</summary>
    public static IReadOnlyList<string> Options(IEnumerable<SnipeAsset> assets, AssetFilter filter)
    {
        if (filter == AssetFilter.Location) return AssetLocationFilter.Options(assets);
        return new[] { All }.Concat(assets.Select(a => Value(a, filter))
                .Where(v => !string.IsNullOrEmpty(v))
                .Select(v => v!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(v => v, StringComparer.CurrentCulture))
            .ToList();
    }

    /// <summary>The search text matches the asset's name, tag, serial or assignee.</summary>
    public static bool MatchesSearch(SnipeAsset asset, string? search)
    {
        var text = search?.Trim();
        if (string.IsNullOrEmpty(text)) return true;
        return Contains(asset.DisplayName, text) || Contains(asset.AssetTag, text)
            || Contains(asset.Serial, text) || Contains(asset.AssignedTo?.Name, text);
    }

    public bool Matches(SnipeAsset asset)
    {
        if (!MatchesSearch(asset, Search)) return false;
        foreach (var (filter, selected) in _filters)
        {
            if (!IsSet(selected)) continue;
            if (filter == AssetFilter.Location ? !AssetLocationFilter.Matches(asset, selected) : Value(asset, filter) != selected)
                return false;
        }
        return true;
    }

    /// <summary>The assets that pass the search and filters, in the sort's order.</summary>
    public List<SnipeAsset> Apply(IEnumerable<SnipeAsset> assets)
    {
        // Snipe-IT timestamps are "yyyy-MM-dd HH:mm:ss", which sort correctly
        // as text, so the date sorts compare the raw string.
        var key = SortKey(SortField);
        var matching = assets.Where(Matches);
        return (SortDescending
            ? matching.OrderByDescending(key, StringComparer.OrdinalIgnoreCase)
            : matching.OrderBy(key, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>The text a column shows for an asset; also what it sorts on.</summary>
    public static string Cell(SnipeAsset asset, string column) =>
        column == "Last Activity" ? asset.LastActivityFormatted : SortKey(column)(asset);

    private static Func<SnipeAsset, string> SortKey(string field) => field switch
    {
        "Asset Tag" => a => a.AssetTag ?? "",
        "Name" => a => a.DisplayName ?? "",
        "Serial" => a => a.Serial ?? "",
        "Model" => a => a.Model?.Name ?? "",
        "Status" => a => a.StatusLabel?.Name ?? "",
        "Assigned To" => a => a.AssignedTo?.Name ?? "",
        "Category" => a => a.Category?.Name ?? "",
        "Manufacturer" => a => a.Manufacturer?.Name ?? "",
        "Platform" => a => a.Platform ?? "",
        "Usage" => a => a.Usage ?? "",
        "Catalog" => a => a.Catalog ?? "",
        "Area" => a => a.Area ?? "",
        "Location" => a => a.LocationName ?? "",
        _ => a => a.LastActivity?.DateTime ?? "",
    };

    private static bool IsSet(string? value) => !string.IsNullOrEmpty(value) && value != All;

    private static bool Contains(string? value, string text) =>
        value?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;
}
