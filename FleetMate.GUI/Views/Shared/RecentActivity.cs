using System.Globalization;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Inventory;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Models.Tickets;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// One row in the toolbar Recent Activity popover — the macOS ActivityItem.
/// Each item belongs to exactly one tab, and the popover shows only the
/// current tab's items.
/// </summary>
public sealed record ActivityItem
{
    public required string Icon { get; init; }
    public required string Name { get; init; }

    /// <summary>The status pill.</summary>
    public string Detail { get; init; } = "";

    /// <summary>Who or where — the middle column.</summary>
    public string Context { get; init; } = "";

    public required DateTime Timestamp { get; init; }

    /// <summary>The filter key: the tab tag the item belongs to.</summary>
    public required string Tab { get; init; }

    public string? DeviceId { get; init; }
    public int? TicketId { get; init; }
    public int? AssetId { get; init; }
    public int? WorkItemId { get; init; }

    /// <summary>Development: the pull request a comment belongs to.</summary>
    public UnifiedPullRequest? PullRequest { get; init; }

    /// <summary>Relative time, in the feed's own units.</summary>
    public string Time => Relative(Timestamp, DateTime.UtcNow);

    public static string Relative(DateTime when, DateTime now)
    {
        var span = now - when.ToUniversalTime();
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h ago";
        return $"{(int)span.TotalDays}d ago";
    }
}

/// <summary>
/// Builds the Recent Activity feed for one tab from data the app already
/// holds, newest first. Windows: tickets, work items and device syncs in the
/// last 24 hours; assets and the Snipe-IT activity log in the last 7 days;
/// recent pull request comments for Development; Manage and Identity empty.
/// </summary>
public static class RecentActivityFeed
{
    public const string GlyphTicket = "";
    public const string GlyphWorkItem = "";
    public const string GlyphDevice = "";
    public const string GlyphAsset = "";
    public const string GlyphActivity = "";
    public const string GlyphComment = "";

    public sealed class Sources
    {
        public IReadOnlyList<TdxTicket> Tickets { get; init; } = Array.Empty<TdxTicket>();
        public IReadOnlyList<WorkItem> WorkItems { get; init; } = Array.Empty<WorkItem>();
        public IReadOnlyList<IntuneDevice> Devices { get; init; } = Array.Empty<IntuneDevice>();
        public IReadOnlyList<SnipeAsset> Assets { get; init; } = Array.Empty<SnipeAsset>();
        public IReadOnlyList<SnipeActivity> SnipeActivity { get; init; } = Array.Empty<SnipeActivity>();
        public PullRequestQueue? PullRequests { get; init; }
    }

    public static List<ActivityItem> Build(string tab, Sources sources, DateTime now, bool hideMine = false)
    {
        var dayAgo = now.AddHours(-24);
        var weekAgo = now.AddDays(-7);

        IEnumerable<ActivityItem> items = tab switch
        {
            "Tickets" => sources.Tickets
                .Where(t => t.ModifiedDate is { } d && d.ToUniversalTime() > dayAgo)
                .Select(t => new ActivityItem
                {
                    Icon = GlyphTicket, Name = $"#{t.Id}  {t.Title}", Detail = t.StatusName ?? "",
                    Context = t.RequestorName ?? "", Timestamp = t.ModifiedDate!.Value, Tab = tab, TicketId = t.Id,
                }),

            // The work item cache is the whole loaded set, the operator's own
            // included, so de-duplicating by id is all "all plus mine" needs.
            "Projects" => sources.WorkItems
                .Where(w => w.ChangedDate is { } d && d.ToUniversalTime() > dayAgo)
                .GroupBy(w => w.Id).Select(g => g.First())
                .Select(w => new ActivityItem
                {
                    Icon = GlyphWorkItem, Name = $"#{w.Id}  {w.Title}", Detail = w.State,
                    Context = string.Join(" › ", (w.AreaPath ?? "").Split('\\', StringSplitOptions.RemoveEmptyEntries)),
                    Timestamp = w.ChangedDate!.Value, Tab = tab, WorkItemId = w.Id,
                }),

            "Devices" => sources.Devices
                .Where(d => d.LastSyncDateTime is { } s && s.ToUniversalTime() > dayAgo)
                .Select(d => new ActivityItem
                {
                    Icon = GlyphDevice, Name = d.DeviceName ?? "device", Detail = "synced",
                    Context = !string.IsNullOrEmpty(d.UserDisplayName) ? d.UserDisplayName! : d.OperatingSystem ?? "",
                    Timestamp = d.LastSyncDateTime!.Value, Tab = tab, DeviceId = d.Id,
                }),

            "Inventory" => Inventory(sources, weekAgo, tab),

            "Development" => Development(sources.PullRequests, hideMine, tab),

            _ => Enumerable.Empty<ActivityItem>(),
        };

        return items.OrderByDescending(i => i.Timestamp).ToList();
    }

    private static IEnumerable<ActivityItem> Inventory(Sources sources, DateTime since, string tab)
    {
        foreach (var a in sources.Assets)
        {
            if (ParseSnipeDate(a.UpdatedAt?.DateTime) is not { } when || when.ToUniversalTime() <= since) continue;
            yield return new ActivityItem
            {
                Icon = GlyphAsset, Name = a.DisplayName, Detail = "updated",
                Context = a.StatusLabel?.Name ?? "", Timestamp = when, Tab = tab, AssetId = a.Id,
            };
        }

        foreach (var entry in sources.SnipeActivity)
        {
            if (ParseSnipeDate(entry.CreatedAt?.DateTime) is not { } when || when.ToUniversalTime() <= since) continue;
            var isAsset = string.Equals(entry.Item?.Type, "asset", StringComparison.OrdinalIgnoreCase);
            yield return new ActivityItem
            {
                Icon = GlyphActivity, Name = entry.Item?.Name ?? "item", Detail = entry.ActionType ?? "activity",
                Context = entry.Admin?.Name ?? "", Timestamp = when, Tab = tab,
                AssetId = isAsset ? entry.Item?.Id : null,
            };
        }
    }

    /// <summary>Recent comments and reviews across the loaded pull requests — the old Activity sidebar.</summary>
    private static IEnumerable<ActivityItem> Development(PullRequestQueue? queue, bool hideMine, string tab)
    {
        if (queue == null) yield break;

        foreach (var pr in queue.PullRequests)
        foreach (var comment in pr.RecentComments)
        {
            if (hideMine && queue.ViewerNames.Contains(comment.AuthorName)) continue;
            if (comment.Date is not { } when) continue;

            yield return new ActivityItem
            {
                Icon = GlyphComment,
                Name = $"{pr.Repository} {pr.Reference}  {pr.Title}",
                Detail = comment.IsSystem ? comment.Body : "commented",
                Context = comment.AuthorName,
                Timestamp = when, Tab = tab, PullRequest = pr,
            };
        }
    }

    internal static DateTime? ParseSnipeDate(string? value) =>
        string.IsNullOrEmpty(value)
            ? null
            : DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed) ? parsed : null;
}
