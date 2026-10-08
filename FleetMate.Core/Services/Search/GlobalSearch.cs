using System.Text.RegularExpressions;
using FleetMate.Core.Links;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Identity;
using FleetMate.Core.Models.Inventory;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Models.Reporting;
using FleetMate.Core.Models.Tickets;

namespace FleetMate.Core.Services.Search;

public enum SearchCategory { Devices, Reporting, Inventory, Tickets, WorkItems, Users, Groups, PullRequests, Issues, Commits, PipelineRuns }

/// <summary>
/// One result: what to show (title, subtitle, and which field matched) and
/// the id the destination tab opens it by.
/// </summary>
public sealed record SearchHit(SearchCategory Category, string Title, string? Subtitle, string MatchLabel, string Key)
{
    /// <summary>Lower ranks sort first: 0 exact, 1 prefix, 2 contains.</summary>
    public int Rank { get; init; }

    /// <summary>
    /// The fleetmate:// link that opens this hit, the same route an outside
    /// link takes. Empty only when the source record lacks what a link needs.
    /// </summary>
    public string Link { get; init; } = "";
}

/// <summary>A category's best hits, capped, with the full match count.</summary>
public sealed record SearchGroup(SearchCategory Category, int Total, IReadOnlyList<SearchHit> Hits)
{
    public string Title => Category switch
    {
        SearchCategory.WorkItems => "Work Items",
        SearchCategory.PullRequests => "Pull Requests",
        SearchCategory.PipelineRuns => "Pipeline Runs",
        _ => Category.ToString(),
    };
}

/// <summary>The in-memory caches the search scans. No API calls happen here.</summary>
public sealed class SearchSources
{
    public IReadOnlyList<IntuneDevice> Devices { get; init; } = Array.Empty<IntuneDevice>();
    /// <summary>The Reporting tab's ReportMate devices.</summary>
    public IReadOnlyList<ReportingDevice> ReportingDevices { get; init; } = Array.Empty<ReportingDevice>();
    public IReadOnlyList<SnipeAsset> Assets { get; init; } = Array.Empty<SnipeAsset>();
    public IReadOnlyList<TdxTicket> Tickets { get; init; } = Array.Empty<TdxTicket>();
    public IReadOnlyList<WorkItem> WorkItems { get; init; } = Array.Empty<WorkItem>();
    public IReadOnlyList<EntraUser> Users { get; init; } = Array.Empty<EntraUser>();
    public IReadOnlyList<EntraGroup> Groups { get; init; } = Array.Empty<EntraGroup>();
    /// <summary>The Development tab's open pull requests.</summary>
    public IReadOnlyList<UnifiedPullRequest> PullRequests { get; init; } = Array.Empty<UnifiedPullRequest>();
    /// <summary>GitHub inbox notifications; those about issues are searched.</summary>
    public IReadOnlyList<GitHubNotification> Notifications { get; init; } = Array.Empty<GitHubNotification>();
    /// <summary>The Development tab's recent commits, per repository.</summary>
    public IReadOnlyList<RepositoryCommits> Commits { get; init; } = Array.Empty<RepositoryCommits>();
    /// <summary>The Development tab's recent pipeline runs.</summary>
    public IReadOnlyList<PipelineRun> Runs { get; init; } = Array.Empty<PipelineRun>();
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

    /// <summary>A pull request or issue number typed bare, or after ! or #.</summary>
    public static int? ParseNumber(string? query)
    {
        var m = NumberPattern().Match(query?.Trim() ?? "");
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > 0 ? n : null;
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

        // ReportMate's devices open on the Reporting tab, by serial.
        Add(groups, SearchCategory.Reporting, sources.ReportingDevices, d =>
        {
            var hit = Best(SearchCategory.Reporting, text, d.Serial,
                string.IsNullOrWhiteSpace(d.Name) ? d.Serial : d.Name, Sub(d.Platform, d.User),
                ("Name", d.Name), ("Serial", d.Serial), ("Asset tag", d.AssetTag), ("User", d.User),
                ("Host", d.Hostname));
            return hit == null ? null : hit with { Link = FleetMateLink.Reporting.ForDevice(d.Serial).ToLink() };
        });

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

        var number = ParseNumber(q);
        var sha = q.Length >= 7 && FleetMateLink.IsSha(q) ? q : null;

        Add(groups, SearchCategory.PullRequests, sources.PullRequests, p =>
        {
            var title = $"!{p.Number} {p.Title}";
            var hit = number == p.Number
                ? new SearchHit(SearchCategory.PullRequests, title, Sub(Repo(p.Container, p.Repository), p.AuthorName),
                    $"Number: {p.Number}", p.Number.ToString())
                : Best(SearchCategory.PullRequests, text, p.Number.ToString(), title,
                    Sub(Repo(p.Container, p.Repository), p.AuthorName),
                    ("Title", p.Title), ("Repo", Repo(p.Container, p.Repository)), ("Author", p.AuthorName),
                    ("Branch", p.SourceBranch));
            return hit == null ? null : hit with { Link = PullRequestLink(p) };
        });

        Add(groups, SearchCategory.Issues, sources.Notifications.Where(n => n.SubjectType == "Issue"), n =>
        {
            if (!Uri.TryCreate(n.WebUrl, UriKind.Absolute, out var url)
                || TryParseWeb(url) is not FleetMateLink.GitHubIssue issue) return null;
            var title = $"#{issue.Number} {n.SubjectTitle}";
            var hit = number == issue.Number
                ? new SearchHit(SearchCategory.Issues, title, n.Repository, $"Number: {issue.Number}", issue.Number.ToString())
                : Best(SearchCategory.Issues, text, issue.Number.ToString(), title, n.Repository, ("Title", n.SubjectTitle));
            return hit == null ? null : hit with { Link = issue.ToLink() };
        });

        Add(groups, SearchCategory.Commits, sources.Commits.SelectMany(r => r.Commits.Select(c => (Repo: r, Commit: c))), x =>
        {
            var (repo, c) = x;
            if (!FleetMateLink.IsSha(c.Id)) return null;
            var message = FirstLine(c.Message);
            var title = $"{c.ShortSha} {message}";
            var hit = sha != null && c.Id.StartsWith(sha, StringComparison.OrdinalIgnoreCase)
                ? new SearchHit(SearchCategory.Commits, title, Sub(repo.DisplayName, c.AuthorName), $"SHA: {c.ShortSha}", c.Id)
                    { Rank = c.Id.Length == sha.Length ? 0 : 1 }
                : Best(SearchCategory.Commits, text, c.Id, title, Sub(repo.DisplayName, c.AuthorName),
                    ("Message", message), ("Author", c.AuthorName));
            return hit == null ? null : hit with { Link = new FleetMateLink.Commit(Source(repo.Source, repo.Container, repo.Repository), c.Id).ToLink() };
        });

        Add(groups, SearchCategory.PipelineRuns, sources.Runs, r =>
        {
            if (RunLink(r) is not { } link) return null;
            var title = $"{r.PipelineName} {r.RunNumber}".Trim();
            var hit = number == r.RunId
                ? new SearchHit(SearchCategory.PipelineRuns, title, Sub(r.Container, r.Branch), $"Run: {r.RunId}", r.RunId.ToString())
                : Best(SearchCategory.PipelineRuns, text, r.RunId.ToString(), title, Sub(r.Container, r.Branch),
                    ("Run number", r.RunNumber), ("Pipeline", r.PipelineName), ("Branch", r.Branch));
            return hit == null ? null : hit with { Link = link };
        });

        // An exact match leads: "148" or "#148" should put pull request !148
        // ahead of serials that merely contain 148, so Enter opens it.
        return groups
            .Select((group, index) => (group, index))
            .OrderBy(x => x.group.Hits.Count > 0 && x.group.Hits[0].Rank == 0 ? 0 : 1)
            .ThenBy(x => x.index)
            .Select(x => x.group)
            .ToList();
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
            Sub(w.Fields.WorkItemType, w.Fields.State), $"ID: {w.Id}", w.Id.ToString())
        { Link = new FleetMateLink.WorkItem(w.Id).ToLink() };

    private static void Add<T>(List<SearchGroup> groups, SearchCategory category, IEnumerable<T> items,
        Func<T, SearchHit?> match)
    {
        var hits = items.Select(match).OfType<SearchHit>()
            .Select(h => h.Link.Length > 0 ? h : h with { Link = KeyLink(h) })
            .ToList();
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

    /// <summary>The link for a hit whose key is the id its tab opens it by.</summary>
    private static string KeyLink(SearchHit hit) => hit.Category switch
    {
        SearchCategory.Devices => new FleetMateLink.Device(hit.Key).ToLink(),
        SearchCategory.Inventory when int.TryParse(hit.Key, out var id) => new FleetMateLink.Asset(id).ToLink(),
        SearchCategory.Tickets when int.TryParse(hit.Key, out var id) => new FleetMateLink.Ticket(id).ToLink(),
        SearchCategory.WorkItems when int.TryParse(hit.Key, out var id) => new FleetMateLink.WorkItem(id).ToLink(),
        SearchCategory.Users => new FleetMateLink.User(hit.Key).ToLink(),
        SearchCategory.Groups => new FleetMateLink.Group(hit.Key).ToLink(),
        _ => "",
    };

    private static FleetMateLink.LinkSource Source(PullRequestSource source, string container, string repo) =>
        source == PullRequestSource.GitHub
            ? new FleetMateLink.GitHub(container, repo)
            : new FleetMateLink.AzureDevOps(container, repo);

    private static string PullRequestLink(UnifiedPullRequest p) =>
        new FleetMateLink.PullRequest(Source(p.Source, p.Container, p.Repository), p.Number).ToLink();

    private static string? RunLink(PipelineRun r)
    {
        if (r.RunId is <= 0 or > int.MaxValue) return null;
        if (r.Source == PullRequestSource.AzureDevOps) return new FleetMateLink.AzureDevOpsRun(r.Container, (int)r.RunId).ToLink();
        return string.IsNullOrEmpty(r.Repository) ? null : new FleetMateLink.GitHubRun(r.Container, r.Repository, (int)r.RunId).ToLink();
    }

    private static FleetMateLink? TryParseWeb(Uri url)
    {
        try { return FleetMateLink.ParseWeb(url); }
        catch (FleetMateLinkException) { return null; }
    }

    private static string Repo(string container, string repo) =>
        string.IsNullOrEmpty(container) ? repo : $"{container}/{repo}";

    private static string FirstLine(string message)
    {
        var line = message.Split('\n', 2)[0].Trim();
        return line.Length == 0 ? "(no message)" : line;
    }

    private static string? Sub(params string?[] parts)
    {
        var shown = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return shown.Count == 0 ? null : string.Join(" · ", shown);
    }

    private static string Clip(string value) => value.Length <= 40 ? value : value[..39] + "…";

    [GeneratedRegex(@"^[!#]?(\d+)$")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"^(?:[A-Za-z]+#|#)?(\d+)$")]
    private static partial Regex WorkItemIdPattern();
}
