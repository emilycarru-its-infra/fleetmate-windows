using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FleetMate.Core.Devices;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services;

namespace FleetMate.GUI.Views.Devices;

/// <summary>
/// Compliance troubleshooting lightbox: one policy on one device, opened from
/// the device detail's policy rows. Per-setting states on the left with the
/// failing rows first and highlighted; the policy definition (requirements,
/// grace period and actions, assigned groups) on the right. Copy produces a
/// plain-text, ticket-ready report. What it says comes from FleetMate.Core's
/// <see cref="ComplianceReport"/>, the shared contract with the macOS app and
/// FleetMate.WinUI.
/// </summary>
public partial class CompliancePolicyLightbox : Window
{
    private readonly GraphService _graph;
    private ComplianceReport _report;

    public CompliancePolicyLightbox(IntuneDevice device, DeviceCompliancePolicyState policy,
        GraphService graph, Window? owner = null)
    {
        InitializeComponent();
        _report = new ComplianceReport(device, policy);
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

    /// <summary>The colour each tone has always had here.</summary>
    private static Color ToneColor(DeviceTone tone) => (Color)ColorConverter.ConvertFromString(tone switch
    {
        DeviceTone.Good => "#27ae60",
        DeviceTone.Attention => "#e74c3c",
        DeviceTone.Caution => "#f39c12",
        _ => "#718096",
    });

    private void RenderHeader()
    {
        PolicyNameText.Text = _report.PolicyName;
        PlatformText.Text = _report.Platform ?? "";
        PlatformBadge.Visibility = string.IsNullOrEmpty(_report.Platform) ? Visibility.Collapsed : Visibility.Visible;
        var (tone, label) = _report.StateBadge;
        StateBadge.Background = new SolidColorBrush(ToneColor(tone));
        StateText.Text = label;
        VersionText.Text = _report.VersionText;
        DeviceLineText.Text = _report.DeviceLine;
    }

    private async Task LoadAsync()
    {
        SettingsPanel.Children.Add(Note("Loading setting states..."));
        DefinitionPanel.Children.Add(Note("Loading policy definition..."));
        _report = await ComplianceReportLoader.LoadAsync(new GraphDeviceDetailSource(_graph), _report.Device, _report.Policy);
        RenderHeader();
        RenderSettings();
        RenderDefinition();
    }

    private void RenderSettings()
    {
        SettingsPanel.Children.Clear();
        SettingsPanel.Children.Add(Heading(_report.SettingsHeading));
        var settings = _report.Settings();
        if (settings.Count == 0)
        {
            SettingsPanel.Children.Add(Note(ComplianceReport.NoSettingStates));
            return;
        }

        foreach (var state in settings)
        {
            var bad = Color.FromArgb(0x22, 0xE7, 0x4C, 0x3C);
            var card = new Border
            {
                Background = state.IsBad ? new SolidColorBrush(bad) : (Brush)FindResource("CardBackgroundBrush"),
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
                Text = state.State,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ToneColor(state.Tone)),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(stateLabel, Dock.Right);
            header.Children.Add(stateLabel);
            header.Children.Add(new TextBlock
            {
                Text = state.Name,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });
            stack.Children.Add(header);

            foreach (var line in state.Details)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = line,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            card.Child = stack;
            SettingsPanel.Children.Add(card);
        }
    }

    private void RenderDefinition()
    {
        DefinitionPanel.Children.Clear();
        DefinitionPanel.Children.Add(Heading("Policy Definition"));

        if (_report.DefinitionNote is { } note)
        {
            DefinitionPanel.Children.Add(Note(note));
            return;
        }
        var definition = _report.Definition!;

        if (!string.IsNullOrEmpty(definition.Description))
        {
            DefinitionPanel.Children.Add(new TextBlock
            {
                Text = definition.Description,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
                Margin = new Thickness(0, 0, 0, 8)
            });
        }

        if (definition.Requirements.Count > 0)
        {
            DefinitionPanel.Children.Add(SubHeading("Requirements"));
            foreach (var (key, value) in definition.Requirements.Select(kv => (kv.Key, kv.Value)))
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

        if (definition.ScheduledActions.Count > 0)
        {
            DefinitionPanel.Children.Add(SubHeading("Actions for noncompliance"));
            foreach (var action in definition.ScheduledActions) DefinitionPanel.Children.Add(Bullet(action));
        }

        if (_report.AssignedGroupNames.Count > 0)
        {
            DefinitionPanel.Children.Add(SubHeading("Assigned to"));
            foreach (var name in _report.AssignedGroupNames) DefinitionPanel.Children.Add(Bullet(name));
        }
    }

    // ── Actions ──────────────────────────────────────────────────────────

    private void OnCopyReport(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_report.Text()); } catch { }
        SyncStatusText.Text = "Report copied to the clipboard";
        SyncStatusText.Visibility = Visibility.Visible;
    }

    private void OnOpenInIntune(object sender, RoutedEventArgs e) =>
        FleetMate.Core.Links.ExternalBrowser.Open(_report.IntuneUrl);

    private async void OnSyncDevice(object sender, RoutedEventArgs e)
    {
        SyncStatusText.Text = "Syncing device...";
        SyncStatusText.Visibility = Visibility.Visible;
        var result = (await _graph.SyncDevicesAsync(new[] { _report.Device.Id })).FirstOrDefault();
        SyncStatusText.Text = ComplianceReport.SyncResult(result?.Success == true, result?.Message);
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    // ── Helpers ──────────────────────────────────────────────────────────

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        FontSize = 14,
        Margin = new Thickness(0, 0, 0, 8)
    };

    private static TextBlock SubHeading(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        FontSize = 12,
        Margin = new Thickness(0, 10, 0, 4)
    };

    private static TextBlock Bullet(string text) => new()
    {
        Text = "• " + text,
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 1, 0, 1)
    };

    private TextBlock Note(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
        TextWrapping = TextWrapping.Wrap
    };
}
