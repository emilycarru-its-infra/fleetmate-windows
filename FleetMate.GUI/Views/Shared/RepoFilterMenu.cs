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

    public static string? Fill(RepositoryFilterButton button, List<FleetMate.Core.Models.Projects.RepositoryFilterEntry> entries, string? selection) =>
        button.SetEntries(entries, selection);

    public static string? Picked(RepositoryFilterButton button, out bool changed)
    {
        changed = true;
        return button.Selection;
    }
}
