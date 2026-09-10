using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services;
using Serilog;

namespace FleetMate.GUI.Views.Devices;

/// <summary>
/// Compliance troubleshooting lightbox: one policy on one device, opened from
/// the device detail's policy rows. Per-setting states on the left with the
/// failing rows first and highlighted; the policy definition (requirements,
/// grace period and actions, assigned groups) on the right. Copy produces a
/// plain-text, ticket-ready report. Shared contract with the macOS app.
/// </summary>
public partial class CompliancePolicyLightbox : Window
{
    private readonly IntuneDevice _device;
    private readonly DeviceCompliancePolicyState _policy;
    private readonly GraphService _graph;

    private List<ComplianceSettingState> _settingStates = new();
    private CompliancePolicyDefinition? _definition;
    private List<string> _assignedGroupNames = new();

    public CompliancePolicyLightbox(IntuneDevice device, DeviceCompliancePolicyState policy,
        GraphService graph, Window? owner = null)
    {
        InitializeComponent();
        _device = device;
        _policy = policy;
        _graph = graph;

        var host = owner ?? Application.Current?.MainWindow;
        if (host != null)
        {
            Owner = host;
            var source = PresentationSource.FromVisual(host);
            if (source?.CompositionTarget != null)
            {
                var topLeft = source.CompositionTarget.TransformFromDevice
                    .Transform(host.PointToScreen(new Point(0, 0)));
                Left = topLeft.X;
                Top = topLeft.Y;
            }
            else
            {
                Left = host.Left;
                Top = host.Top;
            }
            Width = host.ActualWidth;
            Height = host.ActualHeight;
            CardBorder.Width = Math.Min(Width - 32, Math.Max(860, Width * 0.8));
            CardBorder.Height = Math.Min(Height - 32, Math.Max(560, Height * 0.8));
        }
        else
        {
            Width = 1100;
            Height = 720;
            CardBorder.Width = 1060;
            CardBorder.Height = 680;
        }

        RenderHeader();
        Loaded += async (_, _) => await LoadAsync();
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    private void OnScrimMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!CardBorder.IsMouseOver) Close();
    }

    // ── Rendering ────────────────────────────────────────────────────────

    private void RenderHeader()
    {
        PolicyNameText.Text = _policy.DisplayName ?? "Unknown Policy";

        var platform = _definition?.PlatformType ?? _policy.PlatformType;
        PlatformText.Text = platform ?? "";
        PlatformBadge.Visibility = string.IsNullOrEmpty(platform) ? Visibility.Collapsed : Visibility.Visible;

        var (color, label) = _policy.State?.ToLowerInvariant() switch
        {
            "compliant" => ("#27ae60", "Compliant"),
            "noncompliant" => ("#e74c3c", "Non-Compliant"),
            "error" => ("#e74c3c", "Error"),
            "conflict" => ("#f39c12", "Conflict"),
            "ingraceperiod" => ("#f39c12", "In Grace Period"),
            _ => ("#718096", _policy.State ?? "Unknown")
        };
        StateBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        StateText.Text = label;

        var version = _definition?.Version ?? _policy.Version;
        VersionText.Text = version.HasValue ? $"v{version}" : "";

        var parts = new List<string>
        {
            _device.DeviceName,
        };
        if (!string.IsNullOrEmpty(_device.SerialNumber)) parts.Add(_device.SerialNumber!);
        if (!string.IsNullOrEmpty(_device.UserDisplayName ?? _device.UserPrincipalName))
            parts.Add((_device.UserDisplayName ?? _device.UserPrincipalName)!);
        if (_device.LastSyncDateTime is { } sync) parts.Add($"checked in {sync:yyyy-MM-dd HH:mm}");
        if (_policy.LastReportedDateTime is { } reported) parts.Add($"evaluated {reported:yyyy-MM-dd HH:mm}");
        DeviceLineText.Text = string.Join(" · ", parts);
    }

    private async Task LoadAsync()
    {
        SettingsPanel.Children.Add(Note("Loading setting states..."));
        DefinitionPanel.Children.Add(Note("Loading policy definition..."));

        var statesTask = _graph.GetPolicySettingStatesAsync(_device.Id, _policy.Id);
        var definitionTask = _graph.GetCompliancePolicyDefinitionAsync(_policy.Id);
        _settingStates = await statesTask;
        _definition = await definitionTask;

        if (_definition is { AssignedGroupIds.Count: > 0 })
        {
            foreach (var id in _definition.AssignedGroupIds)
            {
                try
                {
                    var group = await _graph.GetGroupByIdAsync(id);
                    _assignedGroupNames.Add(group?.DisplayName ?? id);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Could not resolve group {GroupId}", id);
                    _assignedGroupNames.Add(id);
                }
            }
        }

        RenderHeader();
        RenderSettings();
        RenderDefinition();
    }

    private void RenderSettings()
    {
        SettingsPanel.Children.Clear();
        SettingsPanel.Children.Add(Heading($"Setting States ({_settingStates.Count})"));

        if (_settingStates.Count == 0)
        {
            SettingsPanel.Children.Add(Note("No per-setting states reported for this policy."));
            return;
        }

        // Non-compliant and error rows first, then the rest, stable by name.
        var ordered = _settingStates
            .OrderByDescending(s => s.IsBad)
            .ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var state in ordered)
        {
            var card = new Border
            {
                Background = state.IsBad
                    ? new SolidColorBrush(Color.FromArgb(0x22, 0xE7, 0x4C, 0x3C))
                    : (Brush)FindResource("CardBackgroundBrush"),
                BorderBrush = state.IsBad
                    ? new SolidColorBrush(Color.FromArgb(0x66, 0xE7, 0x4C, 0x3C))
                    : (Brush)FindResource("CardBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 0, 0, 6)
            };
            var stack = new StackPanel();

            var header = new DockPanel();
            var stateLabel = new TextBlock
            {
                Text = state.State ?? "unknown",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                    state.IsBad ? "#e74c3c" : state.State?.ToLowerInvariant() == "compliant" ? "#27ae60" : "#718096")),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(stateLabel, Dock.Right);
            header.Children.Add(stateLabel);
            header.Children.Add(new TextBlock
            {
                Text = state.DisplayName,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });
            stack.Children.Add(header);

            void Detail(string label, string? value)
            {
                if (string.IsNullOrEmpty(value)) return;
                stack.Children.Add(new TextBlock
                {
                    Text = $"{label}: {value}",
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            Detail("Current value", state.CurrentValue);
            // "0" means no error — showing it on every compliant row is noise.
            if (state.ErrorCode is { } code && code != 0)
                Detail("Error", $"0x{code:X8}{(string.IsNullOrEmpty(state.ErrorDescription) ? "" : $" — {state.ErrorDescription}")}");
            else if (!string.IsNullOrEmpty(state.ErrorDescription) && state.ErrorDescription != "0")
                Detail("Error", state.ErrorDescription);
            if (state.Sources is { Count: > 0 })
                Detail("Source policies", string.Join(", ", state.Sources.Select(s => s.DisplayName ?? s.Id)));
            Detail("User", state.UserPrincipalName);

            card.Child = stack;
            SettingsPanel.Children.Add(card);
        }
    }

    private void RenderDefinition()
    {
        DefinitionPanel.Children.Clear();
        DefinitionPanel.Children.Add(Heading("Policy Definition"));

        if (_definition == null)
        {
            DefinitionPanel.Children.Add(Note("The policy definition could not be loaded."));
            return;
        }
        if (_definition.AccessDenied)
        {
            DefinitionPanel.Children.Add(Note(
                "Graph refused the policy definition read — the app registration needs DeviceManagementConfiguration.Read.All."));
            return;
        }

        if (!string.IsNullOrEmpty(_definition.Description))
        {
            DefinitionPanel.Children.Add(new TextBlock
            {
                Text = _definition.Description,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
                Margin = new Thickness(0, 0, 0, 8)
            });
        }

        if (_definition.Requirements.Count > 0)
        {
            DefinitionPanel.Children.Add(SubHeading("Requirements"));
            foreach (var (key, value) in _definition.Requirements.Select(kv => (kv.Key, kv.Value)))
            {
                var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var label = new TextBlock
                {
                    Text = key,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush")
                };
                Grid.SetColumn(label, 0);
                grid.Children.Add(label);
                var valueBlock = new TextBlock { Text = value, FontSize = 11, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(valueBlock, 1);
                grid.Children.Add(valueBlock);
                DefinitionPanel.Children.Add(grid);
            }
        }

        if (_definition.ScheduledActions.Count > 0)
        {
            DefinitionPanel.Children.Add(SubHeading("Actions for noncompliance"));
            foreach (var action in _definition.ScheduledActions)
            {
                DefinitionPanel.Children.Add(new TextBlock
                {
                    Text = "• " + action,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 1, 0, 1)
                });
            }
        }

        if (_assignedGroupNames.Count > 0)
        {
            DefinitionPanel.Children.Add(SubHeading("Assigned to"));
            foreach (var name in _assignedGroupNames)
            {
                DefinitionPanel.Children.Add(new TextBlock
                {
                    Text = "• " + name,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 1, 0, 1)
                });
            }
        }
    }

    // ── Actions ──────────────────────────────────────────────────────────

    private void OnCopyReport(object sender, RoutedEventArgs e)
    {
        // Report layout shared with the macOS app (work item 5143): policy line
        // with state, device block, settings, requirements, actions, assignments.
        var report = new StringBuilder();
        var version = _definition?.Version ?? _policy.Version;
        var platform = _definition?.PlatformType ?? _policy.PlatformType;
        report.AppendLine($"Compliance policy: {_policy.DisplayName ?? "Unknown"} — {_policy.State ?? "unknown"}"
            + (version.HasValue ? $" (v{version})" : "")
            + (string.IsNullOrEmpty(platform) ? "" : $" [{platform}]"));
        report.AppendLine();
        report.AppendLine($"Device: {_device.DeviceName}");
        if (!string.IsNullOrEmpty(_device.SerialNumber)) report.AppendLine($"Serial: {_device.SerialNumber}");
        if (!string.IsNullOrEmpty(_device.UserDisplayName ?? _device.UserPrincipalName))
            report.AppendLine($"Primary user: {_device.UserDisplayName ?? _device.UserPrincipalName}");
        if (!string.IsNullOrEmpty(_device.OperatingSystem))
            report.AppendLine($"OS: {_device.OperatingSystem} {_device.OsVersion}".TrimEnd());
        report.AppendLine($"Overall compliance: {_device.ComplianceState ?? "unknown"}");
        if (_device.LastSyncDateTime is { } sync) report.AppendLine($"Last check-in: {sync:yyyy-MM-dd HH:mm}");
        if (_policy.LastReportedDateTime is { } reported) report.AppendLine($"Last evaluation: {reported:yyyy-MM-dd HH:mm}");
        report.AppendLine($"Intune id: {_device.Id}");

        report.AppendLine();
        report.AppendLine($"Setting states ({_settingStates.Count}):");
        foreach (var state in _settingStates.OrderByDescending(s => s.IsBad).ThenBy(s => s.DisplayName))
        {
            var line = $"  [{state.State ?? "unknown"}] {state.DisplayName}";
            if (!string.IsNullOrEmpty(state.CurrentValue)) line += $" — current: {state.CurrentValue}";
            if (state.ErrorCode is { } code && code != 0) line += $" — error: 0x{code:X8} {state.ErrorDescription}".TrimEnd();
            else if (!string.IsNullOrEmpty(state.ErrorDescription) && state.ErrorDescription != "0") line += $" — error: {state.ErrorDescription}";
            report.AppendLine(line);
            if (state.Sources is { Count: > 0 })
                report.AppendLine($"      sources: {string.Join(", ", state.Sources.Select(s => s.DisplayName ?? s.Id))}");
            if (!string.IsNullOrEmpty(state.UserPrincipalName)) report.AppendLine($"      user: {state.UserPrincipalName}");
        }

        if (_definition is { AccessDenied: false })
        {
            if (_definition.Requirements.Count > 0)
            {
                report.AppendLine();
                report.AppendLine("Policy requirements:");
                foreach (var requirement in _definition.Requirements)
                    report.AppendLine($"  {requirement.Key}: {requirement.Value}");
            }
            if (_definition.ScheduledActions.Count > 0)
            {
                report.AppendLine();
                report.AppendLine("Actions for noncompliance:");
                foreach (var action in _definition.ScheduledActions) report.AppendLine($"  {action}");
            }
            if (_assignedGroupNames.Count > 0)
            {
                report.AppendLine();
                report.AppendLine("Assigned to:");
                foreach (var name in _assignedGroupNames) report.AppendLine($"  {name}");
            }
        }

        report.AppendLine();
        report.AppendLine($"Intune: https://intune.microsoft.com/#view/Microsoft_Intune_Devices/DeviceSettingsMenuBlade/~/compliance/mdmDeviceId/{_device.Id}");

        try { Clipboard.SetText(report.ToString()); } catch { }
        SyncStatusText.Text = "Report copied to the clipboard";
        SyncStatusText.Visibility = Visibility.Visible;
    }

    private void OnOpenInIntune(object sender, RoutedEventArgs e)
    {
        var url = $"https://intune.microsoft.com/#view/Microsoft_Intune_Devices/DeviceSettingsMenuBlade/~/compliance/mdmDeviceId/{_device.Id}";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warning(ex, "Failed to open Intune"); }
    }

    private async void OnSyncDevice(object sender, RoutedEventArgs e)
    {
        SyncStatusText.Text = "Syncing device...";
        SyncStatusText.Visibility = Visibility.Visible;
        var results = await _graph.SyncDevicesAsync(new[] { _device.Id });
        SyncStatusText.Text = results.FirstOrDefault()?.Success == true
            ? "Sync sent — the device re-evaluates at next check-in"
            : $"Sync failed: {results.FirstOrDefault()?.Message ?? "no response"}";
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    // ── Helpers ──────────────────────────────────────────────────────────

    private TextBlock Heading(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        FontSize = 14,
        Margin = new Thickness(0, 0, 0, 8)
    };

    private TextBlock SubHeading(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        FontSize = 12,
        Margin = new Thickness(0, 10, 0, 4)
    };

    private TextBlock Note(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
        TextWrapping = TextWrapping.Wrap
    };
}
