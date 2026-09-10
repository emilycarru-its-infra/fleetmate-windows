using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services;
using ModernWpf.Controls;
using Serilog;

namespace FleetMate.GUI.Views.Devices;

/// <summary>
/// The device detail sidebar — the macOS DeviceDetailView, ported. Sections in
/// the Mac's order: Summary, Group Membership, Enrollment &amp; Identity,
/// Hardware, Compliance &amp; Conditional Access (with per-policy states), and
/// Detected Apps. The async sections render into placeholders reserved at
/// build time so they land in order, not at the bottom as they arrive.
/// </summary>
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
        var groupsHost = Section("Group Membership", "");
        RenderEnrollment(device);
        RenderHardware(device);
        var complianceHost = RenderCompliance(device);
        var appsHost = Section("Detected Apps", "");

        if (graphService == null)
        {
            AddNote(groupsHost, "Graph is not configured.");
            AddNote(appsHost, "Graph is not configured.");
            return;
        }

        AddNote(groupsHost, "Loading groups...");
        AddNote(appsHost, "Loading apps...");

        // Parallel like the Mac; each fills its own reserved host.
        var current = device.Id;
        await Task.WhenAll(
            LoadGroupsAsync(device, groupsHost, current),
            LoadCompliancePoliciesAsync(device.Id, complianceHost, current),
            LoadDetectedAppsAsync(device.Id, appsHost, current));
    }

    // ── Summary ──────────────────────────────────────────────────────────

    private void RenderSummary(IntuneDevice d)
    {
        var host = Section("Summary", "");
        AddRow(host, "Device Name", d.DeviceName);
        AddRow(host, "Management Name", d.ManagedDeviceName);
        AddRow(host, "Ownership", FormatOwnership(d.ManagedDeviceOwnerType));
        AddRow(host, "Manufacturer", d.Manufacturer);
        AddRow(host, "Model", d.Model);
        AddRow(host, "OS", FormatOs(d));
        AddRow(host, "Last Check-in", d.LastSyncDateTime?.ToString("yyyy-MM-dd HH:mm"));
        AddRow(host, "Primary User", d.UserDisplayName ?? d.UserPrincipalName);
    }

    // ── Enrollment & Identity ────────────────────────────────────────────

    private void RenderEnrollment(IntuneDevice d)
    {
        var host = Section("Enrollment & Identity", "");
        AddRowMono(host, "Intune Device ID", d.Id);
        AddRowMono(host, "Entra Device ID", d.AzureAdDeviceId);
        AddRow(host, "Enrollment Type", FormatEnrollmentType(d.DeviceEnrollmentType));
        AddRow(host, "Enrollment Profile", d.EnrollmentProfileName);
        AddRow(host, "Supervised", d.IsSupervised == true ? "Yes" : d.IsSupervised == false ? "No" : null);
        AddRow(host, "Enrolled", d.EnrolledDateTime?.ToString("yyyy-MM-dd HH:mm"));
        AddRow(host, "Registration State", d.DeviceRegistrationState);
        AddRow(host, "Management Agent", d.ManagementAgent);
        AddRow(host, "Join Type", d.JoinType);
    }

    // ── Hardware ─────────────────────────────────────────────────────────

    private void RenderHardware(IntuneDevice d)
    {
        var host = Section("Hardware", "");
        AddRowMono(host, "Serial Number", d.SerialNumber);
        AddRow(host, "SKU Family", d.SkuFamily);

        if (d.TotalStorageSpaceInBytes is > 0)
            AddRow(host, "Total Storage", FormatBytes(d.TotalStorageSpaceInBytes.Value));
        if (d.FreeStorageSpaceInBytes is > 0)
            AddRow(host, "Free Storage", FormatBytes(d.FreeStorageSpaceInBytes.Value));
        if (d.StorageUsedPercent.HasValue)
            AddStorageBar(host, d.StorageUsedPercent.Value);
        if (d.PhysicalMemoryInBytes is > 0)
            AddRow(host, "Physical Memory", FormatBytes(d.PhysicalMemoryInBytes.Value));

        AddRowMono(host, "Wi-Fi MAC", d.WiFiMacAddress);
        AddRowMono(host, "Ethernet MAC", d.EthernetMacAddress);

        if (!string.IsNullOrEmpty(d.Imei) || !string.IsNullOrEmpty(d.Meid))
        {
            AddRowMono(host, "IMEI", d.Imei);
            AddRowMono(host, "MEID", d.Meid);
            AddRow(host, "Phone Number", d.PhoneNumber);
            AddRow(host, "Carrier", d.SubscriberCarrier);
        }
    }

    // ── Compliance ───────────────────────────────────────────────────────

    private StackPanel RenderCompliance(IntuneDevice d)
    {
        var host = Section("Compliance & Conditional Access", "");
        AddComplianceBadge(host, d.ComplianceState);
        AddRow(host, "Management State", d.ManagementState);
        AddRow(host, "Azure AD Registered", d.AzureAdRegistered == true ? "Yes" : d.AzureAdRegistered == false ? "No" : null);
        AddRow(host, "Device Category", d.DeviceCategoryDisplayName);
        AddRow(host, "Encrypted", d.IsEncrypted == true ? "Yes" : d.IsEncrypted == false ? "No" : null);
        AddRow(host, "Jailbroken", d.JailBroken);
        return host;
    }

    // ── Async sections ───────────────────────────────────────────────────

    private async Task LoadGroupsAsync(IntuneDevice device, StackPanel host, string forDeviceId)
    {
        if (_graphService == null || string.IsNullOrEmpty(device.AzureAdDeviceId))
        {
            Dispatcher.Invoke(() => ReplaceNotes(host, "No Entra device id."));
            return;
        }

        try
        {
            var groups = await _graphService.GetDeviceGroupMembershipsAsync(device.AzureAdDeviceId!);
            if (_device?.Id != forDeviceId) return;

            Dispatcher.Invoke(() =>
            {
                ClearNotes(host);
                if (groups.Count == 0)
                {
                    AddNote(host, "No group memberships found");
                    return;
                }
                foreach (var group in groups.OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase))
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                    row.Children.Add(new FontIcon
                    {
                        FontFamily = new FontFamily("Segoe Fluent Icons"),
                        Glyph = "",
                        FontSize = 11,
                        Foreground = (Brush)FindResource("SystemControlForegroundAccentBrush"),
                        Margin = new Thickness(0, 2, 6, 0),
                        VerticalAlignment = VerticalAlignment.Top
                    });
                    var text = new StackPanel();
                    text.Children.Add(new TextBlock { Text = group.DisplayName, FontSize = 12, FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap });
                    if (!string.IsNullOrEmpty(group.Description))
                        text.Children.Add(new TextBlock
                        {
                            Text = group.Description,
                            FontSize = 10,
                            Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
                            TextTrimming = TextTrimming.CharacterEllipsis
                        });
                    row.Children.Add(text);
                    host.Children.Add(row);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load device groups for {DeviceId}", device.Id);
            Dispatcher.Invoke(() => ReplaceNotes(host, "Could not load groups."));
        }
    }

    private async Task LoadCompliancePoliciesAsync(string deviceId, StackPanel host, string forDeviceId)
    {
        if (_graphService == null) return;

        try
        {
            var policies = await _graphService.GetDeviceComplianceAsync(deviceId);
            if (_device?.Id != forDeviceId || policies.Count == 0) return;

            Dispatcher.Invoke(() =>
            {
                host.Children.Add(new TextBlock
                {
                    Text = "Compliance Policies",
                    FontSize = 11,
                    Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
                    Margin = new Thickness(0, 6, 0, 2)
                });
                foreach (var policy in policies)
                {
                    var color = policy.State?.ToLowerInvariant() switch
                    {
                        "compliant" => "#27ae60",
                        "noncompliant" or "error" => "#e74c3c",
                        "conflict" => "#f39c12",
                        _ => "#718096"
                    };
                    var row = new DockPanel { Margin = new Thickness(0, 0, 0, 0) };
                    var state = new TextBlock
                    {
                        Text = policy.State ?? "unknown",
                        FontSize = 10,
                        Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(8, 0, 0, 0)
                    };
                    DockPanel.SetDock(state, Dock.Right);
                    row.Children.Add(state);
                    var dot = new System.Windows.Shapes.Ellipse
                    {
                        Width = 8,
                        Height = 8,
                        Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
                        Margin = new Thickness(0, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    DockPanel.SetDock(dot, Dock.Left);
                    row.Children.Add(dot);
                    row.Children.Add(new TextBlock
                    {
                        Text = policy.DisplayName ?? "Unknown Policy",
                        FontSize = 11,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center
                    });

                    // Clicking a policy opens the compliance troubleshooting
                    // lightbox — the shared contract with the macOS app.
                    var clickable = new Border
                    {
                        Child = row,
                        Background = Brushes.Transparent,
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(4, 2, 4, 2),
                        Margin = new Thickness(-4, 0, -4, 0),
                        Cursor = System.Windows.Input.Cursors.Hand,
                        ToolTip = "Open the compliance details for this policy"
                    };
                    clickable.MouseEnter += (_, _) => clickable.Background = (Brush)FindResource("SubtleFillBrush");
                    clickable.MouseLeave += (_, _) => clickable.Background = Brushes.Transparent;
                    var capturedPolicy = policy;
                    clickable.MouseLeftButtonUp += (_, _) =>
                    {
                        if (_device == null || _graphService == null) return;
                        new CompliancePolicyLightbox(_device, capturedPolicy, _graphService, Window.GetWindow(this)).ShowDialog();
                    };
                    host.Children.Add(clickable);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load compliance policies for {DeviceId}", deviceId);
        }
    }

    private async Task LoadDetectedAppsAsync(string deviceId, StackPanel host, string forDeviceId)
    {
        if (_graphService == null) return;

        try
        {
            var apps = await _graphService.GetDetectedAppsAsync(deviceId);
            if (_device?.Id != forDeviceId) return;

            Dispatcher.Invoke(() =>
            {
                ClearNotes(host);
                if (apps.Count == 0)
                {
                    AddNote(host, "No detected apps found");
                    return;
                }
                AddNote(host, $"{apps.Count} apps detected");
                foreach (var app in apps.Take(50))
                {
                    var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                    var version = new TextBlock
                    {
                        Text = app.Version ?? "",
                        FontSize = 10,
                        Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(8, 0, 0, 0)
                    };
                    DockPanel.SetDock(version, Dock.Right);
                    row.Children.Add(version);
                    row.Children.Add(new TextBlock
                    {
                        Text = app.DisplayName ?? "Unknown",
                        FontSize = 11,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    host.Children.Add(row);
                }
                if (apps.Count > 50)
                {
                    AddNote(host, $"... and {apps.Count - 50} more");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load detected apps for {DeviceId}", deviceId);
        }
    }

    // ── Building blocks ──────────────────────────────────────────────────

    /// <summary>Section header plus a host panel the rows render into.</summary>
    private StackPanel Section(string title, string glyph)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 4) };
        header.Children.Add(new FontIcon
        {
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            Glyph = glyph,
            FontSize = 12,
            Foreground = (Brush)FindResource("SystemControlForegroundAccentBrush"),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        });
        ContentPanel.Children.Add(header);

        var host = new StackPanel();
        ContentPanel.Children.Add(host);
        return host;
    }

    private void AddRow(StackPanel host, string? label, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        host.Children.Add(Row(label ?? "", value!, mono: false));
    }

    private void AddRowMono(StackPanel host, string label, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        host.Children.Add(Row(label, value!, mono: true));
    }

    private Grid Row(string label, string value, bool mono)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
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
            FontSize = mono ? 11 : 12,
            TextWrapping = TextWrapping.Wrap
        };
        if (mono) valueBlock.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        Grid.SetColumn(valueBlock, 1);

        grid.Children.Add(labelBlock);
        grid.Children.Add(valueBlock);
        return grid;
    }

    private void AddNote(StackPanel host, string text)
    {
        host.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
            Margin = new Thickness(0, 2, 0, 2),
            Tag = "note"
        });
    }

    private static void ClearNotes(StackPanel host)
    {
        for (var i = host.Children.Count - 1; i >= 0; i--)
        {
            if (host.Children[i] is TextBlock { Tag: "note" }) host.Children.RemoveAt(i);
        }
    }

    private static void ReplaceNotes(StackPanel host, string text)
    {
        ClearNotes(host);
        host.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11,
            Opacity = 0.7,
            Tag = "note"
        });
    }

    private void AddComplianceBadge(StackPanel host, string? state)
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
        host.Children.Add(border);
    }

    private void AddStorageBar(StackPanel host, double usedPercent)
    {
        var bar = new Grid { Height = 8, Margin = new Thickness(0, 4, 0, 4) };
        bar.Children.Add(new Border
        {
            Background = (Brush)FindResource("SubtleFillBrush"),
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
        host.Children.Add(bar);

        host.Children.Add(new TextBlock
        {
            Text = $"{usedPercent:F1}% used",
            FontSize = 10,
            Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
            Margin = new Thickness(0, 0, 0, 4)
        });
    }

    // ── Format helpers ───────────────────────────────────────────────────

    private static string FormatOs(IntuneDevice d)
    {
        if (d.OperatingSystem == null) return "-";
        return d.OsVersion != null ? $"{d.OperatingSystem} {d.OsVersion}" : d.OperatingSystem;
    }

    private static string? FormatOwnership(string? ownership) => ownership?.ToLowerInvariant() switch
    {
        null or "" => null,
        "company" => "Corporate",
        "personal" => "Personal",
        _ => char.ToUpperInvariant(ownership[0]) + ownership[1..]
    };

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
