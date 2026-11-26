using System.Collections.Specialized;
using FleetMate.Core.Identity;
using FleetMate.Core.Models.Identity;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace FleetMate.WinUI.Pages.Identity;

/// <summary>
/// The Identity tab: device groups (expand one to see its devices) and the
/// Users search with its inspector. The toolbar search field filters groups
/// as you type and, on the Users view, looks people up on Enter. The logic is
/// <see cref="IdentityViewModel"/>'s.
/// </summary>
public sealed partial class IdentityPage : Page, Shell.ICaptureViews
{
    private bool _loaded;

    public IdentityPage()
    {
        ViewModel = new IdentityViewModel(App.Services.Identity);
        InitializeComponent();
        ViewModel.Groups.CollectionChanged += (_, _) => RebuildTree();
        MainWindow.SearchChanged += OnToolbarSearchChanged;
        MainWindow.SearchSubmitted += OnToolbarSearchSubmitted;
    }

    public IdentityViewModel ViewModel { get; }

    private Visibility ConfiguredVisibility => ViewModel.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
    private Visibility NotConfiguredVisibility => ViewModel.IsConfigured ? Visibility.Collapsed : Visibility.Visible;

    private bool OnUsers => UsersPanel.Visibility == Visibility.Visible;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // A capture walks every view each pass; each pass starts on the groups.
        if (App.Services.DemoMode) Views.SelectedItem = GroupsItem;
        if (_loaded || !ViewModel.IsConfigured) return;
        _loaded = true;
        await ViewModel.LoadGroupsAsync();
    }

    // MARK: - Views

    private void Views_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var users = Equals(sender.SelectedItem?.Tag, "users");
        GroupsPanel.Visibility = !users && ViewModel.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
        UsersPanel.Visibility = users && ViewModel.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Inspector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var tag = sender.SelectedItem?.Tag as string ?? "properties";
        PropertiesView.Visibility = tag == "properties" ? Visibility.Visible : Visibility.Collapsed;
        DevicesView.Visibility = tag == "devices" ? Visibility.Visible : Visibility.Collapsed;
        GroupsView.Visibility = tag == "groups" ? Visibility.Visible : Visibility.Collapsed;
    }

    // MARK: - Toolbar search

    private void OnToolbarSearchChanged(object? sender, string text)
    {
        if (OnUsers) ViewModel.UserQuery = text;
        else ViewModel.GroupFilter = text;
    }

    private async void OnToolbarSearchSubmitted(object? sender, string text)
    {
        if (!OnUsers) return;
        ViewModel.UserQuery = text;
        await ViewModel.SearchUsersAsync();
    }

    // MARK: - Groups

    private void RebuildTree()
    {
        GroupsTree.RootNodes.Clear();
        foreach (var group in ViewModel.Groups)
        {
            GroupsTree.RootNodes.Add(new TreeViewNode
            {
                Content = string.IsNullOrWhiteSpace(group.Description) ? group.DisplayName : $"{group.DisplayName} ({group.Description})",
                HasUnrealizedChildren = true,
                IsExpanded = false,
            });
            GroupIds[GroupsTree.RootNodes[^1]] = group.Id;
        }
    }

    private readonly Dictionary<TreeViewNode, string> GroupIds = new();

    private async void GroupsTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        var node = args.Node;
        if (!node.HasUnrealizedChildren || !GroupIds.TryGetValue(node, out var id)) return;
        node.HasUnrealizedChildren = false;
        try
        {
            var devices = await ViewModel.GroupDevicesAsync(id);
            if (devices.Count == 0) node.Children.Add(new TreeViewNode { Content = "(no devices)" });
            foreach (var device in devices)
                node.Children.Add(new TreeViewNode { Content = $"{device.DeviceName} · {device.OperatingSystem} {device.OsVersion}" });
        }
        catch (Exception ex)
        {
            node.Children.Add(new TreeViewNode { Content = $"Could not load devices: {ex.Message}" });
        }
    }

    // MARK: - Users

    private void UsersList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.SelectedUser = UsersList.SelectedItem as UserRow;

    private async void ToggleAccount_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedUser is not { } row) return;
        var verb = row.User.AccountEnabled == false ? "Enable" : "Disable";
        if (!await ConfirmAsync($"{verb} the account {row.UserPrincipalName}?", verb)) return;
        await ViewModel.ToggleAccountAsync();
    }

    private async void RemoveGroup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not EntraGroup group || ViewModel.SelectedUser is not { } row) return;
        if (!await ConfirmAsync($"Remove {row.DisplayName} from '{group.DisplayName}'?", "Remove")) return;
        await ViewModel.RemoveFromGroupAsync(group);
    }

    private async Task<bool> ConfirmAsync(string message, string action)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = action,
            Content = message,
            PrimaryButtonText = action,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    // MARK: - Binding helpers

    public static Visibility Hide(bool on) => on ? Visibility.Collapsed : Visibility.Visible;

    public static bool HasText(string? text) => !string.IsNullOrWhiteSpace(text);

    public static Brush StateBrush(AccountState state) => (Brush)Application.Current.Resources[state switch
    {
        AccountState.Active => "SystemFillColorSuccessBrush",
        AccountState.Disabled => "SystemFillColorCriticalBrush",
        _ => "SystemFillColorNeutralBrush",
    }];

    public static string DeviceDetail(string? os, string? version, string? serial) =>
        string.Join(" · ", new[] { $"{os} {version}".Trim(), serial }.Where(p => !string.IsNullOrWhiteSpace(p)));

    // MARK: - QA capture

    public IReadOnlyList<string> CaptureViews { get; } = new[] { "groups-expanded", "users", "users-devices", "users-groups" };

    public async Task ShowCaptureViewAsync(string view)
    {
        switch (view)
        {
            case "groups-expanded":
                if (GroupsTree.RootNodes.FirstOrDefault() is { } first) first.IsExpanded = true;
                break;
            case "users":
                Views.SelectedItem = UsersItem;
                ViewModel.UserQuery = "a";
                await ViewModel.SearchUsersAsync();
                UsersList.SelectedIndex = 0;
                Inspector.SelectedItem = Inspector.Items[0];
                break;
            case "users-devices":
                Inspector.SelectedItem = Inspector.Items[1];
                break;
            case "users-groups":
                Inspector.SelectedItem = Inspector.Items[2];
                break;
        }
    }
}
