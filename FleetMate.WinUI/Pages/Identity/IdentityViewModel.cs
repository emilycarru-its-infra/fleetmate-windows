using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FleetMate.Core.Identity;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Identity;
using Serilog;

namespace FleetMate.WinUI.Pages.Identity;

/// <summary>
/// The Identity tab: device groups and their members, and the Users search
/// with its inspector. The directory is <see cref="IIdentityDirectory"/>
/// (Graph, or fictional in demo mode); the presentation rules are
/// <see cref="EntraUserProfile"/>'s and the search rule
/// <see cref="IdentitySearch"/>'s, shared with the WPF app.
/// </summary>
public sealed partial class IdentityViewModel : ObservableObject
{
    private readonly IIdentityDirectory? _directory;

    public IdentityViewModel(IIdentityDirectory? directory) => _directory = directory;

    public bool IsConfigured => _directory != null;

    public string NotConfiguredMessage => "Microsoft Graph is not configured. Set the tenant ID in Settings › Authentication.";

    // MARK: - Groups

    private readonly List<GroupRow> _allGroups = new();

    /// <summary>The groups the filter leaves, in name order.</summary>
    public ObservableCollection<GroupRow> Groups { get; } = new();

    [ObservableProperty] private bool _groupsLoading;
    [ObservableProperty] private string? _groupsError;
    [ObservableProperty] private string _groupFilter = "";
    [ObservableProperty] private string _groupsCount = "";
    [ObservableProperty] private bool _groupsEmpty;
    [ObservableProperty] private string _groupsEmptyTitle = "";
    [ObservableProperty] private string _groupsEmptyText = "";

    partial void OnGroupFilterChanged(string value) => ApplyGroupFilter();

    [RelayCommand]
    public async Task LoadGroupsAsync()
    {
        if (_directory == null) return;
        GroupsLoading = true;
        GroupsError = null;
        try
        {
            var groups = await _directory.DeviceGroupsAsync();
            _allGroups.Clear();
            _allGroups.AddRange(groups.Select(g => new GroupRow(this, g)));
            ApplyGroupFilter();
            // Every row shows its device count, and a device name finds its
            // groups, so members load in the background (as the Mac app warms them).
            _ = LoadAllMembersAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[identity] Could not load device groups");
            GroupsError = $"Could not load device groups: {ex.Message}";
        }
        finally
        {
            GroupsLoading = false;
            UpdateGroupsEmpty();
        }
    }

    /// <summary>
    /// Show the groups that match by name, description or a loaded member's
    /// name. While filtering, a group with loaded members opens to show the
    /// matching devices, as on FleetMate for Mac.
    /// </summary>
    internal void ApplyGroupFilter()
    {
        var filtering = !string.IsNullOrWhiteSpace(GroupFilter);
        Groups.Clear();
        foreach (var row in _allGroups.Where(r => IdentitySearch.GroupMatches(r.Group, GroupFilter, r.AllMembers)))
        {
            row.ShowMembers(GroupFilter, openForFilter: filtering && row.AllMembers is { Count: > 0 });
            Groups.Add(row);
        }
        GroupsCount = _allGroups.Count == 0 ? "" : IdentitySearch.CountText(Groups.Count, _allGroups.Count);
        UpdateGroupsEmpty();
    }

    private void UpdateGroupsEmpty()
    {
        GroupsEmpty = !GroupsLoading && GroupsError == null && Groups.Count == 0 && IsConfigured;
        (GroupsEmptyTitle, GroupsEmptyText) = _allGroups.Count == 0
            ? ("No device groups", "No groups were found with the Devices- prefix.")
            : ("No matches", $"No group or member device matches “{GroupFilter.Trim()}”.");
    }

    /// <summary>Load every group's members, a few at a time, refiltering as they arrive.</summary>
    private async Task LoadAllMembersAsync()
    {
        using var gate = new SemaphoreSlim(4);
        var rows = _allGroups.ToList();
        await Task.WhenAll(rows.Select(async row =>
        {
            await gate.WaitAsync();
            try { await LoadMembersAsync(row); }
            finally { gate.Release(); }
        }));
        if (!string.IsNullOrWhiteSpace(GroupFilter)) ApplyGroupFilter();
    }

    internal async Task LoadMembersAsync(GroupRow row)
    {
        if (_directory == null || row.AllMembers != null || row.IsLoading) return;
        row.IsLoading = true;
        try
        {
            row.SetMembers(await _directory.GroupDevicesAsync(row.Group.Id), GroupFilter);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[identity] Could not load members of {Group}", row.Group.DisplayName);
            row.LoadError = $"Could not load members: {ex.Message}";
            row.SetMembers(Array.Empty<IntuneDevice>(), GroupFilter);
        }
        finally
        {
            row.IsLoading = false;
        }
    }

    // MARK: - Users

    public ObservableCollection<UserRow> Users { get; } = new();

    [ObservableProperty] private string _userQuery = "";
    [ObservableProperty] private bool _usersLoading;
    [ObservableProperty] private string _usersMessage = "Enter an address or a name in the search field, then press Enter.";
    [ObservableProperty] private UserRow? _selectedUser;

    [RelayCommand]
    public async Task SearchUsersAsync()
    {
        if (_directory == null || string.IsNullOrWhiteSpace(UserQuery)) return;
        UsersLoading = true;
        Users.Clear();
        try
        {
            var found = await IdentitySearch.FindUsersAsync(_directory, UserQuery);
            foreach (var user in found) Users.Add(new UserRow(user));
            UsersMessage = found.Count == 0 ? $"No users match “{UserQuery.Trim()}”." : "";
            // A single hit is the common case; open the first result.
            SelectedUser = Users.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[identity] User search failed");
            UsersMessage = $"Search error: {ex.Message}";
        }
        finally
        {
            UsersLoading = false;
        }
    }

    // MARK: - Inspector

    public ObservableCollection<IntuneDevice> UserDevices { get; } = new();
    public ObservableCollection<EntraGroup> UserGroups { get; } = new();

    [ObservableProperty] private string _devicesMessage = "";
    [ObservableProperty] private string _groupsMessage = "";
    [ObservableProperty] private string _groupToAdd = "";
    [ObservableProperty] private string? _actionError;

    public bool HasSelectedUser => SelectedUser != null;

    partial void OnSelectedUserChanged(UserRow? value)
    {
        OnPropertyChanged(nameof(HasSelectedUser));
        ActionError = null;
        _ = LoadRelationsAsync(value);
    }

    private async Task LoadRelationsAsync(UserRow? row)
    {
        UserDevices.Clear();
        UserGroups.Clear();
        if (row == null || _directory == null) return;
        DevicesMessage = "Loading devices…";
        GroupsMessage = "Loading groups…";
        var upn = row.User.UserPrincipalName;
        try
        {
            var devicesTask = _directory.UserDevicesAsync(upn);
            var groupsTask = row.User.MemberOf is { Count: > 0 } cached
                ? Task.FromResult<IReadOnlyList<EntraGroup>>(cached)
                : _directory.UserGroupsAsync(upn);
            await Task.WhenAll(devicesTask, groupsTask);
            // The selection can move while these are in flight.
            if (SelectedUser?.User.UserPrincipalName != upn) return;
            foreach (var device in devicesTask.Result) UserDevices.Add(device);
            foreach (var group in groupsTask.Result) UserGroups.Add(group);
            DevicesMessage = UserDevices.Count == 0 ? "No Intune devices are assigned to this user." : "";
            GroupsMessage = UserGroups.Count == 0 ? "This user is not a member of any groups." : "";
        }
        catch (Exception ex)
        {
            DevicesMessage = $"Could not load devices: {ex.Message}";
            GroupsMessage = $"Could not load groups: {ex.Message}";
        }
    }

    /// <summary>Enable or disable the selected account; the page confirms first.</summary>
    public async Task<bool> ToggleAccountAsync()
    {
        if (_directory == null || SelectedUser is not { } row) return false;
        var enable = row.User.AccountEnabled == false;
        if (!await _directory.SetAccountEnabledAsync(row.User.Id, enable))
        {
            ActionError = "The account update failed. See the log for detail.";
            return false;
        }
        row.User.AccountEnabled = enable;
        row.Refresh();
        return true;
    }

    [RelayCommand]
    private async Task AddToGroupAsync()
    {
        if (_directory == null || SelectedUser is not { } row || string.IsNullOrWhiteSpace(GroupToAdd)) return;
        var group = GroupToAdd.Trim();
        if (!await _directory.AddToGroupAsync(group, row.User.Id))
        {
            ActionError = $"Could not add {row.DisplayName} to '{group}'.";
            return;
        }
        GroupToAdd = "";
        row.User.MemberOf = new List<EntraGroup>();
        await LoadRelationsAsync(row);
    }

    /// <summary>Remove the selected user from <paramref name="group"/>; the page confirms first.</summary>
    public async Task RemoveFromGroupAsync(EntraGroup group)
    {
        if (_directory == null || SelectedUser is not { } row) return;
        if (!await _directory.RemoveFromGroupAsync(group.Id, row.User.Id))
        {
            ActionError = $"Could not remove {row.DisplayName} from '{group.DisplayName}'.";
            return;
        }
        row.User.MemberOf = new List<EntraGroup>();
        await LoadRelationsAsync(row);
    }
}

/// <summary>A device group: its header, and its members once opened.</summary>
public sealed partial class GroupRow(IdentityViewModel owner, EntraGroup group) : ObservableObject
{
    public EntraGroup Group { get; } = group;
    public string Name => Group.DisplayName;
    public string Id => Group.Id;
    public IReadOnlyList<string> Badges { get; } = IdentitySearch.Badges(group);
    public string Glyph => Group.SecurityEnabled == true ? "\uE83D" : "\uE716";

    /// <summary>Every member, once loaded; null until the group is first opened.</summary>
    public IReadOnlyList<IntuneDevice>? AllMembers { get; private set; }

    /// <summary>The members showing: all of them, or those matching the filter.</summary>
    public ObservableCollection<DeviceRow> Members { get; } = new();

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Chevron))] private bool _isExpanded;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _loadError;
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private string _membersHeader = "";
    [ObservableProperty] private bool _noMembers;

    public string Chevron => IsExpanded ? "\uE70D" : "\uE76C";

    [RelayCommand]
    private async Task ToggleAsync()
    {
        IsExpanded = !IsExpanded;
        if (IsExpanded) await owner.LoadMembersAsync(this);
    }

    [RelayCommand]
    private void CopyId()
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(Id);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    internal void SetMembers(IReadOnlyList<IntuneDevice> members, string? filter)
    {
        AllMembers = members;
        CountText = $"{members.Count} device{(members.Count == 1 ? "" : "s")}";
        ShowMembers(filter, openForFilter: false);
    }

    internal void ShowMembers(string? filter, bool openForFilter)
    {
        if (openForFilter) IsExpanded = true;
        Members.Clear();
        if (AllMembers == null) return;
        foreach (var device in IdentitySearch.VisibleMembers(AllMembers, filter)) Members.Add(new DeviceRow(device, filter));
        MembersHeader = Members.Count == AllMembers.Count
            ? $"Members ({AllMembers.Count})"
            : $"Members ({Members.Count} of {AllMembers.Count})";
        NoMembers = AllMembers.Count == 0;
    }
}

/// <summary>A member device: its name, OS and platform icon; highlighted when it matches the filter.</summary>
public sealed class DeviceRow(IntuneDevice device, string? filter)
{
    public string Name => device.DeviceName;
    public string OsText => string.Join(" ", new[] { device.OperatingSystem, device.OsVersion }.Where(p => !string.IsNullOrWhiteSpace(p)));
    public string Glyph => IdentitySearch.Platform(device.OperatingSystem) switch
    {
        "Windows" => "\uE977",
        "macOS" => "\uE7F4",
        "Mobile" => "\uE8EA",
        _ => "\uE7F8",
    };
    public bool IsMatch { get; } = !string.IsNullOrWhiteSpace(filter)
        && device.DeviceName.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>A user in the results list and the inspector.</summary>
public sealed partial class UserRow(EntraUser user) : ObservableObject
{
    public EntraUser User { get; } = user;
    public string DisplayName => User.DisplayName;
    public string UserPrincipalName => User.UserPrincipalName;
    public string Initials => EntraUserProfile.Initials(User.DisplayName);
    public string Subtitle => EntraUserProfile.Subtitle(User);
    public AccountState State => EntraUserProfile.State(User);
    public string StateText => EntraUserProfile.StateText(State);
    public string ToggleLabel => EntraUserProfile.ToggleLabel(User);
    public IReadOnlyList<UserPropertySection> Sections =>
        EntraUserProfile.Sections(User).Where(s => s.HasValues).ToList();

    public void Refresh()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(ToggleLabel));
        OnPropertyChanged(nameof(Sections));
    }
}
