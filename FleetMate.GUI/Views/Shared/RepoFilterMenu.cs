using System.Windows;
using System.Windows.Controls;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// One repository dropdown in place of a row of chips, which ran off the edge
/// once there were more than a handful of repositories. Busiest first, each
/// with its count; "All repositories" clears it. Hidden when there is only one
/// repository — no choice to offer.
/// </summary>
public static class RepoFilterMenu
{
    public const string AllLabel = "All repositories";

    /// <summary>One entry in the dropdown; <see cref="Repository"/> is null for "All repositories".</summary>
    public sealed record Item(string? Repository, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>Counts by key, busiest first, then by name. Empty when there is only one key.</summary>
    public static List<(string Repository, int Count)> Counts<T>(IEnumerable<T> items, Func<T, string> key)
    {
        var counts = items
            .GroupBy(key)
            .Select(g => (Repository: g.Key, Count: g.Count()))
            .OrderByDescending(e => e.Count)
            .ThenBy(e => e.Repository, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return counts.Count > 1 ? counts : new();
    }

    /// <summary>
    /// Fill <paramref name="combo"/> and select <paramref name="selection"/>.
    /// Returns the selection that is still valid — a repository that vanished
    /// (refresh, source change) must not keep filtering.
    /// </summary>
    public static string? Fill(ComboBox combo, List<(string Repository, int Count)> counts, string? selection)
    {
        if (selection != null && counts.All(c => c.Repository != selection)) selection = null;

        var items = new List<Item> { new(null, AllLabel) };
        items.AddRange(counts.Select(c => new Item(c.Repository, $"{c.Repository}  {c.Count}")));

        combo.Tag = "filling";
        combo.ItemsSource = items;
        combo.SelectedItem = items.First(i => i.Repository == selection);
        combo.Tag = null;
        combo.Visibility = counts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        return selection;
    }

    /// <summary>
    /// The repository picked by the user, or <c>false</c> in <paramref name="changed"/>
    /// when the change came from <see cref="Fill"/> rather than a person.
    /// </summary>
    public static string? Picked(ComboBox combo, out bool changed)
    {
        changed = combo.Tag as string != "filling" && combo.SelectedItem is Item;
        return (combo.SelectedItem as Item)?.Repository;
    }
}
