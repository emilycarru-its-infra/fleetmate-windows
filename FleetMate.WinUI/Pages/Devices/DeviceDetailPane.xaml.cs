using FleetMate.Core.Devices;
using FleetMate.Core.Models.Devices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Serilog;

namespace FleetMate.WinUI.Pages.Devices;

/// <summary>An Apple organization record to show beside a device, and how to read its AppleCare and Activation Lock on selection.</summary>
public sealed record AppleOrgDetail(AppleOrgView View,
    Func<Task<List<AppleCareAgreement>>>? LoadAppleCare,
    Func<Task<AppleActivationLock>>? LoadActivationLock);

/// <summary>
/// The device detail pane — the macOS DeviceDetailView. Its sections and
/// their order come from FleetMate.Core's <see cref="DeviceDetailSections"/>,
/// which FleetMate.GUI draws too; the async ones (groups, policies, apps,
/// AppleCare, Activation Lock) are reserved in place and fill as they
/// arrive, each dropped if another device was selected meanwhile. A policy
/// row opens the compliance lightbox; Recovery reveals a secret only after
/// asking, and only in <see cref="RecoverySecretDialog"/>.
/// </summary>
public sealed partial class DeviceDetailPane : UserControl
{
    private string? _current;
    private IntuneDevice? _device;

    public DeviceDetailPane()
    {
        InitializeComponent();
    }

    public event EventHandler? CloseRequested;

    /// <summary>A compliance policy row was clicked.</summary>
    public event EventHandler<(IntuneDevice Device, DeviceCompliancePolicyState Policy)>? PolicyRequested;

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The device shown, by row id; null when none.</summary>
    public string? CurrentId => _current;

    public async Task ShowDeviceAsync(IntuneDevice device, AutopilotDevice? autopilot, AppleOrgDetail? apple)
    {
        _current = device.Id;
        _device = device;
        (TitleText.Text, SubtitleText.Text) = DeviceDetailSections.Header(device);
        await RenderAsync(DeviceDetailSections.ForDevice(device, autopilot, apple?.View), device, apple, device.Id);
    }

    public async Task ShowAppleOnlyAsync(AppleOrgDetail apple)
    {
        var id = DeviceListRow.OrgOnlyPrefix + apple.View.Device.SerialNumber;
        _current = id;
        _device = null;
        (TitleText.Text, SubtitleText.Text) = DeviceDetailSections.Header(apple.View.Device);
        await RenderAsync(DeviceDetailSections.ForAppleOnly(apple.View), null, apple, id);
    }

    public void ShowAutopilotOnly(AutopilotDevice identity)
    {
        var id = DeviceListRow.AutopilotOnlyPrefix + identity.Id;
        _current = id;
        _device = null;
        (TitleText.Text, SubtitleText.Text) = DeviceDetailSections.Header(identity);
        _ = RenderAsync(DeviceDetailSections.ForAutopilotOnly(identity), null, null, id);
    }

    private async Task RenderAsync(List<DeviceDetailSection> sections, IntuneDevice? device, AppleOrgDetail? apple, string current)
    {
        ContentPanel.Children.Clear();
        FirstPolicy = null;
        Scroller.ChangeView(null, 0, null, disableAnimation: true);
        var loads = new List<Task>();
        foreach (var section in sections)
        {
            var host = Section(section.Title);
            if (section.Badge is { } badge)
            {
                var b = DeviceUi.Badge(badge.Tone, badge.Text);
                b.Margin = new Thickness(0, 0, 0, 4);
                host.Children.Add(b);
            }
            foreach (var row in section.Rows) host.Children.Add(Row(row));
            if (section.StorageUsedPercent is { } used) host.Children.Add(StorageBar(used));
            if (section.Placeholder != null && section.Slot == DeviceDetailSlot.None) host.Children.Add(Note(section.Placeholder));

            var source = App.Services.DeviceDetails;
            switch (section.Slot)
            {
                case DeviceDetailSlot.ActivationLock when apple?.LoadActivationLock is { } read:
                    loads.Add(ActivationLockAsync(host, read, current));
                    break;
                case DeviceDetailSlot.AppleCare when apple?.LoadAppleCare is { } read:
                    loads.Add(AppleCareAsync(host, read, current));
                    break;
                case DeviceDetailSlot.Recovery when device != null:
                    Recovery(host, device);
                    break;
                case DeviceDetailSlot.Groups when device != null:
                    loads.Add(source == null ? NotConfigured(host) : GroupsAsync(host, source, device, current));
                    break;
                case DeviceDetailSlot.Compliance when device != null && source != null:
                    loads.Add(PoliciesAsync(host, source, device, current));
                    break;
                case DeviceDetailSlot.DetectedApps when device != null:
                    loads.Add(source == null ? NotConfigured(host) : AppsAsync(host, source, device, current));
                    break;
            }
        }
        await Task.WhenAll(loads);
    }

    // MARK: - Async sections

    private static Task NotConfigured(StackPanel host)
    {
        host.Children.Add(Note("Graph is not configured."));
        return Task.CompletedTask;
    }

    private async Task GroupsAsync(StackPanel host, IDeviceDetailSource source, IntuneDevice device, string current)
    {
        if (string.IsNullOrEmpty(device.AzureAdDeviceId))
        {
            host.Children.Add(Note("No Entra device id."));
            return;
        }
        var loading = Note("Loading groups...");
        host.Children.Add(loading);
        try
        {
            var groups = await source.GroupsAsync(device.AzureAdDeviceId!);
            if (_current != current) return;
            host.Children.Remove(loading);
            if (groups.Count == 0) { host.Children.Add(Note("No group memberships found")); return; }
            foreach (var group in groups.OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = group.DisplayName, Style = Res("BodyTextBlockStyle"), TextWrapping = TextWrapping.Wrap });
                if (!string.IsNullOrEmpty(group.Description))
                    text.Children.Add(new TextBlock { Text = group.Description, Style = Res("CaptionTextBlockStyle"), Foreground = DeviceUi.Theme("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap });
                var row = new Grid
                {
                    ColumnSpacing = 8,
                    Margin = new Thickness(0, 2, 0, 2),
                    ColumnDefinitions = { new() { Width = GridLength.Auto }, new() { Width = new GridLength(1, GridUnitType.Star) } },
                };
                row.Children.Add(new FontIcon { Glyph = "", FontSize = 12, Foreground = DeviceUi.Theme("AccentTextFillColorPrimaryBrush"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) });
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                host.Children.Add(row);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load device groups for {DeviceId}", device.Id);
            if (_current != current) return;
            host.Children.Remove(loading);
            host.Children.Add(Note("Could not load groups."));
        }
    }

    private async Task PoliciesAsync(StackPanel host, IDeviceDetailSource source, IntuneDevice device, string current)
    {
        try
        {
            var policies = await source.CompliancePoliciesAsync(device.Id);
            if (_current != current || policies.Count == 0) return;
            FirstPolicy = (device, policies[0]);
            var heading = Note("Compliance Policies");
            heading.Margin = new Thickness(0, 6, 0, 2);
            host.Children.Add(heading);
            foreach (var policy in policies)
            {
                var row = new Grid
                {
                    ColumnSpacing = 8,
                    ColumnDefinitions = { new() { Width = GridLength.Auto }, new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto } },
                };
                row.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center,
                    Fill = DeviceUi.ToneBrush(DeviceDetailSections.PolicyTone(policy.State)),
                });
                var name = new TextBlock { Text = policy.DisplayName ?? "Unknown Policy", Style = Res("BodyTextBlockStyle"), TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(name, 1);
                row.Children.Add(name);
                var state = new TextBlock { Text = policy.State ?? "unknown", Style = Res("CaptionTextBlockStyle"), Foreground = DeviceUi.Theme("TextFillColorSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(state, 2);
                row.Children.Add(state);

                // A policy opens the compliance troubleshooting lightbox, the shared contract with the Mac.
                var button = new Button
                {
                    Content = row,
                    Style = Res("SubtleButtonStyle"),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(4, 4, 4, 4),
                    Margin = new Thickness(-4, 0, -4, 0),
                };
                AutomationProperties.SetName(button, $"{policy.DisplayName ?? "Unknown Policy"}, {policy.State ?? "unknown"}");
                AutomationProperties.SetAutomationId(button, $"DevicesPolicy{policy.Id}");
                ToolTipService.SetToolTip(button, "Open the compliance details for this policy");
                var captured = policy;
                button.Click += (_, _) => PolicyRequested?.Invoke(this, (device, captured));
                host.Children.Add(button);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load compliance policies for {DeviceId}", device.Id);
        }
    }

    /// <summary>The first policy row, for the QA capture's lightbox view.</summary>
    internal (IntuneDevice Device, DeviceCompliancePolicyState Policy)? FirstPolicy { get; private set; }

    private async Task AppsAsync(StackPanel host, IDeviceDetailSource source, IntuneDevice device, string current)
    {
        var loading = Note("Loading apps...");
        host.Children.Add(loading);
        try
        {
            var apps = await source.DetectedAppsAsync(device.Id);
            if (_current != current) return;
            host.Children.Remove(loading);
            if (apps.Count == 0) { host.Children.Add(Note("No detected apps found")); return; }
            host.Children.Add(Note(DeviceDetailSections.AppsHeadline(apps.Count)));
            foreach (var app in apps.Take(DeviceDetailSections.AppLimit))
            {
                var row = new Grid
                {
                    ColumnSpacing = 8,
                    Margin = new Thickness(0, 1, 0, 1),
                    ColumnDefinitions = { new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto } },
                };
                row.Children.Add(new TextBlock { Text = app.DisplayName ?? "Unknown", Style = Res("BodyTextBlockStyle"), TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 });
                var version = new TextBlock { Text = app.Version ?? "", Style = Res("CaptionTextBlockStyle"), Foreground = DeviceUi.Theme("TextFillColorSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(version, 1);
                row.Children.Add(version);
                host.Children.Add(row);
            }
            if (apps.Count > DeviceDetailSections.AppLimit) host.Children.Add(Note(DeviceDetailSections.AppsMore(apps.Count)));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load detected apps for {DeviceId}", device.Id);
            if (_current == current) host.Children.Remove(loading);
        }
    }

    /// <summary>Report only: no bypass code is shown and nothing here clears a lock.</summary>
    private async Task ActivationLockAsync(StackPanel host, Func<Task<AppleActivationLock>> read, string current)
    {
        // The row sits before Migration Deadline, as on the Mac.
        var row = Row(new DeviceDetailRow("Activation Lock", "Reading Activation Lock…"));
        var value = (TextBlock)row.Children[1];
        value.Foreground = DeviceUi.Theme("TextFillColorSecondaryBrush");
        var deadline = host.Children.OfType<Grid>().FirstOrDefault(g => g.Children.FirstOrDefault() is TextBlock { Text: "Migration Deadline" });
        host.Children.Insert(deadline == null ? host.Children.Count : host.Children.IndexOf(deadline), row);
        try
        {
            var state = await read();
            if (_current != current) return;
            value.Text = state.DetailText();
            value.Foreground = state.IsLocked() ? DeviceUi.ToneBrush(DeviceTone.Attention) : DeviceUi.Theme("TextFillColorPrimaryBrush");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Activation Lock could not be read");
            if (_current == current) value.Text = AppleActivationLock.Unknown.DetailText();
        }
    }

    private async Task AppleCareAsync(StackPanel host, Func<Task<List<AppleCareAgreement>>> read, string current)
    {
        var loading = Note("Loading coverage...");
        host.Children.Add(loading);
        try
        {
            var agreements = await read();
            if (_current != current) return;
            host.Children.Remove(loading);
            if (agreements.Count == 0) { host.Children.Add(Note("No coverage on record.")); return; }
            foreach (var a in agreements) host.Children.Add(Row(DeviceDetailSections.AppleCareRow(a)));
        }
        catch (Exception ex)
        {
            if (_current != current) return;
            host.Children.Remove(loading);
            host.Children.Add(Note($"Coverage could not be read: {ex.Message}"));
        }
    }

    // MARK: - Recovery

    /// <summary>One Reveal button per secret the platform has; each asks first, as every reveal is audited.</summary>
    private void Recovery(StackPanel host, IntuneDevice device)
    {
        foreach (var kind in RecoverySecretKinds.Available(device.OperatingSystem))
        {
            var row = new Grid
            {
                Margin = new Thickness(0, 2, 0, 2),
                ColumnDefinitions = { new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto } },
            };
            row.Children.Add(new TextBlock { Text = kind.DisplayName(), Style = Res("BodyTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center });
            var reveal = new Button { Content = "Reveal…", MinWidth = 88 };
            AutomationProperties.SetName(reveal, $"Reveal {kind.DisplayName()}");
            AutomationProperties.SetAutomationId(reveal, $"Reveal{kind}Button");
            Grid.SetColumn(reveal, 1);
            row.Children.Add(reveal);
            var k = kind;
            reveal.Click += async (_, _) => await RevealAsync(device, k, reveal);
            host.Children.Add(row);
        }
    }

    private async Task RevealAsync(IntuneDevice device, RecoverySecretKind kind, Button button)
    {
        var name = string.IsNullOrWhiteSpace(device.DeviceName) ? device.SerialNumber ?? device.Id : device.DeviceName;
        var ok = await DeviceUi.ConfirmAsync(XamlRoot, new DeviceActionConfirm("Reveal recovery secret",
            $"Show the {kind.DisplayName()} for {name}?\n\nThe request is recorded in the directory audit log."), destructive: true, primary: "Reveal");
        if (!ok) return;
        if (App.Services.DemoMode)
        {
            await DeviceUi.InformAsync(XamlRoot, kind.DisplayName(), "Demo mode: no secret is read or shown.");
            return;
        }
        if (App.Services.Graph is not { } graph) return;

        button.IsEnabled = false;
        try
        {
            var secrets = await graph.RevealRecoverySecretAsync(kind, device);
            // Another device may have been selected while this one loaded.
            if (_device?.Id != device.Id) { secrets.ForEach(s => s.Forget()); return; }
            await new RecoverySecretDialog(name, kind, secrets) { XamlRoot = XamlRoot }.ShowAsync();
        }
        catch (RecoverySecretException ex)
        {
            await DeviceUi.InformAsync(XamlRoot, kind.DisplayName(), ex.Message);
        }
        catch (Exception ex)
        {
            // The type only: an exception is never formatted with a secret in it, but its message is not ours to vouch for.
            Log.Warning("Revealing {Kind} for {DeviceId} failed: {Error}", kind, device.Id, ex.GetType().Name);
            await DeviceUi.InformAsync(XamlRoot, kind.DisplayName(), "FleetMate couldn't read it. Check the elevation status and try again.");
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    // MARK: - Building blocks

    private StackPanel Section(string title)
    {
        var header = new TextBlock
        {
            Text = title,
            Style = Res("BodyStrongTextBlockStyle"),
            Margin = new Thickness(0, ContentPanel.Children.Count == 0 ? 4 : 16, 0, 4),
        };
        AutomationProperties.SetHeadingLevel(header, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level3);
        ContentPanel.Children.Add(header);
        var host = new StackPanel();
        ContentPanel.Children.Add(host);
        return host;
    }

    private static Grid Row(DeviceDetailRow row)
    {
        var grid = new Grid
        {
            ColumnSpacing = 12,
            Margin = new Thickness(0, 2, 0, 2),
            ColumnDefinitions = { new() { Width = new GridLength(128) }, new() { Width = new GridLength(1, GridUnitType.Star) } },
        };
        var label = new TextBlock { Text = row.Label, Style = Res("CaptionTextBlockStyle"), Foreground = DeviceUi.Theme("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        var value = new TextBlock
        {
            Text = row.Value,
            Style = Res("BodyTextBlockStyle"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        if (row.Mono)
        {
            value.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            value.FontSize = 12;
        }
        Grid.SetColumn(value, 1);
        grid.Children.Add(label);
        grid.Children.Add(value);
        return grid;
    }

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        Style = Res("CaptionTextBlockStyle"),
        Foreground = DeviceUi.Theme("TextFillColorSecondaryBrush"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 2),
    };

    private static FrameworkElement StorageBar(double usedPercent)
    {
        var bar = new ProgressBar
        {
            Value = usedPercent,
            Maximum = 100,
            Foreground = DeviceUi.ToneBrush(DeviceDetailSections.StorageTone(usedPercent)),
            Margin = new Thickness(0, 6, 0, 2),
        };
        AutomationProperties.SetName(bar, $"Storage {usedPercent:F1}% used");
        return new StackPanel
        {
            Children =
            {
                bar,
                Note($"{usedPercent:F1}% used"),
            },
        };
    }

    private static Style Res(string key) => (Style)Application.Current.Resources[key];

}
