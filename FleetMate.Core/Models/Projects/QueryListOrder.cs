namespace FleetMate.Core.Models.Projects;

/// <summary>
/// How the Projects queries list orders its stored queries, and the project
/// groups that hold them. It orders the queries themselves, so it works while
/// every query is collapsed. Mirrors the macOS <c>QueryListOrder</c>, with the
/// same stored values.
/// </summary>
public enum QueryListOrder
{
    /// <summary>Most recently changed work first. The list's long-standing default.</summary>
    LastChanged,
    Name,
    ResultCount,
    /// <summary>The order Azure DevOps keeps the shared queries in.</summary>
    QueryOrder
}

/// <summary>What the sort needs to know about one query, or one project group.</summary>
public readonly record struct QueryOrderKey(string Name, int ResultCount, DateTime LastChanged, int Position);

public static class QueryListOrders
{
    public const QueryListOrder Default = QueryListOrder.LastChanged;

    public static readonly QueryListOrder[] All =
        { QueryListOrder.LastChanged, QueryListOrder.Name, QueryListOrder.ResultCount, QueryListOrder.QueryOrder };

    /// <summary>The value stored in preferences — the macOS app's raw values.</summary>
    public static string StorageValue(this QueryListOrder order) => order switch
    {
        QueryListOrder.Name => "name",
        QueryListOrder.ResultCount => "resultCount",
        QueryListOrder.QueryOrder => "queryOrder",
        _ => "lastChanged"
    };

    public static QueryListOrder Parse(string? value) => value switch
    {
        "name" => QueryListOrder.Name,
        "resultCount" => QueryListOrder.ResultCount,
        "queryOrder" => QueryListOrder.QueryOrder,
        _ => Default
    };

    public static string Title(this QueryListOrder order) => order switch
    {
        QueryListOrder.Name => "Name",
        QueryListOrder.ResultCount => "Result Count",
        QueryListOrder.QueryOrder => "Query Order",
        _ => "Last Changed"
    };

    /// <summary>
    /// The direction a fresh pick starts in: names A to Z, the newest and the
    /// biggest queries first, and Azure DevOps order as stored.
    /// </summary>
    public static bool DefaultAscending(this QueryListOrder order) =>
        order is QueryListOrder.Name or QueryListOrder.QueryOrder;

    /// <summary>Words for each direction, so the menu reads "Newest First" rather than "Ascending".</summary>
    public static string DirectionTitle(this QueryListOrder order, bool ascending) => order switch
    {
        QueryListOrder.Name => ascending ? "A to Z" : "Z to A",
        QueryListOrder.ResultCount => ascending ? "Fewest First" : "Most First",
        QueryListOrder.QueryOrder => ascending ? "As Stored" : "Reversed",
        _ => ascending ? "Oldest First" : "Newest First"
    };

    /// <summary>
    /// Sort items by this order. Ties fall back to name, then stored position,
    /// so the result is stable whichever key is chosen.
    /// </summary>
    public static List<T> Sort<T>(this QueryListOrder order, IEnumerable<T> items, bool ascending, Func<T, QueryOrderKey> key)
    {
        var keyed = items.Select(item => (Item: item, Key: key(item))).ToList();
        keyed.Sort((a, b) =>
        {
            var primary = Compare(order, a.Key, b.Key);
            if (primary != 0) return ascending ? primary : -primary;
            var byName = string.Compare(a.Key.Name, b.Key.Name, StringComparison.CurrentCultureIgnoreCase);
            return byName != 0 ? byName : a.Key.Position.CompareTo(b.Key.Position);
        });
        return keyed.Select(k => k.Item).ToList();
    }

    private static int Compare(QueryListOrder order, QueryOrderKey a, QueryOrderKey b) => order switch
    {
        QueryListOrder.Name => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase),
        QueryListOrder.ResultCount => a.ResultCount.CompareTo(b.ResultCount),
        QueryListOrder.QueryOrder => a.Position.CompareTo(b.Position),
        _ => a.LastChanged.CompareTo(b.LastChanged)
    };
}
