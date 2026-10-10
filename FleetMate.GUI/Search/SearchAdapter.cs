using System.Windows;
using FleetMate.Core.Services.Search;

namespace FleetMate.GUI.Search;

/// <summary>One search result row, flattened for a results list.</summary>
/// <param name="Category">The group heading, e.g. "Pull Requests".</param>
/// <param name="Title">The hit's title.</param>
/// <param name="Detail">Its subtitle and the field that matched, e.g. "Proj/Repo · Pat · Branch: fix/x".</param>
/// <param name="Open">Opens the hit through its fleetmate:// link, on the UI thread.</param>
/// <param name="Record">The record behind the hit, where "Copy for Agent" can describe it.</param>
public sealed record SearchRow(string Category, string Title, string Detail, Action Open, object? Record = null);

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

        var rows = groups
            .SelectMany(g => g.Hits.Select(hit => new SearchRow(
                g.Title,
                hit.Title,
                string.Join(" · ", new[] { hit.Subtitle, hit.MatchLabel }.Where(s => !string.IsNullOrWhiteSpace(s))),
                () => app.Dispatcher.BeginInvoke(() => app.OpenLink(hit.Link)),
                Record(g.Category, hit, sources))))
            .ToList();

        // Handbook pages last, each opening in FleetMate's reader (macOS parity).
        if (app.Handbook is { IsConfigured: true } handbook)
            rows.AddRange(handbook.Index.Search(text, GlobalSearch.PerCategory).Select(page => new SearchRow(
                "Handbook",
                page.Title,
                string.IsNullOrEmpty(page.Breadcrumb) ? "Handbook" : page.Breadcrumb,
                () => app.Dispatcher.BeginInvoke(() => app.OpenHandbookPage(page)))));
        return rows;
    }

    /// <summary>The record a hit stands for, for the hits the agent menu describes.</summary>
    private static object? Record(SearchCategory category, SearchHit hit, SearchSources sources) => category switch
    {
        SearchCategory.Reporting => sources.ReportingDevices.FirstOrDefault(d => d.Serial == hit.Key),
        _ => null,
    };
}
