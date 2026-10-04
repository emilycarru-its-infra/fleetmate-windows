using System.Text.RegularExpressions;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Identity;
using FleetMate.Core.Models.Inventory;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Models.Tickets;

namespace FleetMate.Core.Services.Search;

public enum SearchCategory { Devices, Inventory, Tickets, WorkItems, Users, Groups }

/// <summary>
/// One result: what to show (title, subtitle, and which field matched) and
/// the id the destination tab opens it by.
/// </summary>
public sealed record SearchHit(SearchCategory Category, string Title, string? Subtitle, string MatchLabel, string Key)
{
    /// <summary>Lower ranks sort first: 0 exact, 1 prefix, 2 contains.</summary>
    public int Rank { get; init; }
}

/// <summary>A category's best hits, capped, with the full match count.</summary>
public sealed record SearchGroup(SearchCategory Category, int Total, IReadOnlyList<SearchHit> Hits)
{
    public string Title => Category switch
    {
        SearchCategory.WorkItems => "Work Items",
        _ => Category.ToString(),
    };
}

/// <summary>The in-memory caches the search scans. No API calls happen here.</summary>
public sealed class SearchSources
{
    public IReadOnlyList<IntuneDevice> Devices { get; init; } = Array.Empty<IntuneDevice>();
    public IReadOnlyList<SnipeAsset> Assets { get; init; } = Array.Empty<SnipeAsset>();
    public IReadOnlyList<TdxTicket> Tickets { get; init; } = Array.Empty<TdxTicket>();
    public IReadOnlyList<WorkItem> WorkItems { get; init; } = Array.Empty<WorkItem>();
    public IReadOnlyList<EntraUser> Users { get; init; } = Array.Empty<EntraUser>();
    public IReadOnlyList<EntraGroup> Groups { get; init; } = Array.Empty<EntraGroup>();
}

/// <summary>
/// The Dashboard's search-everything box (macOS parity): scans the cached
/// devices, assets, tickets, work items, users and groups, and returns hits
/// grouped by category, best first, at most <see cref="PerCategory"/> each.
/// </summary>
public static partial class GlobalSearch
{
    public const int PerCategory = 6;
    public const int MinLength = 2;

    /// <summary>
    /// A work-item id typed bare ("1234"), with a hash ("#1234"), or in the
    /// Boards mention form of a letter prefix and a hash. Null otherwise.
    /// </summary>
    public static int? ParseWorkItemId(string? query)
    {
        var m = WorkItemIdPattern().Match(query?.Trim() ?? "");
        return m.Success && int.TryParse(m.Groups[1].Value, out var id) && id > 0 ? id : null;
    }

    /// <summary>Searching starts at two characters, or at any work-item id.</summary>
    public static bool ShouldSearch(string? query) =>
        (query?.Trim().Length ?? 0) >= MinLength || ParseWorkItemId(query) != null;

    public static List<SearchGroup> Search(string? query, SearchSources sources)
    {
        var groups = new List<SearchGroup>();
        if (!ShouldSearch(query)) return groups;
        var q = query!.Trim();
        var idText = ParseWorkItemId(q)?.ToString();
        // "#1234" should still find ticket 1234 and title text by its digits.
        var text = q.Contains('#') && idText != null ? idText : q;

        Add(groups, SearchCategory.Devices, sources.Devices, d =>
            Best(SearchCategory.Devices, text, d.Id, d.DeviceName,
                Sub(d.Model, d.UserDisplayName),
                ("Name", d.DeviceName), ("Serial", d.SerialNumber), ("User", d.UserDisplayName),
                ("UPN", d.UserPrincipalName), ("Hostname", d.ManagedDeviceName)));

        Add(groups, SearchCategory.Inventory, sources.Assets, a =>
            Best(SearchCategory.Inventory, text, a.Id.ToString(),
                a.DisplayName is { Length: > 0 } n ? n : a.AssetTag,
                Sub(a.AssetTag, a.Model?.Name, a.AssignedTo?.Name),
                new (string, string?)[]
                {
                    ("Name", a.DisplayName), ("Tag", a.AssetTag), ("Serial", a.Serial),
                    ("Model", a.Model?.Name), ("Assigned", a.AssignedTo?.Name),
                }.Concat((a.CustomFields ?? new()).Select(f => (f.Key, f.Value?.Value))).ToArray()));

        Add(groups, SearchCategory.Tickets, sources.Tickets, t =>
            Best(SearchCategory.Tickets, text, t.Id.ToString(), $"#{t.Id} {t.Title}",
                Sub(t.StatusName, t.RequestorName),
                ("ID", t.Id.ToString()), ("Title", t.Title), ("Requestor", t.RequestorName)));

        Add(groups, SearchCategory.WorkItems, sources.WorkItems, w =>
            // A typed id matches that item exactly — not every id containing it.
            idText != null && w.Id.ToString() == idText
                ? WorkItemHit(w)
                : Best(SearchCategory.WorkItems, text, w.Id.ToString(), $"#{w.Id} {w.Fields.Title}",
                    Sub(w.Fields.WorkItemType, w.Fields.State), ("Title", w.Fields.Title)));

        Add(groups, SearchCategory.Users, sources.Users, u =>
            Best(SearchCategory.Users, text, u.Id, u.DisplayName, Sub(u.JobTitle, u.Department),
                ("Name", u.DisplayName), ("UPN", u.UserPrincipalName), ("Mail", u.Mail)));

        Add(groups, SearchCategory.Groups, sources.Groups, g =>
            Best(SearchCategory.Groups, text, g.Id, g.DisplayName, null, ("Name", g.DisplayName)));

        return groups;
    }

    /// <summary>A work item fetched by id because it was not cached, put first.</summary>
    public static void PrependWorkItem(List<SearchGroup> groups, WorkItem item)
    {
        var hit = WorkItemHit(item);
        var existing = groups.FindIndex(g => g.Category == SearchCategory.WorkItems);
        if (existing >= 0)
        {
            var old = groups[existing];
            if (old.Hits.Any(h => h.Key == hit.Key)) return;
            groups.RemoveAt(existing);
            groups.Insert(0, new SearchGroup(SearchCategory.WorkItems, old.Total + 1,
                new[] { hit }.Concat(old.Hits).Take(PerCategory).ToList()));
        }
        else
        {
            groups.Insert(0, new SearchGroup(SearchCategory.WorkItems, 1, new[] { hit }));
        }
    }

    private static SearchHit WorkItemHit(WorkItem w) =>
        new(SearchCategory.WorkItems, $"#{w.Id} {w.Fields.Title}",
            Sub(w.Fields.WorkItemType, w.Fields.State), $"ID: {w.Id}", w.Id.ToString());

    private static void Add<T>(List<SearchGroup> groups, SearchCategory category, IEnumerable<T> items,
        Func<T, SearchHit?> match)
    {
        var hits = items.Select(match).OfType<SearchHit>().ToList();
        if (hits.Count == 0) return;
        groups.Add(new SearchGroup(category, hits.Count,
            hits.OrderBy(h => h.Rank).ThenBy(h => h.Title, StringComparer.OrdinalIgnoreCase)
                .Take(PerCategory).ToList()));
    }

    /// <summary>The best-matching field decides the hit's rank and its match label.</summary>
    private static SearchHit? Best(SearchCategory category, string q, string key, string? title, string? subtitle,
        params (string Label, string? Value)[] fields)
    {
        (int Rank, string Label, string Value)? best = null;
        foreach (var (label, value) in fields)
        {
            if (string.IsNullOrEmpty(value)) continue;
            var index = value.IndexOf(q, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;
            var rank = value.Length == q.Length ? 0 : index == 0 ? 1 : 2;
            if (best == null || rank < best.Value.Rank) best = (rank, label, value);
        }
        if (best is not { } b) return null;
        return new SearchHit(category, string.IsNullOrWhiteSpace(title) ? b.Value : title!, subtitle,
            $"{b.Label}: {Clip(b.Value)}", key) { Rank = b.Rank };
    }

    private static string? Sub(params string?[] parts)
    {
        var shown = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return shown.Count == 0 ? null : string.Join(" · ", shown);
    }

    private static string Clip(string value) => value.Length <= 40 ? value : value[..39] + "…";

    [GeneratedRegex(@"^(?:[A-Za-z]+#|#)?(\d+)$")]
    private static partial Regex WorkItemIdPattern();
}
