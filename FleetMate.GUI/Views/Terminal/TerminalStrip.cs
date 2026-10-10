using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FleetMate.Core.Services.Terminal;
using ModernWpf.Controls;

namespace FleetMate.GUI.Views.Terminal;

/// <summary>
/// What shows across the bottom of the window while the terminal is closed:
/// the agent, the sessions and how to bring them back. Clicking it or
/// dragging it up opens the terminal. Plain text throughout; nothing here is
/// a badge.
/// </summary>
public sealed class TerminalStrip : UserControl
{
    public const double StripHeight = 26;

    private readonly TextBlock _agent = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _status = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Border _surface;
    // Session activity changes without the panel announcing it, so the strip
    // reads it on a slow tick while it is on screen.
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private Point? _pressedAt;

    /// <summary>Raised when the strip is clicked, dragged up or invoked by a screen reader.</summary>
    public event EventHandler? ShowRequested;

    /// <summary>The panel whose sessions the strip describes.</summary>
    public TerminalPanel? Panel { get; set; }

    public TerminalStrip()
    {
        AutomationProperties.SetAutomationId(this, "TerminalStrip");
        Height = StripHeight;
        Cursor = Cursors.Hand;
        ToolTip = "Show the Agent Terminal (Ctrl+`): click or drag up";

        var icon = new FontIcon { Glyph = "", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        var title = new TextBlock { Text = "Agent", FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        _agent.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        var hint = new TextBlock { Text = "Ctrl+`", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumLowBrush");
        var chevron = new FontIcon { Glyph = "", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        chevron.SetResourceReference(ForegroundProperty, "SystemControlForegroundBaseMediumBrush");

        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(icon);
        left.Children.Add(title);
        left.Children.Add(_agent);
        left.Children.Add(_status);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(hint);
        right.Children.Add(chevron);

        var row = new DockPanel { Margin = new Thickness(12, 0, 12, 0), LastChildFill = false };
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        row.Children.Add(left);

        _surface = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = row };
        _surface.SetResourceReference(Border.BorderBrushProperty, "SystemControlForegroundBaseLowBrush");
        _surface.SetResourceReference(Border.BackgroundProperty, "AppBackgroundBrush");
        Content = _surface;

        MouseEnter += (_, _) => _surface.SetResourceReference(Border.BackgroundProperty, "SubtleFillBrush");
        MouseLeave += (_, _) => _surface.SetResourceReference(Border.BackgroundProperty, "AppBackgroundBrush");
        MouseLeftButtonDown += (_, e) => { _pressedAt = e.GetPosition(this); CaptureMouse(); e.Handled = true; };
        MouseMove += (_, e) =>
        {
            // Dragged up past a few pixels: open, as dragging the divider would.
            if (_pressedAt is { } start && e.GetPosition(this).Y < start.Y - 8) Release(show: true);
        };
        MouseLeftButtonUp += (_, e) => { if (_pressedAt != null) Release(show: true); e.Handled = true; };
        LostMouseCapture += (_, _) => _pressedAt = null;

        _tick.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { Refresh(); _tick.Start(); }
            else _tick.Stop();
        };
    }

    private void Release(bool show)
    {
        _pressedAt = null;
        ReleaseMouseCapture();
        if (show) ShowRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Read the default agent and the sessions' state again.</summary>
    public void Refresh()
    {
        _agent.Text = TerminalPanel.DefaultAgentLabel;
        _status.Text = TerminalStripStatus.Describe(Panel?.SessionStates() ?? Array.Empty<ActivityState>());
        AutomationProperties.SetName(this, $"Agent Terminal, {_status.Text}");
    }

    internal void Invoke() => ShowRequested?.Invoke(this, EventArgs.Empty);

    protected override AutomationPeer OnCreateAutomationPeer() => new StripPeer(this);

    /// <summary>Screen readers see the strip as a button that opens the terminal.</summary>
    private sealed class StripPeer : FrameworkElementAutomationPeer, System.Windows.Automation.Provider.IInvokeProvider
    {
        public StripPeer(TerminalStrip owner) : base(owner) { }
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        protected override string GetClassNameCore() => nameof(TerminalStrip);
        public override object GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);
        public void Invoke() => ((TerminalStrip)Owner).Invoke();
    }
}
