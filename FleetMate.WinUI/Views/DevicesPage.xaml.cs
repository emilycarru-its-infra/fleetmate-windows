using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services;
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

    // MARK: - Load / filter

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
        UpdateSelectionUi();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    // MARK: - Selection

    private List<IntuneDevice> SelectedDevices() =>
        DeviceList.SelectedItems.OfType<DeviceRowViewModel>().Select(r => r.Device).ToList();

    private async void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionUi();

        var selected = DeviceList.SelectedItems.OfType<DeviceRowViewModel>().ToList();
        if (selected.Count == 1)
        {
            EmptyDetail.Visibility = Visibility.Collapsed;
            MultiDetail.Visibility = Visibility.Collapsed;
            DetailPanel.Visibility = Visibility.Visible;
            await ShowDetailAsync(selected[0]);
        }
        else
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            EmptyDetail.Visibility = selected.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            MultiDetail.Visibility = selected.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            MultiDetail.Text = $"{selected.Count} devices selected. Use the toolbar to run a bulk action.";
        }
    }

    private void UpdateSelectionUi()
    {
        var n = DeviceList.SelectedItems.Count;
        var has = n >= 1;
        SyncButton.IsEnabled = RestartButton.IsEnabled = LockButton.IsEnabled =
            RetireButton.IsEnabled = WipeButton.IsEnabled = has;
        SelectionText.Text = n == 0 ? "" : n == 1 ? "1 selected" : $"{n} selected";
    }

    // MARK: - Detail

    private async Task ShowDetailAsync(DeviceRowViewModel row)
    {
        var d = row.Device;
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

        await Task.WhenAll(LoadComplianceAsync(d.Id), LoadAppsAsync(d.Id));
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
            if (!IsStillSelected(deviceId)) return;
            if (policies.Count == 0)
                ComplianceEmpty.Visibility = Visibility.Visible;
            else
                ComplianceList.ItemsSource = policies
                    .Select(p => $"{p.DisplayName ?? "(policy)"} — {p.State ?? "unknown"}").ToList();
        }
        catch (Exception ex)
        {
            ComplianceList.ItemsSource = new[] { $"Failed to load: {ex.Message}" };
        }
        finally { ComplianceRing.IsActive = false; }
    }

    private async Task LoadAppsAsync(string deviceId)
    {
        AppsList.ItemsSource = null;
        AppsEmpty.Visibility = Visibility.Collapsed;
        var graph = App.Current.GraphService;
        if (graph == null || string.IsNullOrEmpty(deviceId)) return;

        AppsRing.IsActive = true;
        try
        {
            var apps = await graph.GetDetectedAppsAsync(deviceId);
            if (!IsStillSelected(deviceId)) return;
            if (apps.Count == 0)
                AppsEmpty.Visibility = Visibility.Visible;
            else
                AppsList.ItemsSource = apps
                    .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Take(100)
                    .Select(a => string.IsNullOrEmpty(a.Version) ? a.DisplayName ?? "(app)" : $"{a.DisplayName} — {a.Version}")
                    .ToList();
        }
        catch (Exception ex)
        {
            AppsList.ItemsSource = new[] { $"Failed to load: {ex.Message}" };
        }
        finally { AppsRing.IsActive = false; }
    }

    /// <summary>True if the given device is still the sole selection (guards stale async responses).</summary>
    private bool IsStillSelected(string deviceId) =>
        DeviceList.SelectedItems.OfType<DeviceRowViewModel>().ToList() is { Count: 1 } sel && sel[0].Device.Id == deviceId;

    // MARK: - Bulk actions

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        var devices = SelectedDevices();
        if (devices.Count == 0) return;
        if (!await ConfirmAsync("Sync devices", $"Trigger a sync on {devices.Count} device(s)?", "Sync", false)) return;
        await RunAsync("Sync", g => g.SyncDevicesAsync(devices.Select(d => d.Id)));
    }

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        var devices = SelectedDevices();
        if (devices.Count == 0) return;
        if (!await ConfirmAsync("Restart devices", $"Restart {devices.Count} device(s)? Users may lose unsaved work.", "Restart", true)) return;
        await RunAsync("Restart", g => g.RebootDevicesAsync(devices.Select(d => d.Id)));
    }

    private async void Lock_Click(object sender, RoutedEventArgs e)
    {
        var devices = SelectedDevices();
        if (devices.Count == 0) return;
        var pin = await PromptPinAsync(devices.Count);
        if (pin is null) return; // cancelled
        await RunAsync("Lock", g => g.RemoteLockDevicesAsync(devices.Select(d => d.Id), string.IsNullOrWhiteSpace(pin) ? null : pin));
    }

    private async void Retire_Click(object sender, RoutedEventArgs e)
    {
        var devices = SelectedDevices();
        if (devices.Count == 0) return;
        if (!await ConfirmAsync("Retire devices",
            $"Retire {devices.Count} device(s)? This removes company data and unenrolls them from Intune.", "Retire", true)) return;
        await RunAsync("Retire", g => g.RetireDevicesAsync(devices.Select(d => d.Id)), reload: true);
    }

    private async void Wipe_Click(object sender, RoutedEventArgs e)
    {
        var devices = SelectedDevices();
        if (devices.Count == 0) return;
        var keep = await PromptWipeAsync(devices.Count);
        if (keep is null) return; // cancelled
        await RunAsync("Wipe", g => g.WipeDevicesAsync(devices.Select(d => d.Id), keepEnrollmentData: keep.Value), reload: true);
    }

    private async Task RunAsync(string name, Func<GraphService, Task<List<GraphService.DeviceActionResult>>> action, bool reload = false)
    {
        var graph = App.Current.GraphService;
        if (graph == null) return;

        SetToolbarEnabled(false);
        try
        {
            var results = await action(graph);
            var ok = results.Count(r => r.Success);
            var fail = results.Count - ok;
            var msg = $"{name}: {ok} succeeded" + (fail > 0 ? $", {fail} failed." : ".");
            if (fail > 0)
            {
                var failed = results.Where(r => !r.Success).Take(10).Select(r => $"• {r.Message ?? r.DeviceId}");
                msg += "\n\n" + string.Join("\n", failed);
            }
            await MessageAsync($"{name} complete", msg);
        }
        catch (Exception ex)
        {
            await MessageAsync($"{name} failed", ex.Message);
        }
        finally
        {
            SetToolbarEnabled(true);
            UpdateSelectionUi();
            if (reload) await LoadAsync();
        }
    }

    private void SetToolbarEnabled(bool on) =>
        SyncButton.IsEnabled = RestartButton.IsEnabled = LockButton.IsEnabled =
            RetireButton.IsEnabled = WipeButton.IsEnabled = on;

    // MARK: - Dialogs

    private async Task<bool> ConfirmAsync(string title, string message, string primary, bool dangerous)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = dangerous ? ContentDialogButton.Close : ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>Returns the PIN (possibly empty), or null if cancelled.</summary>
    private async Task<string?> PromptPinAsync(int count)
    {
        var box = new TextBox { PlaceholderText = "Optional PIN (leave blank for none)", MaxLength = 8 };
        var dialog = new ContentDialog
        {
            Title = "Lock devices",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = $"Remote-lock {count} device(s). Optionally set a recovery PIN.", TextWrapping = TextWrapping.Wrap },
                    box,
                },
            },
            PrimaryButtonText = "Lock",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text ?? "" : null;
    }

    /// <summary>Returns keepEnrollmentData, or null if cancelled.</summary>
    private async Task<bool?> PromptWipeAsync(int count)
    {
        var keep = new CheckBox { Content = "Keep enrollment data" };
        var dialog = new ContentDialog
        {
            Title = "Wipe devices",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = $"Factory-reset {count} device(s)? This erases all data and cannot be undone.", TextWrapping = TextWrapping.Wrap },
                    keep,
                },
            },
            PrimaryButtonText = "Wipe",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? keep.IsChecked == true : null;
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

    // MARK: - Detail row helpers

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
