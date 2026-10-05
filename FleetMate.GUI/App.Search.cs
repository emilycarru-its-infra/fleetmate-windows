using FleetMate.Core.Services.Search;

namespace FleetMate.GUI;

/// <summary>What search scans: the caches every tab already keeps. No API calls.</summary>
public partial class App
{
    /// <summary>A snapshot of the loaded data, for <see cref="GlobalSearch.Search"/>.</summary>
    public SearchSources BuildSearchSources() => new()
    {
        Devices = CachedDevices.ToList(),
        Assets = CachedAssets.ToList(),
        Tickets = CachedTickets.ToList(),
        WorkItems = CachedWorkItems.ToList(),
        Users = CachedUsers.ToList(),
        Groups = CachedGroups.ToList(),
        PullRequests = DevelopmentPullRequests?.PullRequests.ToList() ?? new(),
        Notifications = Inbox.Notifications.ToList(),
        Commits = DevelopmentCommits?.ToList() ?? new(),
        Runs = DevelopmentRuns?.ToList() ?? new(),
    };
}
