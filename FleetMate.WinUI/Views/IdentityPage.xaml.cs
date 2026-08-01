using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using FleetMate.Core.Models.Identity;
using FleetMate.WinUI.ViewModels;

namespace FleetMate.WinUI.Views;

public sealed partial class IdentityPage : Page
{
    private enum Mode { Groups, Users }
    private Mode _mode = Mode.Groups;
    private bool _loaded;

    private List<GroupRowViewModel> _groups = new();
    private List<UserRowViewModel> _users = new();
    private EntraGroup? _currentGroup;
    private EntraUser? _currentUser;

    private sealed record MemberRow(string Label, string UserId);

    public IdentityPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        await LoadAsync();
    }

    private void ModeBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        _mode = sender.SelectedItem == UsersTab ? Mode.Users : Mode.Groups;
        GroupList.Visibility = _mode == Mode.Groups ? Visibility.Visible : Visibility.Collapsed;
        UserList.Visibility = _mode == Mode.Users ? Visibility.Visible : Visibility.Collapsed;
        ClearDetail();
        if (_loaded) _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var graph = App.Current.GraphService;
        if (graph == null)
        {
            CountText.Text = "Microsoft Graph is not configured — add credentials in Settings.";
            return;
        }

        var query = SearchBox.Text?.Trim() ?? "";
        RefreshButton.IsEnabled = false;
        LoadingRing.IsActive = true;
        try
        {
            if (_mode == Mode.Groups)
            {
                var groups = await graph.SearchGroupsAsync(query, 200);
                _groups = groups.Select(g => new GroupRowViewModel(g)).ToList();
            }
            else
            {
                if (string.IsNullOrEmpty(query))
                {
                    _users = new();
                    UserList.ItemsSource = null;
                    CountText.Text = "Type a name or UPN and press Enter to search users.";
                    return;
                }
                var users = await graph.SearchUsersAsync(query, 100);
                _users = users.Select(u => new UserRowViewModel(u)).ToList();
            }
            ApplyFilter();
        }
        catch (Exception ex)
        {
            CountText.Text = $"Failed to load: {ex.Message}";
        }
        finally
        {
            LoadingRing.IsActive = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private void ApplyFilter()
    {
        var q = SearchBox.Text?.Trim() ?? "";
        if (_mode == Mode.Groups)
        {
            var rows = (string.IsNullOrEmpty(q) ? _groups : _groups.Where(r => r.Matches(q)))
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            GroupList.ItemsSource = rows;
            CountText.Text = Count(rows.Count, _groups.Count, "groups");
        }
        else
        {
            var rows = (string.IsNullOrEmpty(q) ? _users : _users.Where(r => r.Matches(q)))
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            UserList.ItemsSource = rows;
            CountText.Text = Count(rows.Count, _users.Count, "users");
        }
    }

    private static string Count(int shown, int total, string noun) =>
        total == 0 ? $"No {noun}." : shown == total ? $"{total} {noun}" : $"{shown} of {total} {noun}";

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Client-side narrowing over what's already loaded (server re-query is on Enter / Search).
        if ((_mode == Mode.Groups && _groups.Count > 0) || (_mode == Mode.Users && _users.Count > 0))
            ApplyFilter();
    }

    private async void SearchBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) await LoadAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    // MARK: - Group detail

    private async void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupList.SelectedItem is not GroupRowViewModel row) { ClearDetail(); return; }
        var g = row.Group;
        _currentGroup = g;
        _currentUser = null;
        ShowDetailHeader(row.Name, g.Mail ?? g.Description ?? "");

        DetailRows.Children.Clear();
        AddRow("Type", row.TypeLabel);
        AddRow("Mail", g.Mail ?? "—");
        AddRow("Security", g.SecurityEnabled == true ? "Yes" : "No");
        if (!string.IsNullOrEmpty(g.MembershipRule)) AddRow("Rule", g.MembershipRule!);
        AddRow("Members", g.MemberCount?.ToString() ?? "—");
        AddRow("Created", g.CreatedDateTime?.ToLocalTime().ToString("yyyy-MM-dd") ?? "—");

        // Dynamic membership can't be edited by hand.
        AddMemberButton.Visibility = string.IsNullOrEmpty(g.MembershipRule) ? Visibility.Visible : Visibility.Collapsed;
        EnableDisableButton.Visibility = Visibility.Collapsed;
        ListHeader.Text = "Members";
        SubList.Visibility = Visibility.Collapsed;
        MembersList.Visibility = Visibility.Visible;
        await LoadMembersAsync(g.Id);
    }

    private async Task LoadMembersAsync(string groupId)
    {
        MembersList.ItemsSource = null;
        SubEmpty.Visibility = Visibility.Collapsed;
        var graph = App.Current.GraphService;
        if (graph == null) return;

        SubRing.IsActive = true;
        try
        {
            var members = await graph.GetGroupMembersAsync(groupId, 200);
            if (_currentGroup?.Id != groupId) return; // stale
            if (members.Count == 0) { SubEmpty.Text = "No members."; SubEmpty.Visibility = Visibility.Visible; }
            else MembersList.ItemsSource = members
                .Select(m => new MemberRow($"{m.DisplayName} · {m.UserPrincipalName}", m.Id)).ToList();
        }
        catch (Exception ex)
        {
            MembersList.ItemsSource = new[] { new MemberRow($"Failed to load: {ex.Message}", "") };
        }
        finally { SubRing.IsActive = false; }
    }

    // MARK: - User detail

    private async void UserList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UserList.SelectedItem is not UserRowViewModel row) { ClearDetail(); return; }
        var u = row.User;
        _currentUser = u;
        _currentGroup = null;
        ShowDetailHeader(row.Name, u.UserPrincipalName);

        DetailRows.Children.Clear();
        AddRow("Status", row.StatusLabel);
        AddRow("Mail", u.Mail ?? "—");
        AddRow("Job title", u.JobTitle ?? "—");
        AddRow("Department", u.Department ?? "—");
        AddRow("Office", u.OfficeLocation ?? "—");
        AddRow("Company", u.CompanyName ?? "—");
        AddRow("Created", u.CreatedDateTime?.ToLocalTime().ToString("yyyy-MM-dd") ?? "—");
        AddRow("Last sign-in", u.LastSignInDateTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—");

        AddMemberButton.Visibility = Visibility.Collapsed;
        EnableDisableButton.Visibility = Visibility.Visible;
        EnableDisableButton.Content = u.AccountEnabled == false ? "Enable account" : "Disable account";
        ListHeader.Text = "Group memberships";
        MembersList.Visibility = Visibility.Collapsed;
        SubList.Visibility = Visibility.Visible;
        await LoadSubAsync(async graph =>
            (await graph.GetUserGroupsAsync(u.Id)).Select(gr => gr.DisplayName).ToList(),
            () => UserList.SelectedItem == row);
    }

    // MARK: - Identity actions

    private async void AddMember_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGroup == null) return;
        var upn = await PromptTextAsync("Add member", "Enter the user's UPN or object id:", "user@ecuad.ca");
        if (string.IsNullOrWhiteSpace(upn)) return;

        var ok = await App.Current.GraphService!.AddGroupMemberAsync(_currentGroup.Id, upn.Trim());
        if (ok) await LoadMembersAsync(_currentGroup.Id);
        else await MessageAsync("Add member failed", $"Could not add {upn} to {_currentGroup.DisplayName}.");
    }

    private async void RemoveMember_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGroup == null || (sender as Button)?.Tag is not MemberRow m || string.IsNullOrEmpty(m.UserId)) return;
        if (!await ConfirmAsync("Remove member", $"Remove {m.Label} from {_currentGroup.DisplayName}?", "Remove")) return;

        var ok = await App.Current.GraphService!.RemoveGroupMemberAsync(_currentGroup.Id, m.UserId);
        if (ok) await LoadMembersAsync(_currentGroup.Id);
        else await MessageAsync("Remove failed", "Could not remove the member.");
    }

    private async void EnableDisable_Click(object sender, RoutedEventArgs e)
    {
        if (_currentUser == null) return;
        var enable = _currentUser.AccountEnabled == false;
        var verb = enable ? "Enable" : "Disable";
        if (!await ConfirmAsync($"{verb} account", $"{verb} {_currentUser.DisplayName}'s account?", verb)) return;

        var ok = await App.Current.GraphService!.SetUserAccountEnabledAsync(_currentUser.Id, enable);
        if (ok)
        {
            _currentUser.AccountEnabled = enable;
            EnableDisableButton.Content = enable ? "Disable account" : "Enable account";
            ApplyFilter(); // refresh the list's status dot
        }
        else await MessageAsync($"{verb} failed", "Could not update the account.");
    }

    // MARK: - Dialogs

    private async Task<bool> ConfirmAsync(string title, string message, string primary)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<string?> PromptTextAsync(string title, string message, string placeholder)
    {
        var box = new TextBox { PlaceholderText = placeholder };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, box } },
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
    }

    private async Task MessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    // MARK: - Shared detail helpers

    private void ShowDetailHeader(string title, string subtitle)
    {
        EmptyDetail.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        DetailTitle.Text = title;
        DetailSubtitle.Text = subtitle;
    }

    private void ClearDetail()
    {
        DetailPanel.Visibility = Visibility.Collapsed;
        EmptyDetail.Visibility = Visibility.Visible;
        AddMemberButton.Visibility = Visibility.Collapsed;
        EnableDisableButton.Visibility = Visibility.Collapsed;
        _currentGroup = null;
        _currentUser = null;
    }

    private async Task LoadSubAsync(Func<Core.Services.GraphService, Task<List<string>>> fetch, Func<bool> stillSelected)
    {
        SubList.ItemsSource = null;
        SubEmpty.Visibility = Visibility.Collapsed;
        var graph = App.Current.GraphService;
        if (graph == null) return;

        SubRing.IsActive = true;
        try
        {
            var items = await fetch(graph);
            if (!stillSelected()) return;
            if (items.Count == 0) { SubEmpty.Text = "None."; SubEmpty.Visibility = Visibility.Visible; }
            else SubList.ItemsSource = items;
        }
        catch (Exception ex)
        {
            SubList.ItemsSource = new[] { $"Failed to load: {ex.Message}" };
        }
        finally { SubRing.IsActive = false; }
    }

    private void AddRow(string label, string value)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var l = new TextBlock { Text = label, FontSize = 12, Opacity = 0.65 };
        l.SetValue(Grid.ColumnProperty, 0);

        var v = new TextBlock { Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        v.SetValue(Grid.ColumnProperty, 1);

        grid.Children.Add(l);
        grid.Children.Add(v);
        DetailRows.Children.Add(grid);
    }
}
