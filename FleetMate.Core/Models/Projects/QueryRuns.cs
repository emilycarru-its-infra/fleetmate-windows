namespace FleetMate.Core.Models.Projects;

/// <summary>A stored query with its materialized rows, ready to render.</summary>
public sealed record QueryRunDisplay(AdoSharedQuery Query, List<QueryRowDisplay> Rows, bool Truncated, string AreaBucket);

/// <summary>One row of a query's results, indented by its depth in a tree query.</summary>
public sealed record QueryRowDisplay(UnifiedTask Task, int Depth, bool HasChildren);

/// <summary>
/// The Projects Queries view's rules, shared by both Windows apps and matching
/// the macOS QueriesListView: each Shared Query's area-path bucket, the Sort
/// Queries By orders, and which rows search, Show Closed and a strip tile keep.
/// </summary>
public static class QueryRuns
{
    /// <summary>
    /// Dominant area-path bucket: the second path component when present
    /// ("Projects\Devices\Macintosh" → "Devices"), else the project itself.
    /// Empty queries fall back to the "Bucket - Query name" convention.
    /// </summary>
    public static string AreaBucket(IEnumerable<QueryRowDisplay> rows, string queryName)
    {
        var counts = new Dictionary<string, int>();
        foreach (var row in rows)
        {
            if (!row.Task.Metadata.TryGetValue("areaPath", out var area) || area.Length == 0) continue;
            var comps = area.Split('\\');
            var bucket = comps.Length >= 2 ? comps[1] : comps[0];
            counts[bucket] = counts.GetValueOrDefault(bucket) + 1;
        }
        if (counts.Count > 0)
            return counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).First().Key;
        foreach (var separator in new[] { " - ", " — " })
        {
            var idx = queryName.IndexOf(separator, StringComparison.Ordinal);
            if (idx > 0) return queryName[..idx];
        }
        return "General";
    }

    /// <summary>A stored query's run as rows, bucketed.</summary>
    public static QueryRunDisplay From(AdoSharedQuery query, StoredQueryRun run)
    {
        var rows = run.Rows.Select(r => new QueryRowDisplay(r.Item.AsUnifiedTask(), r.Depth, r.HasChildren)).ToList();
        return new QueryRunDisplay(query, rows, run.Truncated, AreaBucket(rows, query.Name));
    }

    /// <summary>When anything in a query last changed.</summary>
    public static DateTime LatestChange(QueryRunDisplay run) =>
        run.Rows.Count == 0 ? DateTime.MinValue : run.Rows.Max(r => r.Task.UpdatedAt);

    /// <summary>Areas and the queries inside each, most recently active first — the default order. Ties fall back to name.</summary>
    public static List<(string Key, List<QueryRunDisplay> Runs)> RecentFirst(IEnumerable<QueryRunDisplay> runs) =>
        Ordered(runs, QueryListOrders.Default, QueryListOrders.Default.DefaultAscending(), r => r.Rows.Count);

    /// <summary>
    /// Areas and the queries inside each, in the chosen Sort Queries By order.
    /// <paramref name="runs"/> arrive in Azure DevOps order, which is what
    /// Query Order shows; <paramref name="resultCount"/> counts the rows a
    /// query shows after search and the Closed toggle.
    /// </summary>
    public static List<(string Key, List<QueryRunDisplay> Runs)> Ordered(
        IEnumerable<QueryRunDisplay> runs, QueryListOrder order, bool ascending, Func<QueryRunDisplay, int> resultCount)
    {
        var list = runs.ToList();
        var positions = new Dictionary<QueryRunDisplay, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < list.Count; i++) positions.TryAdd(list[i], i);

        QueryOrderKey RunKey(QueryRunDisplay r) => new(r.Query.Name, resultCount(r), LatestChange(r), positions[r]);

        var sections = list.GroupBy(r => r.AreaBucket)
            .Select(g => (g.Key, Runs: order.Sort(g, ascending, RunKey)))
            .ToList();
        var ordered = order.Sort(sections, ascending, s => new QueryOrderKey(
            s.Key,
            s.Runs.Sum(resultCount),
            s.Runs.Max(LatestChange),
            s.Runs.Min(r => positions[r])));
        // "General" holds whatever fits no project; by name it reads best last.
        if (order == QueryListOrder.Name)
            ordered = ordered.Where(s => s.Key != "General").Concat(ordered.Where(s => s.Key == "General")).ToList();
        return ordered;
    }

    /// <summary>Whether a row shows under the search text, Show Closed and an optional extra test.</summary>
    public static bool RowVisible(QueryRowDisplay row, string search, bool showClosed, Func<UnifiedTask, bool>? rowFilter)
    {
        if (!showClosed && row.Task.State == TaskState.Closed) return false;
        if (rowFilter != null && !rowFilter(row.Task)) return false;
        if (search.Length == 0) return true;
        return row.Task.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
            || row.Task.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
            || row.Task.Assignees.Any(a => a.Contains(search, StringComparison.OrdinalIgnoreCase))
            || row.Task.Labels.Any(l => l.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A query's header name: its folder path, then its name.</summary>
    public static string Name(AdoSharedQuery query) =>
        query.FolderPath.Length > 0 ? $"{query.FolderPath} / {query.Name}" : query.Name;

    /// <summary>A row's state: the work item's own, or the unified state.</summary>
    public static string State(UnifiedTask task) =>
        task.Metadata.TryGetValue("state", out var s) && s.Length > 0 ? s : task.State.ToString();

    /// <summary>The empty message, given what filtered the list.</summary>
    public static string EmptyMessage(bool rowFilter, string search) =>
        rowFilter ? "No rows in the shared queries match the selected tile."
        : search.Length > 0 ? $"No rows match \"{search}\"." : "No shared queries.";
}
