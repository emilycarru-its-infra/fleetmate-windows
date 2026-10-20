using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FleetMate.GUI.Views.Development;

/// <summary>
/// Which repository groups Pulls shows collapsed, by group key. Remembered in
/// the user's preferences; a new repository opens expanded. Mirrors the macOS
/// <c>collapsedPullGroups</c>.
/// </summary>
internal static class PullGroupCollapse
{
    /// <summary>The live set the group header converters read.</summary>
    internal static HashSet<string> Collapsed { get; private set; } = new(StringComparer.Ordinal);

    internal static void Load(string? stored) => Collapsed = Parse(stored);

    /// <summary>One key per line, the way the preference is stored.</summary>
    internal static HashSet<string> Parse(string? stored) =>
        new((stored ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.Ordinal);

    /// <summary>Null when nothing is collapsed, so the preference is removed.</summary>
    internal static string? Format(IEnumerable<string> keys)
    {
        var sorted = keys.Where(k => k.Length > 0).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
        return sorted.Count == 0 ? null : string.Join("\n", sorted);
    }

    /// <summary>
    /// A header click: collapse or expand that group. With <paramref name="all"/>
    /// (Ctrl-click, Option-click on the Mac) every visible group follows it.
    /// </summary>
    internal static HashSet<string> Toggle(HashSet<string> collapsed, string key, bool all, IEnumerable<string> visibleKeys)
    {
        var collapse = !collapsed.Contains(key);
        return all ? SetAll(collapsed, collapse, visibleKeys) : With(collapsed, key, collapse);
    }

    /// <summary>Expand All Groups clears every group; Collapse All collapses the visible ones.</summary>
    internal static HashSet<string> SetAll(HashSet<string> collapsed, bool collapse, IEnumerable<string> visibleKeys)
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

    internal static void Set(HashSet<string> collapsed) => Collapsed = collapsed;

    /// <summary>The header menu's own item: Expand when the group is collapsed, Collapse when it is not.</summary>
    internal static string ToggleLabel(bool collapsed) => collapsed ? "Expand" : "Collapse";

    /// <summary>The header's tooltip, naming the group and the Ctrl-click for every group.</summary>
    internal static string Tip(string key, bool collapsed) => collapsed
        ? $"Expand {key} (Ctrl-click expands every group)"
        : $"Collapse {key} (Ctrl-click collapses every group)";

    /// <summary>What a screen reader hears after the group's name.</summary>
    internal static string Status(bool collapsed) => collapsed ? "Collapsed" : "Expanded";
}

/// <summary>
/// Group name → the header's text for the converter parameter: "toggle" for
/// the menu item, "tip" for the tooltip, "status" for the accessibility status.
/// </summary>
public sealed class PullGroupTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value as string ?? "";
        var collapsed = PullGroupCollapse.Collapsed.Contains(key);
        return parameter as string switch
        {
            "toggle" => PullGroupCollapse.ToggleLabel(collapsed),
            "tip" => PullGroupCollapse.Tip(key, collapsed),
            _ => PullGroupCollapse.Status(collapsed),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Group name → Collapsed when that group is collapsed, for the group's rows.</summary>
public sealed class PullGroupRowsVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string key && PullGroupCollapse.Collapsed.Contains(key) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Group name → the disclosure chevron: right when collapsed, down when expanded.</summary>
public sealed class PullGroupChevronConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string key && PullGroupCollapse.Collapsed.Contains(key) ? "" : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
