using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FleetMate.Core.Models.Devices;
using FleetMate.WinUI.ViewModels;

namespace FleetMate.WinUI.Views;

public sealed partial class DevicesPage : Page
{
    private List<DeviceRowViewModel> _all = new();

    public DevicesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        var graph = App.Current.GraphService;
        if (graph == null)
        {
            CountText.Text = "Microsoft Graph is not configured — add credentials in Settings.";
            _all = new();
            ApplyFilter();
            return;
        }

        RefreshButton.IsEnabled = false;
        LoadingRing.IsActive = true;
        DeviceList.ItemsSource = null;
        try
        {
            var devices = await graph.GetManagedDevicesAsync(limit: 1000);
            _all = devices.Select(d => new DeviceRowViewModel(d)).ToList();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            CountText.Text = $"Failed to load devices: {ex.Message}";
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
        rows = rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        DeviceList.ItemsSource = rows;
        CountText.Text = _all.Count == 0 ? "No devices."
            : rows.Count == _all.Count ? $"{_all.Count} devices"
            : $"{rows.Count} of {_all.Count} devices";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DeviceRowViewModel row)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            EmptyDetail.Visibility = Visibility.Visible;
            return;
        }

        var d = row.Device;
        EmptyDetail.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        DetailName.Text = row.Name;

        SummaryRows.Children.Clear();
        AddRow("Serial", row.Serial);
        AddRow("Model", Join(d.Manufacturer, d.Model));
        AddRow("OS", row.Os);
        AddRow("Compliance", row.Compliance);
        AddRow("Management", d.ManagementState ?? "—");
        AddRow("Enrollment", d.DeviceEnrollmentType ?? "—");
        AddRow("Enrolled", d.EnrolledDateTime?.ToLocalTime().ToString("yyyy-MM-dd") ?? "—");
        AddRow("Last sync", row.LastSync);
        AddRow("User", d.UserPrincipalName ?? d.UserDisplayName ?? "—");
        AddRow("Encrypted", Yes(d.IsEncrypted));
        AddRow("Supervised", Yes(d.IsSupervised));
        AddRow("Storage", Storage(d));
        AddRow("Category", d.DeviceCategoryDisplayName ?? "—");

        await LoadComplianceAsync(d.Id);
    }

    private async Task LoadComplianceAsync(string deviceId)
    {
        ComplianceList.ItemsSource = null;
        ComplianceEmpty.Visibility = Visibility.Collapsed;
        var graph = App.Current.GraphService;
        if (graph == null || string.IsNullOrEmpty(deviceId)) return;

        ComplianceRing.IsActive = true;
        try
        {
            var policies = await graph.GetDeviceComplianceAsync(deviceId);
            // Guard against a slower request landing after the user picked another device.
            if (DeviceList.SelectedItem is DeviceRowViewModel cur && cur.Device.Id != deviceId) return;

            if (policies.Count == 0)
                ComplianceEmpty.Visibility = Visibility.Visible;
            else
                ComplianceList.ItemsSource = policies
                    .Select(p => $"{p.DisplayName ?? "(policy)"} — {p.State ?? "unknown"}")
                    .ToList();
        }
        catch (Exception ex)
        {
            ComplianceList.ItemsSource = new[] { $"Failed to load: {ex.Message}" };
        }
        finally
        {
            ComplianceRing.IsActive = false;
        }
    }

    private void AddRow(string label, string value)
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
        SummaryRows.Children.Add(grid);
    }

    private static string Join(string? a, string? b) =>
        string.Join(" ", new[] { a, b }.Where(s => !string.IsNullOrEmpty(s))) is { Length: > 0 } s ? s : "—";

    private static string Yes(bool? b) => b == true ? "Yes" : b == false ? "No" : "—";

    private static string Storage(IntuneDevice d)
    {
        if (d.TotalStorageSpaceInBytes is not > 0) return "—";
        double gb(long? bytes) => Math.Round((bytes ?? 0) / 1024d / 1024d / 1024d, 1);
        return $"{gb(d.FreeStorageSpaceInBytes)} GB free of {gb(d.TotalStorageSpaceInBytes)} GB";
    }
}
