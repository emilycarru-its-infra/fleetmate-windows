using FleetMate.Core.Models.Inventory;
using FleetMate.Core.Services.Inventory;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using Windows.System;

namespace FleetMate.WinUI.Pages.Inventory;

/// <summary>
/// Pick the person to re-allocate an asset to: search Snipe-IT's users, pick
/// one, add a note. The page then checks the asset in and out
/// (<see cref="Core.Inventory.AssetReallocation"/>).
/// </summary>
internal sealed partial class ReAllocateDialog : ContentDialog
{
    private readonly SnipeService _snipe;
    private readonly TextBox _search = new() { PlaceholderText = "Search users" };
    private readonly ListView _users = new() { Height = 240, SelectionMode = ListViewSelectionMode.Single, DisplayMemberPath = nameof(SnipeUser.Name) };
    private readonly TextBox _note = new() { Header = "Note (optional)", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };

    public ReAllocateDialog(SnipeService snipe, SnipeAsset asset)
    {
        _snipe = snipe;
        Title = $"Re-Allocate {asset.DisplayName}";
        PrimaryButtonText = "Re-Allocate";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        IsPrimaryButtonEnabled = false;

        AutomationProperties.SetName(_search, "Search users");
        AutomationProperties.SetAutomationId(_search, "InventoryReAllocateSearch");
        AutomationProperties.SetName(_users, "Users");
        _status.Foreground = InventoryUi.Secondary;
        var searchButton = new Button { Content = "Search" };
        AutomationProperties.SetAutomationId(searchButton, "InventoryReAllocateSearchButton");
        searchButton.Click += async (_, _) => await SearchAsync();
        _search.KeyDown += async (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            await SearchAsync();
        };
        _users.SelectionChanged += (_, _) => IsPrimaryButtonEnabled = _users.SelectedItem is SnipeUser;

        var searchRow = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions = { new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto } },
            Children = { _search, searchButton },
        };
        Grid.SetColumn(searchButton, 1);
        Content = new StackPanel
        {
            Width = 420,
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = $"Choose who {asset.DisplayName} ({asset.AssetTag}) goes to.", TextWrapping = TextWrapping.Wrap },
                searchRow,
                _status,
                _users,
                _note,
            },
        };
    }

    public int? SelectedUserId => (_users.SelectedItem as SnipeUser)?.Id;

    public string? Note => string.IsNullOrWhiteSpace(_note.Text) ? null : _note.Text.Trim();

    private async Task SearchAsync()
    {
        var query = _search.Text?.Trim();
        if (string.IsNullOrEmpty(query)) return;
        _status.Text = "Searching…";
        try
        {
            var users = await _snipe.GetUsersAsync(search: query);
            _users.ItemsSource = users;
            _status.Text = users.Count == 0 ? "No users match." : $"{users.Count} users";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[snipe] User search failed");
            _status.Text = $"User search failed: {ex.Message}";
        }
    }
}
