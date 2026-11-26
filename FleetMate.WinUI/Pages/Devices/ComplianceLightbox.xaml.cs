using FleetMate.Core.Devices;
using FleetMate.Core.Links;
using FleetMate.Core.Models.Devices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Serilog;

namespace FleetMate.WinUI.Pages.Devices;

/// <summary>
/// The compliance troubleshooting lightbox: one policy on one device, opened
/// from the detail pane's policy rows. Per-setting states on the left, the
/// failing ones first and highlighted; the policy definition (requirements,
/// actions for noncompliance, assigned groups) on the right. Copy puts the
/// ticket-ready report on the clipboard. Everything it says comes from
/// FleetMate.Core's <see cref="ComplianceReport"/>, the shared contract with the Mac.
/// </summary>
public sealed partial class ComplianceLightbox : UserControl
{
    private ComplianceReport? _report;
    private UIElement? _returnFocus;

    public ComplianceLightbox()
    {
        InitializeComponent();
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>Open on <paramref name="policy"/> for <paramref name="device"/> and load its states and definition.</summary>
    public async Task ShowAsync(IntuneDevice device, DeviceCompliancePolicyState policy, UIElement? returnFocus = null)
    {
        _returnFocus = returnFocus;
        _report = new ComplianceReport(device, policy);
        StatusText.Visibility = Visibility.Collapsed;
        RenderHeader();
        SettingsPanel.Children.Clear();
        SettingsPanel.Children.Add(Note("Loading setting states..."));
        DefinitionPanel.Children.Clear();
        DefinitionPanel.Children.Add(Note("Loading policy definition..."));
        Visibility = Visibility.Visible;
        CloseButton.Focus(FocusState.Programmatic);

        if (App.Services.DeviceDetails is not { } source) return;
        try
        {
            var loaded = await ComplianceReportLoader.LoadAsync(source, device, policy);
            if (!ReferenceEquals(_report.Policy, policy)) return;
            _report = loaded;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load compliance details for policy {PolicyId}", policy.Id);
        }
        RenderHeader();
        RenderSettings();
        RenderDefinition();
    }

    public void Close()
    {
        Visibility = Visibility.Collapsed;
        _returnFocus?.Focus(FocusState.Programmatic);
    }

    private void Lightbox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape) return;
        e.Handled = true;
        Close();
    }

    private void Scrim_Tapped(object sender, TappedRoutedEventArgs e) => Close();

    // A tap on the card stays on the card.
    private void Card_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Side by side when there is room, stacked when the window is narrow.</summary>
    private void Columns_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var stacked = e.NewSize.Width < 760;
        DefinitionColumn.Width = stacked ? new GridLength(0) : new GridLength(400);
        DefinitionRow.Height = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(DefinitionCard, stacked ? 0 : 1);
        Grid.SetRow(DefinitionCard, stacked ? 1 : 0);
    }

    // MARK: - Rendering

    private void RenderHeader()
    {
        if (_report is not { } r) return;
        PolicyNameText.Text = r.PolicyName;
        PlatformText.Text = r.Platform ?? "";
        PlatformBadge.Visibility = DeviceUi.Show(!string.IsNullOrEmpty(r.Platform));
        var (tone, label) = r.StateBadge;
        StateBadgeHost.Content = DeviceUi.Badge(tone, label);
        VersionText.Text = r.VersionText;
        DeviceLineText.Text = r.DeviceLine;
    }

    private void RenderSettings()
    {
        if (_report is not { } r) return;
        SettingsPanel.Children.Clear();
        SettingsPanel.Children.Add(Heading(r.SettingsHeading));
        var settings = r.Settings();
        if (settings.Count == 0)
        {
            SettingsPanel.Children.Add(Note(ComplianceReport.NoSettingStates));
            return;
        }
        foreach (var s in settings)
        {
            var stack = new StackPanel { Spacing = 2 };
            var header = new Grid
            {
                ColumnSpacing = 8,
                ColumnDefinitions = { new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto } },
            };
            header.Children.Add(new TextBlock { Text = s.Name, Style = Style("BodyStrongTextBlockStyle"), TextWrapping = TextWrapping.Wrap });
            var state = DeviceUi.Badge(s.Tone, s.State);
            Grid.SetColumn(state, 1);
            header.Children.Add(state);
            stack.Children.Add(header);
            foreach (var line in s.Details) stack.Children.Add(Note(line));

            SettingsPanel.Children.Add(new Border
            {
                Background = s.IsBad ? DeviceUi.ToneBackground(DeviceTone.Attention) : DeviceUi.Theme("CardBackgroundFillColorDefaultBrush"),
                BorderBrush = s.IsBad ? DeviceUi.ToneBrush(DeviceTone.Attention) : DeviceUi.Theme("CardStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 7, 10, 7),
                Child = stack,
            });
        }
    }

    private void RenderDefinition()
    {
        if (_report is not { } r) return;
        DefinitionPanel.Children.Clear();
        DefinitionPanel.Children.Add(Heading("Policy Definition"));
        if (r.DefinitionNote is { } note)
        {
            DefinitionPanel.Children.Add(Note(note));
            return;
        }
        var d = r.Definition!;
        if (!string.IsNullOrEmpty(d.Description))
        {
            var description = Note(d.Description);
            description.Margin = new Thickness(0, 0, 0, 8);
            DefinitionPanel.Children.Add(description);
        }
        if (d.Requirements.Count > 0)
        {
            DefinitionPanel.Children.Add(SubHeading("Requirements"));
            foreach (var (key, value) in d.Requirements.Select(kv => (kv.Key, kv.Value)))
            {
                var grid = new Grid
                {
                    ColumnSpacing = 12,
                    Margin = new Thickness(0, 2, 0, 2),
                    ColumnDefinitions = { new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = new GridLength(1, GridUnitType.Star) } },
                };
                grid.Children.Add(Note(key));
                var v = new TextBlock { Text = value, Style = Style("BodyTextBlockStyle"), TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(v, 1);
                grid.Children.Add(v);
                DefinitionPanel.Children.Add(grid);
            }
        }
        if (d.ScheduledActions.Count > 0)
        {
            DefinitionPanel.Children.Add(SubHeading("Actions for noncompliance"));
            foreach (var action in d.ScheduledActions) DefinitionPanel.Children.Add(Bullet(action));
        }
        if (r.AssignedGroupNames.Count > 0)
        {
            DefinitionPanel.Children.Add(SubHeading("Assigned to"));
            foreach (var name in r.AssignedGroupNames) DefinitionPanel.Children.Add(Bullet(name));
        }
    }

    // MARK: - Actions

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_report is not { } r) return;
        Development.DevelopmentUi.Copy(r.Text());
        Status("Report copied to the clipboard");
    }

    private void OpenInIntune_Click(object sender, RoutedEventArgs e)
    {
        if (_report is { } r) ExternalBrowser.Open(r.IntuneUrl);
    }

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (_report is not { } r) return;
        if (App.Services.DemoMode)
        {
            Status(DeviceActionText.Demo("Sync"));
            return;
        }
        if (App.Services.Graph is not { } graph) return;
        Status("Syncing device...");
        try
        {
            var result = (await graph.SyncDevicesAsync(new[] { r.Device.Id })).FirstOrDefault();
            Status(ComplianceReport.SyncResult(result?.Success == true, result?.Message));
        }
        catch (Exception ex)
        {
            Status(ComplianceReport.SyncResult(false, ex.Message));
        }
    }

    private void Status(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
    }

    // MARK: - Pieces

    private static TextBlock Heading(string text)
    {
        var block = new TextBlock { Text = text, Style = Style("BodyStrongTextBlockStyle"), Margin = new Thickness(0, 0, 0, 6) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHeadingLevel(block, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level3);
        return block;
    }

    private static TextBlock SubHeading(string text) => new()
    {
        Text = text, Style = Style("BodyStrongTextBlockStyle"), Margin = new Thickness(0, 10, 0, 4),
    };

    private static TextBlock Bullet(string text) => new()
    {
        Text = "• " + text, Style = Style("BodyTextBlockStyle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1),
    };

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        Style = Style("CaptionTextBlockStyle"),
        Foreground = DeviceUi.Theme("TextFillColorSecondaryBrush"),
        TextWrapping = TextWrapping.Wrap,
    };

    private static Style Style(string key) => (Style)Application.Current.Resources[key];
}
