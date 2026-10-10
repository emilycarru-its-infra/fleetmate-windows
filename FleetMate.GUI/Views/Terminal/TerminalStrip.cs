using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FleetMate.Core.Services.Terminal;
using ModernWpf.Controls;

namespace FleetMate.GUI.Views.Terminal;

/// <summary>
/// The bar on the window's bottom edge, open or closed: the agent, the
/// sessions, and the controls to show, hide or fill the window with the
/// terminal. While the terminal is closed, clicking the bar or dragging it up
/// opens it; while it is open, dragging the bar down hides it. Plain text
/// throughout; nothing here is a badge.
/// </summary>
public sealed class TerminalStrip : UserControl
{
    public const double StripHeight = 26;

    private readonly TextBlock _agent = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _status = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Border _surface;
    private readonly Button _fullWindow;
    private readonly Button _toggle;
    // Session activity changes without the panel announcing it, so the strip
    // reads it on a slow tick while it is on screen.
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private Point? _pressedAt;
    private bool _showing;

    /// <summary>Raised when the closed bar is clicked or dragged up, or its chevron is pressed.</summary>
    public event EventHandler? ShowRequested;

    /// <summary>Raised when the open terminal's chevron is pressed or the bar is dragged down.</summary>
    public event EventHandler? HideRequested;

    /// <summary>Raised by the full-window button: fill the window with the terminal, or restore it.</summary>
    public event EventHandler? FullWindowRequested;

    /// <summary>The panel whose sessions the strip describes.</summary>
    public TerminalPanel? Panel { get; set; }

    public TerminalStrip()
    {
        AutomationProperties.SetAutomationId(this, "TerminalStrip");
        Height = StripHeight;

        var icon = new FontIcon { Glyph = "", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        var title = new TextBlock { Text = "Agent", FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        _agent.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        // The shortcuts are in the buttons' tooltips, not as hint text on the bar.
        _fullWindow = BarButton("TerminalFullWindowButton", (_, _) => FullWindowRequested?.Invoke(this, EventArgs.Empty));
        _toggle = BarButton("TerminalToggleBarButton", (_, _) =>
        {
            if (_showing) HideRequested?.Invoke(this, EventArgs.Empty);
            else ShowRequested?.Invoke(this, EventArgs.Empty);
        });

        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(icon);
        left.Children.Add(title);
        left.Children.Add(_agent);
        left.Children.Add(_status);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(_fullWindow);
        right.Children.Add(_toggle);

        var row = new DockPanel { Margin = new Thickness(12, 0, 8, 0), LastChildFill = false };
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        row.Children.Add(left);

        _surface = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = row };
        _surface.SetResourceReference(Border.BorderBrushProperty, "SystemControlForegroundBaseLowBrush");
        _surface.SetResourceReference(Border.BackgroundProperty, "AppBackgroundBrush");
        Content = _surface;

        MouseEnter += (_, _) => { if (!_showing) _surface.SetResourceReference(Border.BackgroundProperty, "SubtleFillBrush"); };
        MouseLeave += (_, _) => _surface.SetResourceReference(Border.BackgroundProperty, "AppBackgroundBrush");
        // The buttons handle their own clicks, so a press reaches here only off them.
        MouseLeftButtonDown += (_, e) => { _pressedAt = e.GetPosition(this); CaptureMouse(); e.Handled = true; };
        MouseMove += (_, e) =>
        {
            if (_pressedAt is not { } start) return;
            var y = e.GetPosition(this).Y;
            // Dragged up past a few pixels while closed: open, as dragging the
            // divider would. Dragged down while open: hide.
            if (!_showing && y < start.Y - 8) Release(ShowRequested);
            else if (_showing && y > start.Y + 8) Release(HideRequested);
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (_pressedAt != null) Release(_showing ? null : ShowRequested);
            e.Handled = true;
        };
        LostMouseCapture += (_, _) => _pressedAt = null;

        _tick.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { Refresh(); _tick.Start(); }
            else _tick.Stop();
        };
        SetState(showing: false, fullWindow: false);
    }

    private void Release(EventHandler? raise)
    {
        _pressedAt = null;
        ReleaseMouseCapture();
        raise?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Follow the terminal: whether it is open, and whether it fills the window.</summary>
    public void SetState(bool showing, bool fullWindow)
    {
        _showing = showing;
        var controls = TerminalBarControls.For(showing, fullWindow);
        Cursor = showing ? Cursors.Arrow : Cursors.Hand;
        ToolTip = showing ? null : "Show the Agent Terminal (Ctrl+`): click or drag up";
        if (showing) _surface.SetResourceReference(Border.BackgroundProperty, "AppBackgroundBrush");
        SetButton(_fullWindow, controls.FullWindow ? "" : "", controls.FullWindowTip, controls.FullWindowName);
        SetButton(_toggle, controls.Showing ? "" : "", controls.ToggleTip, controls.ToggleName);
    }

    /// <summary>Read the default agent and the sessions' state again.</summary>
    public void Refresh()
    {
        _agent.Text = TerminalPanel.DefaultAgentLabel;
        _status.Text = TerminalStripStatus.Describe(Panel?.SessionStates() ?? Array.Empty<ActivityState>());
        AutomationProperties.SetName(this, $"Agent Terminal, {_status.Text}");
    }

    private static Button BarButton(string automationId, RoutedEventHandler click)
    {
        var button = new Button
        {
            Content = new FontIcon { FontSize = 10 },
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(2, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Arrow
        };
        AutomationProperties.SetAutomationId(button, automationId);
        button.Click += click;
        return button;
    }

    private static void SetButton(Button button, string glyph, string tip, string name)
    {
        ((FontIcon)button.Content).Glyph = glyph;
        button.ToolTip = tip;
        AutomationProperties.SetName(button, name);
    }
}
