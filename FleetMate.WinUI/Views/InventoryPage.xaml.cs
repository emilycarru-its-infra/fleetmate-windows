using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FleetMate.Core.Models.Inventory;
using FleetMate.Core.Services.Inventory;
using FleetMate.WinUI.ViewModels;

namespace FleetMate.WinUI.Views;

public sealed partial class InventoryPage : Page
{
    private List<AssetRowViewModel> _all = new();
    private SnipeAsset? _current;
    private List<SnipeStatusLabelFull> _statusLabels = new();

    public InventoryPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        var snipe = App.Current.SnipeService;
        if (snipe == null)
        {
            CountText.Text = "Snipe-IT is not configured — add credentials in Settings.";
            _all = new();
            ApplyFilter();
            return;
        }

        RefreshButton.IsEnabled = false;
        LoadingRing.IsActive = true;
        AssetList.ItemsSource = null;
        try
        {
            var assets = await snipe.GetAssetsAsync(forceRefresh: true);
            _all = assets.Select(a => new AssetRowViewModel(a)).ToList();
            ApplyFilter();

            if (_statusLabels.Count == 0)
            {
                try { _statusLabels = await snipe.GetStatusLabelsAsync(); } catch { /* status picker is best-effort */ }
            }
        }
        catch (Exception ex)
        {
            CountText.Text = $"Failed to load assets: {ex.Message}";
            _all = new();
            ApplyFilter();
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
        var rows = string.IsNullOrEmpty(q) ? _all : _all.Where(r => r.Matches(q)).ToList();
        rows = rows.OrderBy(r => r.Tag, StringComparer.OrdinalIgnoreCase).ToList();
        AssetList.ItemsSource = rows;
        CountText.Text = _all.Count == 0 ? "No assets."
            : rows.Count == _all.Count ? $"{_all.Count} assets"
            : $"{rows.Count} of {_all.Count} assets";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void AssetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AssetList.SelectedItem is not AssetRowViewModel row)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            EmptyDetail.Visibility = Visibility.Visible;
            _current = null;
            return;
        }

        var a = row.Asset;
        _current = a;
        EmptyDetail.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        DetailName.Text = $"{row.Tag} · {row.Name}";

        var assigned = a.AssignedTo != null;
        CheckInButton.Visibility = assigned ? Visibility.Visible : Visibility.Collapsed;
        CheckOutButton.Visibility = assigned ? Visibility.Collapsed : Visibility.Visible;
        StatusCombo.ItemsSource = _statusLabels;
        StatusCombo.SelectedItem = _statusLabels.FirstOrDefault(s => s.Id == a.StatusLabel?.Id)
            ?? _statusLabels.FirstOrDefault(s => s.Name.Equals(a.StatusLabel?.Name, StringComparison.OrdinalIgnoreCase));
        SaveStatusButton.IsEnabled = _statusLabels.Count > 0;

        StatusRows.Children.Clear();
        AddRow(StatusRows, "Status", row.Status);
        AddRow(StatusRows, "Assigned to", row.AssignedTo + (string.IsNullOrEmpty(a.AssignedTo?.Type) ? "" : $" ({a.AssignedTo!.Type})"));
        AddRow(StatusRows, "Location", a.Location?.Name ?? a.RtdLocation?.Name ?? "—");
        AddRow(StatusRows, "Company", a.Company?.Name ?? "—");
        AddRow(StatusRows, "Last checkout", Fmt(a.LastCheckout));
        AddRow(StatusRows, "Expected checkin", Fmt(a.ExpectedCheckin));

        HardwareRows.Children.Clear();
        AddRow(HardwareRows, "Model", a.Model?.Name ?? "—");
        AddRow(HardwareRows, "Model no.", a.ModelNumber ?? "—");
        AddRow(HardwareRows, "Category", a.Category?.Name ?? "—");
        AddRow(HardwareRows, "Manufacturer", a.Manufacturer?.Name ?? "—");
        AddRow(HardwareRows, "Serial", row.Serial);
        AddRow(HardwareRows, "Notes", string.IsNullOrEmpty(a.Notes) ? "—" : a.Notes!);

        FinancialRows.Children.Clear();
        AddRow(FinancialRows, "Purchase cost", string.IsNullOrEmpty(a.PurchaseCost) ? "—" : a.PurchaseCost!);
        AddRow(FinancialRows, "Purchase date", Fmt(a.PurchaseDate));
        AddRow(FinancialRows, "Order number", string.IsNullOrEmpty(a.OrderNumber) ? "—" : a.OrderNumber!);
        AddRow(FinancialRows, "Supplier", a.Supplier?.Name ?? "—");
        AddRow(FinancialRows, "Warranty", a.WarrantyMonths is > 0 ? $"{a.WarrantyMonths} months" : "—");
        AddRow(FinancialRows, "Warranty expires", Fmt(a.WarrantyExpires));
    }

    // MARK: - Actions

    private async void CheckOut_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } a) return;
        var userIdText = await PromptTextAsync("Check out", $"Check out {a.AssetTag} to which Snipe user id?", "e.g. 42");
        if (string.IsNullOrWhiteSpace(userIdText) || !int.TryParse(userIdText.Trim(), out var uid)) return;

        var req = new SnipeCheckoutRequest { CheckoutToType = "user", AssignedUser = uid, StatusId = DeployedStatusId(a) };
        await RunAsync("Check out", s => s.CheckoutAssetAsync(a.Id, req));
    }

    private async void CheckIn_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } a) return;
        if (!await ConfirmAsync("Check in", $"Check in {a.AssetTag} from {a.AssignedTo?.Name}?", "Check in")) return;
        await RunAsync("Check in", s => s.CheckinAssetAsync(a.Id));
    }

    private async void SaveStatus_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } a) return;
        if (StatusCombo.SelectedItem is not SnipeStatusLabelFull sel) return;
        if (sel.Id == a.StatusLabel?.Id) return;

        var req = new SnipeAssetRequest { AssetTag = a.AssetTag, StatusId = sel.Id, ModelId = a.Model?.Id ?? 0 };
        await RunAsync("Update status", s => s.UpdateAssetAsync(a.Id, req));
    }

    private int DeployedStatusId(SnipeAsset a)
    {
        var deployed = _statusLabels.FirstOrDefault(s => s.Type.Equals("deployed", StringComparison.OrdinalIgnoreCase));
        return deployed?.Id ?? a.StatusLabel?.Id ?? _statusLabels.FirstOrDefault()?.Id ?? 0;
    }

    private async Task RunAsync(string name, Func<SnipeService, Task<SnipeResponse?>> action)
    {
        var snipe = App.Current.SnipeService;
        if (snipe == null) return;

        SetActionsEnabled(false);
        try
        {
            var resp = await action(snipe);
            var ok = resp?.Status?.Equals("success", StringComparison.OrdinalIgnoreCase) == true;
            if (ok) await LoadAsync();
            else await MessageAsync($"{name} failed", resp?.Messages ?? "The request was not successful.");
        }
        catch (Exception ex)
        {
            await MessageAsync($"{name} failed", ex.Message);
        }
        finally { SetActionsEnabled(true); }
    }

    private void SetActionsEnabled(bool on) =>
        CheckOutButton.IsEnabled = CheckInButton.IsEnabled = SaveStatusButton.IsEnabled = on;

    // MARK: - Dialogs

    private async Task<bool> ConfirmAsync(string title, string message, string primary)
    {
        var dialog = new ContentDialog
        {
            Title = title, Content = message, PrimaryButtonText = primary, CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot,
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
            PrimaryButtonText = "OK", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
    }

    private async Task MessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK", XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private static void AddRow(StackPanel host, string label, string value)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var l = new TextBlock { Text = label, FontSize = 12, Opacity = 0.65 };
        l.SetValue(Grid.ColumnProperty, 0);

        var v = new TextBlock { Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        v.SetValue(Grid.ColumnProperty, 1);

        grid.Children.Add(l);
        grid.Children.Add(v);
        host.Children.Add(grid);
    }

    private static string Fmt(SnipeDate? d) => d?.Formatted ?? d?.Date ?? "—";
    private static string Fmt(SnipeDateTime? d) => d?.Formatted ?? d?.DateTime ?? "—";
}
