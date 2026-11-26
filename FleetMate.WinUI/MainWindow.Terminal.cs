using FleetMate.Core.Config;
using FleetMate.Core.Services.Terminal;
using FleetMate.Core.Shared;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace FleetMate.WinUI;

/// <summary>
/// The Agent Terminal in the window, as FleetMate.GUI's MainWindow hosts it:
/// the Agent toggle first in the toolbar, the panel below the tab's page on
/// every tab, the divider that sizes it (dragged near the top it fills the
/// window, released near the bottom it closes), the keys that open and size
/// it from anywhere, and Send to Agent's hand-over. TicketsMate has none of it.
/// </summary>
public sealed partial class MainWindow
{
    private readonly TerminalLayoutState _terminalLayout = new();
    private WeakReference<UIElement>? _contentFocus;
    private bool _draggingDivider;
    private TeachingTip? _copyPreview;
    private DispatcherQueueTimer? _copyPreviewTimer;

    private bool HasTerminal => !_edition.IsTicketsOnly;

    private void InitializeTerminal()
    {
        if (!HasTerminal)
        {
            TerminalToggleButton.Visibility = Visibility.Collapsed;
            return;
        }
        // Null in demo mode: fictional sessions, and nothing ever starts.
        Terminal.Initialize(App.Services.AgentHost);
        Terminal.HideRequested += () => SetTerminalVisible(false);
        Terminal.FullWindowRequested += ToggleFullWindow;
        Terminal.IsFullWindow = () => _terminalLayout.FullWindow && Terminal.Visibility == Visibility.Visible;
        Terminal.WorkspaceShowRequested += fullWindow =>
        {
            SetTerminalVisible(true, takeFocus: false);
            _terminalLayout.RestoreMode(fullWindow);
            ApplyTerminalLayout();
        };
        Terminal.ReposRequested += () => { SetTerminalVisible(false); NavigateToTab("Development"); };
        // A row dropped on the terminal goes the way Send to Agent does; anything else dropped there is ignored.
        AgentContextDragState.ActiveChanged += active => DispatcherQueue.TryEnqueue(() => Terminal.SetAgentDragActive(active));
        Terminal.TextDropped += (pane, text) =>
        {
            if (AgentContextDragState.Claim(text, DateTime.UtcNow) is { } block) SendToAgent(block, anchor: null, pane);
        };
        // The terminal opens in the mode it was last used in, split or full window.
        _terminalLayout.RestoreMode(!App.Services.DemoMode && UserPreferences.TerminalLastFullWindow);
        // Choosing a tab shows its page, as on FleetMate.GUI.
        NavView.ItemInvoked += (_, _) => { if (Terminal.Visibility == Visibility.Visible) SetTerminalVisible(false); };
        Activated += (_, e) => { if (e.WindowActivationState != WindowActivationState.Deactivated) Terminal.OnAppActivated(); };
        Closed += (_, _) => Terminal.DisposeAll();
        RootGrid.Loaded += (_, _) => Terminal.StartAtLaunch();
        RootGrid.SizeChanged += (_, _) => { if (Terminal.Visibility == Visibility.Visible) ApplyTerminalLayout(); };

        AddTerminalKey(VirtualKey.J, VirtualKeyModifiers.Control, ToggleTerminal);
        // Ctrl+` (the key left of 1); inside a terminal the page reports it itself.
        AddTerminalKey((VirtualKey)192, VirtualKeyModifiers.Control, ToggleTerminal);
        AddTerminalKey(VirtualKey.T, VirtualKeyModifiers.Control, OpenSessionFromAnywhere);
        AddTerminalKey(VirtualKey.T, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, OpenSessionFromAnywhere);
        AddTerminalKey(VirtualKey.Enter, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, ToggleFullWindow);
        AddTerminalKey(VirtualKey.S, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () =>
        {
            if (Terminal.Visibility == Visibility.Visible) Terminal.ToggleSessionList();
        });
    }

    /// <summary>
    /// A window-wide key for the terminal. WebView2 keeps keys to itself, so
    /// inside a terminal the page reports these; these cover the rest of the app.
    /// </summary>
    private void AddTerminalKey(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, e) =>
        {
            action();
            e.Handled = true;
        };
        RootGrid.KeyboardAccelerators.Add(accelerator);
    }

    private void OpenSessionFromAnywhere()
    {
        SetTerminalVisible(true, takeFocus: false);
        Terminal.OpenDefaultSession();
    }

    private void TerminalToggleButton_Click(object sender, RoutedEventArgs e) => ToggleTerminal();

    public void ToggleTerminal() => SetTerminalVisible(Terminal.Visibility != Visibility.Visible);

    /// <summary>
    /// Show or hide the panel. Showing it never opens a session beyond the
    /// one it opens when there is none; tab changes leave its sessions alone.
    /// Closing it hands the keyboard back to whatever had it when it opened,
    /// so typing never goes to a hidden pane.
    /// </summary>
    public void SetTerminalVisible(bool visible, bool takeFocus = true)
    {
        if (!HasTerminal) return;
        var wasVisible = Terminal.Visibility == Visibility.Visible;
        if (visible && !wasVisible) RememberContentFocus();
        var terminalHadFocus = IsInsideTerminal(FocusManager.GetFocusedElement(RootGrid.XamlRoot) as DependencyObject);

        Terminal.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        TerminalToggleButton.IsChecked = visible;
        TerminalToggleGlyph.Glyph = visible ? "" : AgentContextLabels.AgentGlyph;
        ToolTipService.SetToolTip(TerminalToggleButton, $"{(visible ? "Hide Agent" : "Show Agent")} (Ctrl+J or Ctrl+`)");
        AutomationProperties.SetName(TerminalToggleButton, "Agent");
        ApplyTerminalLayout();
        Terminal.OnVisibilityChanged(visible);
        if (!visible)
        {
            if (wasVisible && terminalHadFocus) ReturnFocusToContent();
            return;
        }
        if (takeFocus) Terminal.FocusActive();
    }

    private void RememberContentFocus()
    {
        var focused = FocusManager.GetFocusedElement(RootGrid.XamlRoot) as UIElement;
        if (focused == null || IsInsideTerminal(focused)) return;
        _contentFocus = new WeakReference<UIElement>(focused);
    }

    private void ReturnFocusToContent()
    {
        if (_contentFocus?.TryGetTarget(out var previous) == true
            && previous is Control { Visibility: Visibility.Visible, XamlRoot: not null } control
            && control.Focus(FocusState.Programmatic))
            return;
        NavFrame.Focus(FocusState.Programmatic);
    }

    private bool IsInsideTerminal(DependencyObject? element)
    {
        for (var node = element; node != null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, Terminal)) return true;
        return false;
    }

    // ── Layout ───────────────────────────────────────────────────────────

    /// <summary>Everything below the toolbar and banners the terminal can take.</summary>
    private double AvailableHeight => Math.Max(0, RootGrid.ActualHeight
        - RootGrid.RowDefinitions[0].ActualHeight - RootGrid.RowDefinitions[1].ActualHeight
        - RootGrid.RowDefinitions[2].ActualHeight - TerminalDivider.Height);

    private void ToggleFullWindow()
    {
        if (!HasTerminal) return;
        if (Terminal.Visibility != Visibility.Visible) SetTerminalVisible(true);
        _terminalLayout.Toggle(AvailableHeight);
        ApplyTerminalLayout();
    }

    /// <summary>
    /// Full window hides the tab's page behind the terminal and keeps the tab
    /// bar; otherwise the panel has its height. Closed, its row is empty.
    /// </summary>
    private void ApplyTerminalLayout()
    {
        var visible = Terminal.Visibility == Visibility.Visible;
        var full = visible && _terminalLayout.FullWindow;
        if (visible && !App.Services.DemoMode && _terminalLayout.FullWindow != UserPreferences.TerminalLastFullWindow)
            UserPreferences.SetTerminalLastFullWindow(_terminalLayout.FullWindow);
        TerminalDivider.Visibility = visible && !full ? Visibility.Visible : Visibility.Collapsed;
        Terminal.SetLayoutState(full);
        NavFrame.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
        ContentRow.Height = full ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        TerminalRow.Height = !visible ? GridLength.Auto
            : full ? new GridLength(1, GridUnitType.Star)
            : new GridLength(_terminalLayout.DisplayHeight);
    }

    private void TerminalDivider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _terminalLayout.BeginDrag();
        _draggingDivider = TerminalDivider.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    /// <summary>
    /// The height is where the pointer is in the window, not an offset from
    /// the moving divider, so the drag tracks the pointer without jitter.
    /// </summary>
    private void TerminalDivider_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingDivider) return;
        var pointer = e.GetCurrentPoint(RootGrid).Position.Y;
        _terminalLayout.Drag(RootGrid.ActualHeight - pointer - TerminalDivider.Height / 2, AvailableHeight);
        ApplyTerminalLayout();
    }

    /// <summary>Released near the bottom, the panel hides and keeps its height for next time.</summary>
    private void TerminalDivider_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingDivider) return;
        _draggingDivider = false;
        TerminalDivider.ReleasePointerCapture(e.Pointer);
        if (_terminalLayout.EndDrag()) SetTerminalVisible(false);
        else ApplyTerminalLayout();
    }

    private void TerminalDivider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingDivider) return;
        _draggingDivider = false;
        _terminalLayout.EndDrag();
        ApplyTerminalLayout();
    }

    private void TerminalDivider_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        SetTerminalVisible(false);
        e.Handled = true;
    }

    // ── Send to Agent ────────────────────────────────────────────────────

    /// <summary>
    /// Hand <paramref name="text"/> to the agent: pasted into a running
    /// agent's input, never submitted. With no agent running, nothing is
    /// started on the person's behalf: the text is copied and the terminal
    /// opens idle, so the person pastes it alongside their own request.
    /// Returns true when the text was pasted, false when it was copied.
    /// <paramref name="pane"/> is the pane a row was dropped on, tried first.
    /// </summary>
    public bool SendToAgent(string text, FrameworkElement? anchor, TerminalPaneController? pane = null)
    {
        if (!HasTerminal) return false;
        SetTerminalVisible(true, takeFocus: false);
        if (Terminal.Insert(text, pane)) return true;
        Shell.AgentHandOff.CopyWithPreview(text, anchor ?? Terminal.ViewFor(pane));
        if (Terminal.HasSessions) Terminal.FocusActive();
        else Terminal.OpenDefaultSession();
        return false;
    }

    /// <summary>A short-lived, selectable preview of the text actually copied, beside what was clicked.</summary>
    public void ShowCopyPreview(string text, FrameworkElement? anchor)
    {
        if (_copyPreview == null)
        {
            _copyPreview = new TeachingTip
            {
                Title = "Copied",
                IsLightDismissEnabled = true,
                PreferredPlacement = TeachingTipPlacementMode.Auto,
            };
            AutomationProperties.SetAutomationId(_copyPreview, "CopyPreview");
            Grid.SetRowSpan(_copyPreview, RootGrid.RowDefinitions.Count);
            RootGrid.Children.Add(_copyPreview);
            _copyPreviewTimer = DispatcherQueue.CreateTimer();
            _copyPreviewTimer.Interval = TimeSpan.FromSeconds(5);
            _copyPreviewTimer.IsRepeating = false;
            _copyPreviewTimer.Tick += (_, _) => { if (_copyPreview != null) _copyPreview.IsOpen = false; };
        }
        _copyPreview.IsOpen = false;
        _copyPreview.Content = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            MaxHeight = 300,
            MinWidth = 260,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            Header = "Paste it with your request.",
        };
        _copyPreview.Target = anchor is { XamlRoot: not null, Visibility: Visibility.Visible } ? anchor : null;
        _copyPreview.IsOpen = true;
        _copyPreviewTimer!.Stop();
        _copyPreviewTimer.Start();
    }

    // ── QA capture ───────────────────────────────────────────────────────

    /// <summary>Open the terminal over the current tab, split or full window, for <see cref="Shell.CaptureHarness"/>; null closes it.</summary>
    internal void ShowTerminalForCapture(bool? fullWindow)
    {
        if (!HasTerminal) return;
        if (fullWindow is not { } full)
        {
            SetTerminalVisible(false);
            return;
        }
        _terminalLayout.RestoreMode(full);
        SetTerminalVisible(true, takeFocus: false);
    }

    internal bool CapturesTerminal => HasTerminal;
}
