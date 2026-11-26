using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FleetMate.Core.Devices;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services;
using ModernWpf.Controls;
using Serilog;

namespace FleetMate.GUI.Views.Devices;

/// <summary>
/// The device detail sidebar — the macOS DeviceDetailView, ported. Sections in
/// the Mac's order: Summary, Enrollment &amp; Identity, Recovery Secrets,
/// Hardware, Group Membership, Compliance &amp; Conditional Access (with
/// per-policy states), and Detected Apps. The async sections render into placeholders reserved at
/// build time so they land in order, not at the bottom as they arrive.
/// </summary>
/// <summary>
/// An Apple organization record to show beside a device: the record, the
/// organization's service name ("Apple School Manager"), the name of the
/// device management service it is assigned to, and how to read its
/// AppleCare coverage when the device is selected.
/// </summary>
public sealed record AppleOrgContext(AppleOrgDevice Device, string ServiceName, string? ServerName,
    Func<Task<List<AppleCareAgreement>>>? LoadAppleCare,
    Func<Task<AppleActivationLock>>? LoadActivationLock = null)
{
    /// <summary>The record as Core's detail sections take it.</summary>
    public AppleOrgView View => new(Device, ServiceName, ServerName, LoadAppleCare != null, LoadActivationLock != null);
}

public partial class DeviceDetailPanel : UserControl
{
    private IntuneDevice? _device;
    private GraphService? _graphService;

    public event EventHandler? CloseRequested;

    public DeviceDetailPanel()
    {
        InitializeComponent();
    }

    public async Task ShowDeviceAsync(IntuneDevice device, GraphService? graphService, AutopilotDevice? autopilot = null,
        AppleOrgContext? apple = null)
    {
        _device = device;
        _graphService = graphService;
        (DeviceName.Text, DeviceSerial.Text) = DeviceDetailSections.Header(device);
        ContentPanel.Children.Clear();

        // The sections and their order come from Core, which FleetMate.WinUI draws too.
        var slots = Render(DeviceDetailSections.ForDevice(device, autopilot, apple?.View));
        var loads = new List<Task>();
        if (apple != null) loads.Add(LoadAppleAsync(apple, slots, device.Id));
        if (slots.TryGetValue(DeviceDetailSlot.Recovery, out var recovery)) RenderRecovery(device, recovery);
        var groupsHost = slots[DeviceDetailSlot.Groups];
        var complianceHost = slots[DeviceDetailSlot.Compliance];
        var appsHost = slots[DeviceDetailSlot.DetectedApps];

        if (graphService == null)
        {
            ReplaceNotes(groupsHost, "Graph is not configured.");
            ReplaceNotes(appsHost, "Graph is not configured.");
            await Task.WhenAll(loads);
            return;
        }

        // Parallel like the Mac; each fills its own reserved host.
        var current = device.Id;
        loads.Add(LoadGroupsAsync(device, groupsHost, current));
        loads.Add(LoadCompliancePoliciesAsync(device.Id, complianceHost, current));
        loads.Add(LoadDetectedAppsAsync(device.Id, appsHost, current));
        await Task.WhenAll(loads);
    }

    /// <summary>A device an Apple organization holds and Intune doesn't: only its organization record.</summary>
    public async Task ShowAppleOnlyAsync(AppleOrgContext apple)
    {
        _device = null;
        (DeviceName.Text, DeviceSerial.Text) = DeviceDetailSections.Header(apple.Device);
        ContentPanel.Children.Clear();
        var slots = Render(DeviceDetailSections.ForAppleOnly(apple.View));
        await LoadAppleAsync(apple, slots, "apple-org:" + apple.Device.SerialNumber);
    }

    /// <summary>A device Autopilot knows and Intune doesn't: only its Autopilot record.</summary>
    public void ShowAutopilotOnly(AutopilotDevice identity)
    {
        _device = null;
        (DeviceName.Text, DeviceSerial.Text) = DeviceDetailSections.Header(identity);
        ContentPanel.Children.Clear();
        Render(DeviceDetailSections.ForAutopilotOnly(identity));
    }

    /// <summary>Draw Core's sections; the hosts of the async ones come back by slot.</summary>
    private Dictionary<DeviceDetailSlot, StackPanel> Render(IEnumerable<DeviceDetailSection> sections)
    {
        var slots = new Dictionary<DeviceDetailSlot, StackPanel>();
        foreach (var section in sections)
        {
            var host = Section(section.Title, Glyph(section));
            if (section.Badge is { } badge) AddComplianceBadge(host, badge.Tone, badge.Text);
            foreach (var row in section.Rows) host.Children.Add(Row(row.Label, row.Value, row.Mono));
            if (section.StorageUsedPercent is { } used) AddStorageBar(host, used);
            if (section.Placeholder != null) AddNote(host, section.Placeholder);
            if (section.Slot != DeviceDetailSlot.None) slots[section.Slot] = host;
        }
        return slots;
    }

    /// <summary>The icon WPF has always drawn beside a section title.</summary>
    private static string Glyph(DeviceDetailSection section) => section.Title switch
    {
        "Summary" => "",
        "Device Management Service" when section.Rows.Count > 0 => "",
        "Recovery" => "",
        "Hardware" => "",
        "Group Membership" => "",
        "Compliance & Conditional Access" => "",
        "Detected Apps" => "",
        _ => "",
    };

    /// <summary>The Activation Lock row and AppleCare coverage, read when the device is selected.</summary>
    private Task LoadAppleAsync(AppleOrgContext ctx, Dictionary<DeviceDetailSlot, StackPanel> slots, string current)
    {
        var loads = new List<Task>();
        if (ctx.LoadActivationLock != null && slots.TryGetValue(DeviceDetailSlot.ActivationLock, out var org))
            loads.Add(AddActivationLock(org, ctx));
        if (ctx.LoadAppleCare != null && slots.TryGetValue(DeviceDetailSlot.AppleCare, out var coverage))
            loads.Add(LoadAppleCareAsync(ctx, coverage, current));
        return Task.WhenAll(loads);
    }

    /// <summary>Report only: no bypass code is shown and nothing here clears a lock. The row sits before Migration Deadline.</summary>
    private async Task AddActivationLock(StackPanel host, AppleOrgContext ctx)
    {
        var row = Row("Activation Lock", "Reading Activation Lock…", mono: false);
        var value = row.Children.OfType<TextBlock>().Last();
        value.Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush");
        var deadline = host.Children.OfType<Grid>().FirstOrDefault(g => g.Children.OfType<TextBlock>().FirstOrDefault()?.Text == "Migration Deadline");
        host.Children.Insert(deadline == null ? host.Children.Count : host.Children.IndexOf(deadline), row);

        var state = await ctx.LoadActivationLock!();
        value.Text = state.DetailText();
        value.Foreground = state.IsLocked()
            ? new SolidColorBrush(Color.FromRgb(0xE8, 0x89, 0x0C))
            : (Brush)FindResource("SystemControlForegroundBaseHighBrush");
    }

    private async Task LoadAppleCareAsync(AppleOrgContext ctx, StackPanel host, string current)
    {
        try
        {
            var agreements = await ctx.LoadAppleCare!();
            if (DeviceSerial.Text != ctx.Device.SerialNumber && _device?.Id != current) return;
            host.Children.Clear();
            if (agreements.Count == 0) { AddNote(host, "No coverage on record."); return; }
            foreach (var row in agreements.Select(DeviceDetailSections.AppleCareRow)) AddRow(host, row.Label, row.Value);
        }
        catch (Exception ex)
        {
            host.Children.Clear();
            AddNote(host, $"Coverage could not be read: {ex.Message}");
        }
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
                    var color = ToneHex(DeviceDetailSections.PolicyTone(policy.State), "#718096");
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
                AddNote(host, DeviceDetailSections.AppsHeadline(apps.Count));
                foreach (var app in apps.Take(DeviceDetailSections.AppLimit))
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
                if (apps.Count > DeviceDetailSections.AppLimit) AddNote(host, DeviceDetailSections.AppsMore(apps.Count));
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

    /// <summary>The colour each tone has always had here.</summary>
    private static string ToneHex(DeviceTone tone, string neutral = "#666") => tone switch
    {
        DeviceTone.Good => "#27ae60",
        DeviceTone.Attention => "#e8890c",
        DeviceTone.Caution => "#f39c12",
        _ => neutral,
    };

    private void AddComplianceBadge(StackPanel host, DeviceTone tone, string text)
    {
        var border = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(ToneHex(tone))),
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

        var barColor = ToneHex(DeviceDetailSections.StorageTone(usedPercent));
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

    private void OnClose(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
