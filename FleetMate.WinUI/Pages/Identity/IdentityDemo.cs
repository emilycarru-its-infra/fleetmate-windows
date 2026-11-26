using FleetMate.Core.Identity;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Identity;

namespace FleetMate.WinUI.Pages.Identity;

/// <summary>
/// A fictional directory for demo mode and the QA capture. Every name,
/// address, id and serial here is made up; addresses use example.edu.
/// Changes stay in memory.
/// </summary>
internal sealed class IdentityDemo : IIdentityDirectory
{
    private readonly List<EntraGroup> _groups = new()
    {
        Group("1", "Devices-Lab-North", "Teaching lab, north wing"),
        Group("2", "Devices-Lab-South", "Teaching lab, south wing"),
        Group("3", "Devices-Library", "Library loan laptops"),
        Group("4", "Devices-Studio", "Media studio workstations"),
        Group("5", "Devices-Staff", "Staff laptops"),
    };

    private readonly List<EntraUser> _users = new()
    {
        User("u1", "Avery Kim", "Coordinator", "Student Services", true),
        User("u2", "Priya Shah", "Technician", "Media Studio", true),
        User("u3", "Noah Fischer", "Instructor", "Design", true),
        User("u4", "Lena Ortiz", "Assistant", "Library", false),
        User("u5", "Omar Haddad", "Instructor", "Illustration", true),
    };

    private readonly Dictionary<string, List<string>> _membership = new()
    {
        ["u1"] = new() { "5" },
        ["u2"] = new() { "4", "5" },
        ["u3"] = new() { "1", "5" },
        ["u4"] = new() { "3" },
        ["u5"] = new() { "2", "5" },
    };

    public Task<IReadOnlyList<EntraGroup>> DeviceGroupsAsync() =>
        Task.FromResult<IReadOnlyList<EntraGroup>>(_groups.OrderBy(g => g.DisplayName).ToList());

    public Task<IReadOnlyList<IntuneDevice>> GroupDevicesAsync(string groupId) =>
        Task.FromResult<IReadOnlyList<IntuneDevice>>(Enumerable.Range(1, 3 + groupId.Length % 3)
            .Select(i => Device($"{GroupCode(groupId)}-{i:00}", i % 2 == 0 ? "macOS" : "Windows", i))
            .ToList());

    public Task<EntraUser?> UserAsync(string upnOrId) =>
        Task.FromResult(_users.FirstOrDefault(u =>
            u.Id == upnOrId || u.UserPrincipalName.Equals(upnOrId, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<EntraUser>> SearchUsersAsync(string query) =>
        Task.FromResult<IReadOnlyList<EntraUser>>(_users
            .Where(u => u.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || u.UserPrincipalName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList());

    public Task<IReadOnlyList<IntuneDevice>> UserDevicesAsync(string upn) =>
        Task.FromResult<IReadOnlyList<IntuneDevice>>(new[]
        {
            Device($"LT-{upn[0]}{upn.Length:00}", "Windows", 1),
            Device($"MB-{upn[0]}{upn.Length:00}", "macOS", 2),
        });

    public Task<IReadOnlyList<EntraGroup>> UserGroupsAsync(string upn)
    {
        var user = _users.FirstOrDefault(u => u.UserPrincipalName == upn);
        var ids = user != null && _membership.TryGetValue(user.Id, out var list) ? list : new List<string>();
        return Task.FromResult<IReadOnlyList<EntraGroup>>(_groups.Where(g => ids.Contains(g.Id)).ToList());
    }

    public Task<bool> SetAccountEnabledAsync(string userId, bool enabled)
    {
        if (_users.FirstOrDefault(u => u.Id == userId) is { } user) user.AccountEnabled = enabled;
        return Task.FromResult(true);
    }

    public Task<bool> AddToGroupAsync(string groupNameOrId, string userId)
    {
        var group = _groups.FirstOrDefault(g => g.Id == groupNameOrId
            || g.DisplayName.Equals(groupNameOrId, StringComparison.OrdinalIgnoreCase));
        if (group == null) return Task.FromResult(false);
        var list = _membership.TryGetValue(userId, out var existing) ? existing : _membership[userId] = new();
        if (!list.Contains(group.Id)) list.Add(group.Id);
        return Task.FromResult(true);
    }

    public Task<bool> RemoveFromGroupAsync(string groupId, string userId)
    {
        if (_membership.TryGetValue(userId, out var list)) list.Remove(groupId);
        return Task.FromResult(true);
    }

    private static string GroupCode(string id) => id switch { "1" => "LABN", "2" => "LABS", "3" => "LIB", "4" => "STU", _ => "STF" };

    private static EntraGroup Group(string id, string name, string description) =>
        new() { Id = $"00000000-0000-0000-0000-00000000000{id}", DisplayName = name, Description = description };

    private static EntraUser User(string id, string name, string title, string department, bool enabled)
    {
        var upn = name.ToLowerInvariant().Replace(' ', '.') + "@example.edu";
        var parts = name.Split(' ');
        return new EntraUser
        {
            Id = id,
            DisplayName = name,
            UserPrincipalName = upn,
            Mail = upn,
            GivenName = parts[0],
            Surname = parts[^1],
            JobTitle = title,
            Department = department,
            CompanyName = "Example College",
            OfficeLocation = "Building A",
            AccountEnabled = enabled,
        };
    }

    private static IntuneDevice Device(string name, string os, int n) => new()
    {
        Id = $"demo-{name}",
        DeviceName = name,
        OperatingSystem = os,
        OsVersion = os == "macOS" ? "15.1" : "10.0.26100",
        SerialNumber = $"DEMO{n:0000}",
    };
}
