using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Identity;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Devices;

namespace FleetMate.Core.Identity;

/// <summary>
/// What the Identity tab reads and changes: device groups and their members,
/// users, their devices and groups, account state and membership. Microsoft
/// Graph is the live directory (<see cref="GraphIdentityDirectory"/>); demo
/// mode supplies a fictional one.
/// </summary>
public interface IIdentityDirectory
{
    /// <summary>The device groups the tab lists (named Devices-…).</summary>
    Task<IReadOnlyList<EntraGroup>> DeviceGroupsAsync();

    Task<IReadOnlyList<IntuneDevice>> GroupDevicesAsync(string groupId);

    Task<EntraUser?> UserAsync(string upnOrId);

    Task<IReadOnlyList<EntraUser>> SearchUsersAsync(string query);

    Task<IReadOnlyList<IntuneDevice>> UserDevicesAsync(string upn);

    Task<IReadOnlyList<EntraGroup>> UserGroupsAsync(string upn);

    Task<bool> SetAccountEnabledAsync(string userId, bool enabled);

    Task<bool> AddToGroupAsync(string groupNameOrId, string userId);

    Task<bool> RemoveFromGroupAsync(string groupId, string userId);
}

/// <summary>The live directory: Microsoft Graph.</summary>
public sealed class GraphIdentityDirectory(GraphService graph) : IIdentityDirectory
{
    public async Task<IReadOnlyList<EntraGroup>> DeviceGroupsAsync() =>
        (await graph.SearchGroupsAsync("Devices-", DeviceGroupFetch.Limit)).OrderBy(g => g.DisplayName).ToList();

    public async Task<IReadOnlyList<IntuneDevice>> GroupDevicesAsync(string groupId) =>
        (await graph.GetGroupDevicesAsync(groupId)).OrderBy(d => d.DeviceName).ToList();

    public Task<EntraUser?> UserAsync(string upnOrId) => graph.GetUserAsync(upnOrId, includeGroups: true);

    public async Task<IReadOnlyList<EntraUser>> SearchUsersAsync(string query) => await graph.SearchUsersAsync(query);

    public async Task<IReadOnlyList<IntuneDevice>> UserDevicesAsync(string upn) => await graph.GetUserDevicesAsync(upn);

    public async Task<IReadOnlyList<EntraGroup>> UserGroupsAsync(string upn) => await graph.GetUserGroupsAsync(upn);

    public Task<bool> SetAccountEnabledAsync(string userId, bool enabled) => graph.SetUserAccountEnabledAsync(userId, enabled);

    public Task<bool> AddToGroupAsync(string groupNameOrId, string userId) => graph.AddGroupMemberAsync(groupNameOrId, userId);

    public Task<bool> RemoveFromGroupAsync(string groupId, string userId) => graph.RemoveGroupMemberAsync(groupId, userId);
}

/// <summary>How the Users search finds people, in both apps.</summary>
public static class IdentitySearch
{
    /// <summary>
    /// An address or object id is looked up exactly first; anything else, or
    /// an exact lookup that finds no one, falls back to the fuzzy search.
    /// </summary>
    public static async Task<IReadOnlyList<EntraUser>> FindUsersAsync(IIdentityDirectory directory, string query)
    {
        query = query.Trim();
        if (query.Length == 0) return Array.Empty<EntraUser>();
        if (IsExact(query) && await directory.UserAsync(query) is { } exact) return new[] { exact };
        return await directory.SearchUsersAsync(query);
    }

    /// <summary>An address or an object id, which names one user.</summary>
    public static bool IsExact(string query) => query.Contains('@') || Guid.TryParse(query, out _);

    /// <summary>Whether a group shows for the groups filter: its name or description contains the text.</summary>
    public static bool GroupMatches(EntraGroup group, string? filter) =>
        string.IsNullOrWhiteSpace(filter)
        || group.DisplayName.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase)
        || (group.Description?.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);
}
