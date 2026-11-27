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

    /// <summary>
    /// Whether a group shows for the groups filter: its name or description
    /// contains the text, or (as on FleetMate for Mac) one of its loaded member
    /// devices' names does, so searching a device name finds every group it is in.
    /// </summary>
    public static bool GroupMatches(EntraGroup group, string? filter, IEnumerable<IntuneDevice>? members = null)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        var text = filter.Trim();
        return group.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase)
            || (group.Description?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
            || (members?.Any(d => d.DeviceName.Contains(text, StringComparison.OrdinalIgnoreCase)) ?? false);
    }

    /// <summary>
    /// The members a group shows while filtering: the devices whose names
    /// match, or all of them when the group matched by its own name.
    /// </summary>
    public static IReadOnlyList<IntuneDevice> VisibleMembers(IReadOnlyList<IntuneDevice> members, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return members;
        var matching = members.Where(d => d.DeviceName.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return matching.Count == 0 ? members : matching;
    }

    /// <summary>The kind badges a group carries: Security, Mail, M365, Dynamic.</summary>
    public static IReadOnlyList<string> Badges(EntraGroup group)
    {
        var badges = new List<string>();
        if (group.SecurityEnabled == true) badges.Add("Security");
        if (group.MailEnabled == true) badges.Add("Mail");
        if (group.IsM365Group) badges.Add("M365");
        if (group.IsDynamic) badges.Add("Dynamic");
        return badges;
    }

    /// <summary>A device's platform family for its icon: Windows, macOS, Mobile, or Other.</summary>
    public static string Platform(string? operatingSystem) => operatingSystem?.Trim().ToLowerInvariant() switch
    {
        "windows" => "Windows",
        "macos" or "mac os x" => "macOS",
        "ios" or "ipados" or "android" or "androidenterprise" => "Mobile",
        _ => "Other",
    };

    /// <summary>"3 of 12" when filtering, "12 groups" otherwise.</summary>
    public static string CountText(int shown, int total) =>
        shown == total ? $"{total} group{(total == 1 ? "" : "s")}" : $"{shown} of {total}";
}
