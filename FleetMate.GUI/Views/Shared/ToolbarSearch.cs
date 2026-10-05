namespace FleetMate.GUI.Views.Shared;

/// <summary>One hit in the toolbar search's dropdown.</summary>
/// <param name="Category">Group heading: Pull Requests, Issues, Work Items, Commits, Pipeline Runs, Devices…</param>
/// <param name="Open">What choosing the hit does — normally opening its fleetmate:// link.</param>
public sealed record ToolbarSearchResult(string Category, string Title, string Detail, Action Open);

/// <summary>
/// The seam between the toolbar search field and the search engine. The field
/// owns typing, Ctrl+K, Enter, Esc and the dropdown; whatever is assigned to
/// <see cref="Provider"/> owns matching. The global search in Core plugs in
/// here, mapping each of its hits to a result that opens its link.
/// </summary>
public static class ToolbarSearch
{
    /// <summary>Returns hits for a query, best first; categories keep their first-seen order.</summary>
    public static Func<string, CancellationToken, Task<IReadOnlyList<ToolbarSearchResult>>>? Provider { get; set; }

    /// <summary>Hits grouped for the dropdown, groups in first-hit order, each capped.</summary>
    public static List<(string Category, List<ToolbarSearchResult> Hits)> Group(
        IReadOnlyList<ToolbarSearchResult> hits, int perCategory = 8) =>
        hits.GroupBy(h => h.Category)
            .Select(g => (g.Key, g.Take(perCategory).ToList()))
            .ToList();
}
