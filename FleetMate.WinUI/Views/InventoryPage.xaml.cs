using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FleetMate.Core.Models.Inventory;
using FleetMate.WinUI.ViewModels;

namespace FleetMate.WinUI.Views;

public sealed partial class InventoryPage : Page
{
    private List<AssetRowViewModel> _all = new();

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
            return;
        }

        var a = row.Asset;
        EmptyDetail.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        DetailName.Text = $"{row.Tag} · {row.Name}";

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
