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
    private IReadOnlyList<EntraGroup> _allGroups = Array.Empty<EntraGroup>();

    public IdentityViewModel(IIdentityDirectory? directory) => _directory = directory;

    public bool IsConfigured => _directory != null;

    public string NotConfiguredMessage => "Microsoft Graph is not configured. Set the tenant ID in Settings › Authentication.";

    // MARK: - Groups

    public ObservableCollection<EntraGroup> Groups { get; } = new();

    [ObservableProperty] private bool _groupsLoading;
    [ObservableProperty] private string? _groupsError;
    [ObservableProperty] private string _groupFilter = "";

    partial void OnGroupFilterChanged(string value) => ApplyGroupFilter();

    [RelayCommand]
    public async Task LoadGroupsAsync()
    {
        if (_directory == null) return;
        GroupsLoading = true;
        GroupsError = null;
        try
        {
            _allGroups = await _directory.DeviceGroupsAsync();
            ApplyGroupFilter();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[identity] Could not load device groups");
            GroupsError = $"Could not load device groups: {ex.Message}";
        }
        finally
        {
            GroupsLoading = false;
        }
    }

    private void ApplyGroupFilter()
    {
        Groups.Clear();
        foreach (var group in _allGroups.Where(g => IdentitySearch.GroupMatches(g, GroupFilter))) Groups.Add(group);
    }

    public Task<IReadOnlyList<IntuneDevice>> GroupDevicesAsync(string groupId) =>
        _directory?.GroupDevicesAsync(groupId) ?? Task.FromResult<IReadOnlyList<IntuneDevice>>(Array.Empty<IntuneDevice>());

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
            UsersMessage = found.Count == 0 ? $"No users found matching '{UserQuery.Trim()}'." : "";
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
