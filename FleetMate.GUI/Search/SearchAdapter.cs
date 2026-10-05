using System.Windows;
using FleetMate.Core.Services.Search;

namespace FleetMate.GUI.Search;

/// <summary>One search result row, flattened for a results list.</summary>
/// <param name="Category">The group heading, e.g. "Pull Requests".</param>
/// <param name="Title">The hit's title.</param>
/// <param name="Detail">Its subtitle and the field that matched, e.g. "Proj/Repo · Pat · Branch: fix/x".</param>
/// <param name="Open">Opens the hit through its fleetmate:// link, on the UI thread.</param>
public sealed record SearchRow(string Category, string Title, string Detail, Action Open);

/// <summary>
/// The one search behind every search field: <see cref="GlobalSearch"/> over
/// the app's loaded data, each hit opening through <see cref="App.OpenLink"/>.
/// </summary>
public static class SearchAdapter
{
    /// <summary>
    /// Search the loaded data for <paramref name="text"/>. Rows come grouped
    /// by category in display order, at most <see cref="GlobalSearch.PerCategory"/>
    /// per group; an empty list when the text is too short to search.
    /// </summary>
    public static async Task<IReadOnlyList<SearchRow>> QueryAsync(string text, CancellationToken ct)
    {
        if (Application.Current is not App app || !GlobalSearch.ShouldSearch(text))
            return Array.Empty<SearchRow>();

        // Snapshot the caches on the UI thread; match off it.
        var sources = app.Dispatcher.CheckAccess()
            ? app.BuildSearchSources()
            : await app.Dispatcher.InvokeAsync(app.BuildSearchSources);
        var groups = await Task.Run(() => GlobalSearch.Search(text, sources), ct);
        ct.ThrowIfCancellationRequested();

        return groups
            .SelectMany(g => g.Hits.Select(hit => new SearchRow(
                g.Title,
                hit.Title,
                string.Join(" · ", new[] { hit.Subtitle, hit.MatchLabel }.Where(s => !string.IsNullOrWhiteSpace(s))),
                () => app.Dispatcher.BeginInvoke(() => app.OpenLink(hit.Link)))))
            .ToList();
    }
}
