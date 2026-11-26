namespace FleetMate.Core.Development;

/// <summary>
/// Which repository groups Pulls shows collapsed, by group key. Remembered in
/// the user's preferences; a new repository opens expanded. Mirrors the macOS
/// <c>collapsedPullGroups</c>.
/// </summary>
public static class PullGroupCollapse
{
    /// <summary>The live set the group headers read.</summary>
    public static HashSet<string> Collapsed { get; private set; } = new(StringComparer.Ordinal);

    public static void Load(string? stored) => Collapsed = Parse(stored);

    /// <summary>One key per line, the way the preference is stored.</summary>
    public static HashSet<string> Parse(string? stored) =>
        new((stored ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.Ordinal);

    /// <summary>Null when nothing is collapsed, so the preference is removed.</summary>
    public static string? Format(IEnumerable<string> keys)
    {
        var sorted = keys.Where(k => k.Length > 0).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
        return sorted.Count == 0 ? null : string.Join("\n", sorted);
    }

    /// <summary>
    /// A header click: collapse or expand that group. With <paramref name="all"/>
    /// (Ctrl-click, Option-click on the Mac) every visible group follows it.
    /// </summary>
    public static HashSet<string> Toggle(HashSet<string> collapsed, string key, bool all, IEnumerable<string> visibleKeys)
    {
        var collapse = !collapsed.Contains(key);
        return all ? SetAll(collapsed, collapse, visibleKeys) : With(collapsed, key, collapse);
    }

    /// <summary>Expand All Groups clears every group; Collapse All collapses the visible ones.</summary>
    public static HashSet<string> SetAll(HashSet<string> collapsed, bool collapse, IEnumerable<string> visibleKeys)
    {
        if (!collapse) return new HashSet<string>(StringComparer.Ordinal);
        var next = new HashSet<string>(collapsed, StringComparer.Ordinal);
        next.UnionWith(visibleKeys);
        return next;
    }

    private static HashSet<string> With(HashSet<string> collapsed, string key, bool collapse)
    {
        var next = new HashSet<string>(collapsed, StringComparer.Ordinal);
        if (collapse) next.Add(key); else next.Remove(key);
        return next;
    }

    public static void Set(HashSet<string> collapsed) => Collapsed = collapsed;

    /// <summary>The header menu's own item: Expand when the group is collapsed, Collapse when it is not.</summary>
    public static string ToggleLabel(bool collapsed) => collapsed ? "Expand" : "Collapse";

    /// <summary>The header's tooltip, naming the group and the Ctrl-click for every group.</summary>
    public static string Tip(string key, bool collapsed) => collapsed
        ? $"Expand {key} (Ctrl-click expands every group)"
        : $"Collapse {key} (Ctrl-click collapses every group)";

    /// <summary>What a screen reader hears after the group's name.</summary>
    public static string Status(bool collapsed) => collapsed ? "Collapsed" : "Expanded";
}
