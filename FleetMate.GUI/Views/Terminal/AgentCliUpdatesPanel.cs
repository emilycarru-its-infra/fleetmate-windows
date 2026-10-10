using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using FleetMate.Core.Config;
using FleetMate.Core.Services.Agent;
using Microsoft.Win32;

namespace FleetMate.GUI.Views.Terminal;

/// <summary>
/// Settings › Terminal › Agent CLIs: each CLI's version and install method,
/// when it was last checked, and the switch for keeping them current.
/// Problems are reported as a plain status line; the detail is in the app log.
/// </summary>
public sealed class AgentCliUpdatesPanel : UserControl
{
    private readonly CheckBox _keepCurrent = new() { Content = "Keep agent CLIs up to date", Margin = new Thickness(0, 0, 0, 8) };
    private readonly StackPanel _rows = new();
    private readonly TextBlock _status = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _updateNow = new() { Content = "Update Now" };
    private AgentCliUpdateModel? _model;

    public AgentCliUpdatesPanel()
    {
        AutomationProperties.SetAutomationId(_keepCurrent, "AgentKeepClisCurrentCheckBox");
        AutomationProperties.SetAutomationId(_updateNow, "AgentCliUpdateNowButton");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");

        var title = new TextBlock { Text = "Agent CLIs", FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 0, 0, 10) };
        var footer = new TextBlock
        {
            Text = "FleetMate updates codex and claude in the background with whatever installed them (npm, winget " +
                   "or Claude's own installer) at launch and every six hours, so a session starts straight away " +
                   "instead of on an update. It never installs a missing CLI or asks for elevation, and leaves an " +
                   "npm or winget install a session is using until it is free. While this is on, the CLIs skip " +
                   "their own update checks.",
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 10, 0, 0),
        };
        footer.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");

        var actions = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(_updateNow, Dock.Right);
        actions.Children.Add(_updateNow);
        actions.Children.Add(_status);

        var panel = new StackPanel();
        panel.Children.Add(title);
        panel.Children.Add(_keepCurrent);
        panel.Children.Add(_rows);
        panel.Children.Add(actions);
        panel.Children.Add(footer);
        Content = panel;

        _keepCurrent.Click += (_, _) => SaveKeepCurrent(_keepCurrent.IsChecked == true);
        _updateNow.Click += async (_, _) => { if (_model != null) await _model.RunAsync(checkOnly: false); };
        Loaded += OnLoaded;
        Unloaded += (_, _) => { if (_model != null) _model.PropertyChanged -= OnModelChanged; };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Application.Current is not App app) return;
        _model = app.Agent.Updater;
        _model.PropertyChanged -= OnModelChanged;
        _model.PropertyChanged += OnModelChanged;
        _keepCurrent.IsChecked = app.Config.Terminal.KeepClisCurrent;
        Render();
        // Fill in versions the first time Settings is opened.
        if (_model.State.Statuses.Count == 0 && !_model.IsRunning) _ = _model.RunAsync(checkOnly: true);
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        Dispatcher.BeginInvoke(Render);

    /// <summary>Saved at once, like the Mac's switch; the operator's value wins over policy.</summary>
    private void SaveKeepCurrent(bool on)
    {
        if (Application.Current is not App app) return;
        var terminal = app.Config.Terminal;
        using (var key = Registry.CurrentUser.CreateSubKey(AppEdition.Current.UserRegistryPath))
        {
            if (on == (terminal.PolicyKeepClisCurrent ?? TerminalSettings.DefaultKeepClisCurrent))
                key.DeleteValue("AgentKeepClisCurrent", throwOnMissingValue: false);
            else
                key.SetValue("AgentKeepClisCurrent", on ? "1" : "0");
        }
        terminal.UserKeepClisCurrent = on == (terminal.PolicyKeepClisCurrent ?? TerminalSettings.DefaultKeepClisCurrent) ? null : on;
        if (on) _model?.UpdateIfStale();
    }

    private void Render()
    {
        if (_model == null) return;
        _rows.Children.Clear();
        foreach (var cli in Enum.GetValues<AgentCli>())
            _rows.Children.Add(Row(cli, _model.State.Statuses.FirstOrDefault(s => s.Cli == cli)));
        _updateNow.IsEnabled = !_model.IsRunning;
        _status.Text = _model.IsRunning ? "Checking…"
            : _model.State.LastChecked is { } last ? $"Last updated {Relative(last)}" : "";
    }

    private static FrameworkElement Row(AgentCli cli, AgentCliStatus? status)
    {
        var name = new TextBlock { Text = cli.DisplayName() };
        var version = new TextBlock
        {
            Text = status?.Installed == false ? "Not installed" : status?.Version ?? "—",
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
        };
        version.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        var top = new DockPanel();
        DockPanel.SetDock(version, Dock.Right);
        top.Children.Add(version);
        top.Children.Add(name);

        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        row.Children.Add(top);
        if (status is { Installed: true })
        {
            var parts = new List<string>();
            if (status.Method is { } method) parts.Add(method.DisplayName());
            if (status.LastChecked is { } checkedAt) parts.Add($"checked {Relative(checkedAt)}");
            if (status.Message != null) parts.Add(status.Message);
            var detail = new TextBlock { Text = string.Join(" · ", parts), FontSize = 11, TextWrapping = TextWrapping.Wrap };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
            row.Children.Add(detail);
        }
        return row;
    }

    private static string Relative(DateTimeOffset when)
    {
        var ago = DateTimeOffset.UtcNow - when;
        if (ago < TimeSpan.FromMinutes(1)) return "just now";
        if (ago < TimeSpan.FromHours(1)) return $"{(int)ago.TotalMinutes} min ago";
        if (ago < TimeSpan.FromDays(1)) return $"{(int)ago.TotalHours} h ago";
        return when.LocalDateTime.ToString("g");
    }
}
