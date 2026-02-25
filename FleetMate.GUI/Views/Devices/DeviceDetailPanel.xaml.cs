using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services;
using Serilog;

namespace FleetMate.GUI.Views.Devices;

public partial class DeviceDetailPanel : UserControl
{
    private IntuneDevice? _device;
    private GraphService? _graphService;

    public event EventHandler? CloseRequested;

    public DeviceDetailPanel()
    {
        InitializeComponent();
    }

    public async Task ShowDeviceAsync(IntuneDevice device, GraphService? graphService)
    {
        _device = device;
        _graphService = graphService;

        DeviceName.Text = device.DeviceName;
        DeviceSerial.Text = device.SerialNumber ?? device.Id;

        ContentPanel.Children.Clear();

        RenderSummary(device);
        RenderEnrollment(device);
        RenderHardware(device);
        RenderCompliance(device);

        // Load async detail sections
        if (graphService != null)
        {
            await LoadCompliancePoliciesAsync(device.Id);
            await LoadDetectedAppsAsync(device.Id);
        }
    }

    // ── Summary ──────────────────────────────────────────────────────────

    private void RenderSummary(IntuneDevice d)
    {
        AddSection("Summary");
        AddRow("Device Name", d.DeviceName);
        AddRow("Manufacturer", d.Manufacturer);
        AddRow("Model", d.Model);
        AddRow("OS", FormatOs(d));
        AddRow("Last Check-in", d.LastSyncDateTime?.ToString("yyyy-MM-dd HH:mm") ?? "-");
        AddRow("Primary User", d.UserDisplayName ?? d.UserPrincipalName);
    }

    // ── Enrollment & Identity ────────────────────────────────────────────

    private void RenderEnrollment(IntuneDevice d)
    {
        AddSection("Enrollment & Identity");
        AddRowMono("Intune ID", d.Id);
        AddRowMono("Entra Device ID", d.AzureAdDeviceId);
        AddRow("Enrollment Type", FormatEnrollmentType(d.DeviceEnrollmentType));
        AddRow("Supervised", d.IsSupervised == true ? "Yes" : d.IsSupervised == false ? "No" : "-");
        AddRow("Enrolled", d.EnrolledDateTime?.ToString("yyyy-MM-dd HH:mm") ?? "-");
        AddRow("Management Agent", d.ManagementAgent);
        AddRow("Category", d.DeviceCategoryDisplayName);
    }

    // ── Hardware ─────────────────────────────────────────────────────────

    private void RenderHardware(IntuneDevice d)
    {
        AddSection("Hardware");
        AddRowMono("Serial Number", d.SerialNumber);

        if (d.TotalStorageSpaceInBytes is > 0)
            AddRow("Total Storage", FormatBytes(d.TotalStorageSpaceInBytes.Value));
        if (d.FreeStorageSpaceInBytes is > 0)
            AddRow("Free Storage", FormatBytes(d.FreeStorageSpaceInBytes.Value));
        if (d.StorageUsedPercent.HasValue)
        {
            AddStorageBar(d.StorageUsedPercent.Value);
        }

        AddRowMono("Wi-Fi MAC", d.WiFiMacAddress);
        AddRowMono("Ethernet MAC", d.EthernetMacAddress);
    }

    // ── Compliance ───────────────────────────────────────────────────────

    private void RenderCompliance(IntuneDevice d)
    {
        AddSection("Compliance");
        AddComplianceBadge(d.ComplianceState);
        AddRow("Management State", d.ManagementState);
        AddRow("Encrypted", d.IsEncrypted == true ? "Yes" : d.IsEncrypted == false ? "No" : "-");
        AddRow("Jailbroken", d.JailBroken);
    }

    // ── Async Sections ───────────────────────────────────────────────────

    private async Task LoadCompliancePoliciesAsync(string deviceId)
    {
        if (_graphService == null) return;

        try
        {
            var policies = await _graphService.GetDeviceComplianceAsync(deviceId);
            if (policies.Count == 0) return;

            Dispatcher.Invoke(() =>
            {
                AddSection("Compliance Policies");
                foreach (var p in policies)
                {
                    var icon = p.State?.ToLowerInvariant() switch
                    {
                        "compliant" => "✅",
                        "noncompliant" or "error" => "❌",
                        "conflict" => "⚠️",
                        _ => "⬜"
                    };
                    AddRow(icon + " " + (p.DisplayName ?? "Unknown"), p.State ?? "unknown");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load compliance policies for {DeviceId}", deviceId);
        }
    }

    private async Task LoadDetectedAppsAsync(string deviceId)
    {
        if (_graphService == null) return;

        try
        {
            var apps = await _graphService.GetDetectedAppsAsync(deviceId);
            if (apps.Count == 0) return;

            Dispatcher.Invoke(() =>
            {
                AddSection($"Detected Apps ({apps.Count})");
                foreach (var app in apps.Take(50))
                {
                    var version = !string.IsNullOrEmpty(app.Version) ? $"  v{app.Version}" : "";
                    AddRow(app.DisplayName ?? "Unknown", version);
                }
                if (apps.Count > 50)
                {
                    ContentPanel.Children.Add(new TextBlock
                    {
                        Text = $"... and {apps.Count - 50} more",
                        FontSize = 11,
                        FontStyle = FontStyles.Italic,
                        Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
                        Margin = new Thickness(0, 4, 0, 0)
                    });
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load detected apps for {DeviceId}", deviceId);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void AddSection(string title)
    {
        ContentPanel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Margin = new Thickness(0, 14, 0, 4)
        });
    }

    private void AddRow(string? label, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock
        {
            Text = label ?? "",
            Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
            FontSize = 12
        };
        Grid.SetColumn(labelBlock, 0);

        var valueBlock = new TextBlock { Text = value, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(valueBlock, 1);

        grid.Children.Add(labelBlock);
        grid.Children.Add(valueBlock);
        ContentPanel.Children.Add(grid);
    }

    private void AddRowMono(string label, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock
        {
            Text = label,
            Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
            FontSize = 12
        };
        Grid.SetColumn(labelBlock, 0);

        var valueBlock = new TextBlock
        {
            Text = value,
            FontSize = 11,
            FontFamily = new FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(valueBlock, 1);

        grid.Children.Add(labelBlock);
        grid.Children.Add(valueBlock);
        ContentPanel.Children.Add(grid);
    }

    private void AddComplianceBadge(string? state)
    {
        var (color, text) = (state?.ToLowerInvariant()) switch
        {
            "compliant" => ("#27ae60", "Compliant"),
            "noncompliant" => ("#e74c3c", "Non-Compliant"),
            "ingrace" or "ingraceperiod" => ("#f39c12", "In Grace Period"),
            _ => ("#666", state ?? "Unknown")
        };

        var border = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3, 8, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 4)
        };
        border.Child = new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold
        };
        ContentPanel.Children.Add(border);
    }

    private void AddStorageBar(double usedPercent)
    {
        var bar = new Grid { Height = 8, Margin = new Thickness(0, 4, 0, 4) };
        bar.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
            CornerRadius = new CornerRadius(4)
        });

        var barColor = usedPercent > 90 ? "#e74c3c" : usedPercent > 70 ? "#f39c12" : "#27ae60";
        bar.Children.Add(new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(barColor)),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = Math.Max(4, usedPercent / 100.0 * 200)
        });
        ContentPanel.Children.Add(bar);

        ContentPanel.Children.Add(new TextBlock
        {
            Text = $"{usedPercent:F1}% used",
            FontSize = 10,
            Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
            Margin = new Thickness(0, 0, 0, 4)
        });
    }

    // ── Format Helpers ───────────────────────────────────────────────────

    private static string FormatOs(IntuneDevice d)
    {
        if (d.OperatingSystem == null) return "-";
        return d.OsVersion != null ? $"{d.OperatingSystem} {d.OsVersion}" : d.OperatingSystem;
    }

    private static string? FormatEnrollmentType(string? type)
    {
        if (string.IsNullOrEmpty(type)) return null;
        // Insert spaces before uppercase letters to make camelCase readable
        return System.Text.RegularExpressions.Regex.Replace(type, "([a-z])([A-Z])", "$1 $2");
    }

    private static string FormatBytes(long bytes)
    {
        var gb = (double)bytes / 1_073_741_824;
        return gb >= 1 ? $"{gb:F1} GB" : $"{(double)bytes / 1_048_576:F0} MB";
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
