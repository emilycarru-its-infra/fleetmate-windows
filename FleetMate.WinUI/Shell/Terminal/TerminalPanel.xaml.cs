using FleetMate.Core.Config;
using FleetMate.Core.Services.Terminal;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace FleetMate.WinUI.Shell.Terminal;

/// <summary>
/// The Agent Terminal panel, reachable from every tab. Sessions are a list
/// down the left: each row shows the session's title, its directory, an
/// activity dot and its Ctrl+number; the list folds to just the dots. A
/// session can split into two panes. What the list holds and does is
/// <see cref="TerminalSessionList"/> in FleetMate.Core, shared with
/// FleetMate.GUI; this draws it in WinUI. Call <see cref="Initialize"/> once.
/// </summary>
public sealed partial class TerminalPanel : UserControl
{
    /// <summary>The visuals of one session: its row and the grid its panes sit in.</summary>
    private sealed class RowVisuals
    {
        public required Grid Row { get; init; }
        public required Button Surface { get; init; }
        public required Grid Body { get; init; }
        public required Ellipse Dot { get; init; }
        public required TextBlock TitleText { get; init; }
        public required TextBlock DirectoryText { get; init; }
        public required FontIcon StatusIcon { get; init; }
        public required TextBlock StatusText { get; init; }
        public required StackPanel Status { get; init; }
        public required TextBlock HintText { get; init; }
        public required StackPanel Details { get; init; }
        public required Button Close { get; init; }
        public List<TerminalPaneView> Views { get; } = new();
    }

    private const double DefaultListWidth = 230;
    private const double CollapsedListWidth = 34;
    private const double MinListWidth = 180;
    private const double MaxListWidth = 520;
    /// <summary>Rows drag as this private format, so a row dropped on a terminal is not pasted as text.</summary>
    private const string SessionDragFormat = "FleetMate.TerminalSession";

    private static double SavedListWidth => Math.Clamp(UserPreferences.TerminalListWidth ?? DefaultListWidth, MinListWidth, MaxListWidth);

    private TerminalSessionList _list = null!;
    private readonly Dictionary<TerminalSession, RowVisuals> _visuals = new();
    private readonly Dictionary<TerminalFilterKind, ToggleButton> _filterButtons = new();
    private DispatcherQueueTimer? _tick;
    private DispatcherQueueTimer? _repoTimer;
    private TerminalSession? _dragging;
    private double? _splitterStart;
    private double _splitterWidth;
    private bool _syncingFilters;

    public TerminalPanel()
    {
        InitializeComponent();
        ListColumn.Width = new GridLength(SavedListWidth);
    }

    /// <summary>The terminal should hide: its header's button, or Ctrl+` inside a terminal.</summary>
    public event Action? HideRequested;

    /// <summary>Full-window mode should turn on or off: its header's button, or Ctrl+Shift+Enter inside a terminal.</summary>
    public event Action? FullWindowRequested;

    /// <summary>A workspace opened: show the terminal, split (false) or full window (true).</summary>
    public event Action<bool>? WorkspaceShowRequested;

    /// <summary>Show the Repos tab, where a repository is cloned.</summary>
    public event Action? ReposRequested;

    /// <summary>Text was dropped on a pane; the window decides whether it is a row's block to hand over.</summary>
    public event Action<TerminalPaneController, string>? TextDropped;

    public bool HasSessions => _list.HasSessions;

    /// <summary>Whether the terminal fills the window now, saved with a workspace. Set by the window.</summary>
    public Func<bool>? IsFullWindow { set => _list.IsFullWindow = value; }

    /// <summary>
    /// Build the list over <paramref name="agent"/>: null in demo mode, where
    /// the sessions are fictional and nothing starts.
    /// </summary>
    public void Initialize(AgentSessionHost? agent)
    {
        var queue = DispatcherQueue;
        _list = new TerminalSessionList(
            () => App.Services.Config.Terminal,
            agent,
            action => queue.TryEnqueue(() => action()),
            action => queue.TryEnqueue(DispatcherQueuePriority.Low, () => action()),
            () => ActualTheme == ElementTheme.Dark);
        _list.SessionAdded += OnSessionAdded;
        _list.SessionRemoved += OnSessionRemoved;
        _list.SessionChanged += Refresh;
        _list.RowsChanged += RebuildRows;
        _list.PaneAdded += AddPaneView;
        _list.PanesCleared += ClearPaneViews;
        _list.ActiveChanged += ShowActive;
        _list.FiltersChanged += UpdateFilterControls;
        _list.ListCollapsedChanged += ApplyListCollapsed;
        _list.HideRequested += () => HideRequested?.Invoke();
        _list.FullWindowRequested += () => FullWindowRequested?.Invoke();
        _list.WorkspaceShowRequested += full => WorkspaceShowRequested?.Invoke(full);
        _list.ReposRequested += () => ReposRequested?.Invoke();
        _list.TextDropped += (pane, text) => TextDropped?.Invoke(pane, text);
        _list.ErrorRaised += message => _ = ShowMessageAsync("Terminal Workspaces", message);

        foreach (var filter in _list.FilterButtons())
        {
            var kind = filter.Kind;
            var button = new ToggleButton
            {
                Padding = new Thickness(6, 3, 6, 3),
                MinWidth = 0,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 3,
                    Children =
                    {
                        new FontIcon { Glyph = filter.Glyph, FontSize = 11 },
                        new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            };
            AutomationProperties.SetAutomationId(button, filter.AutomationId);
            button.Click += (_, _) => { if (!_syncingFilters) _list.ToggleFilter(kind); };
            _filterButtons[kind] = button;
            FilterRow.Children.Insert(FilterRow.Children.Count - 1, button);
        }
        UpdateFilterControls();
        SetLayoutState(fullWindow: false);

        // Activity dots decay, and shells that never report their directory are polled.
        _tick = queue.CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(500);
        _tick.Tick += (_, _) => _list.Tick();
        _tick.Start();
        // The Repos rows follow the registry within five seconds of a change.
        _repoTimer = queue.CreateTimer();
        _repoTimer.Interval = TimeSpan.FromSeconds(5);
        _repoTimer.Tick += (_, _) => _list.RepoTick();
        _repoTimer.Start();

        // The text follows the app's text size, and the page the app's theme.
        UserPreferences.Changed += OnPreferencesChanged;
        ActualThemeChanged += (_, _) => _list.ApplyTheme(ActualTheme == ElementTheme.Dark);
    }

    private void OnPreferencesChanged() => DispatcherQueue.TryEnqueue(_list.ApplyFontSize);

    // ── The list's surface for the window ────────────────────────────────

    public void OpenDefaultSession(bool takeFocus = true) => _list.OpenDefaultSession(takeFocus);

    public void SetAgentDragActive(bool active) => _list.SetAgentDragActive(active);

    public void FocusActive() => _list.FocusActive();

    public bool Insert(string text, TerminalPaneController? preferred = null) => _list.Insert(text, preferred);

    public bool HandleAppKey(TerminalAction action, int index) => _list.HandleAppKey(action, index);

    public void ToggleSessionList() => _list.ToggleSessionList();

    /// <summary>Repos rows, the sessions open at the last quit, then the launch workspace or the auto-started session.</summary>
    public void StartAtLaunch() => _list.StartAtLaunch();

    /// <summary>The app came to the front.</summary>
    public void OnAppActivated() => _list.OnAppActivated();

    /// <summary>The pane's control, for anchoring the copy preview.</summary>
    public FrameworkElement? ViewFor(TerminalPaneController? pane) =>
        pane == null ? null : _visuals.Values.SelectMany(v => v.Views).FirstOrDefault(v => ReferenceEquals(v.Controller, pane));

    /// <summary>The panel was shown or hidden, so bells from a hidden panel count as unseen.</summary>
    public void OnVisibilityChanged(bool visible) => _list.SetVisible(visible);

    public void DisposeAll()
    {
        _tick?.Stop();
        _repoTimer?.Stop();
        UserPreferences.Changed -= OnPreferencesChanged;
        _list.DisposeAll();
    }

    // ── Sessions and panes ───────────────────────────────────────────────

    private void OnSessionAdded(TerminalSession session)
    {
        var visuals = BuildRow(session);
        _visuals[session] = visuals;
        visuals.Body.Visibility = Visibility.Collapsed;
        Body.Children.Add(visuals.Body);
        ApplyCollapsedTo(visuals);
        Refresh(session);
    }

    private void OnSessionRemoved(TerminalSession session)
    {
        if (!_visuals.Remove(session, out var visuals)) return;
        Body.Children.Remove(visuals.Body);
    }

    private void AddPaneView(TerminalSession session, TerminalPaneController pane)
    {
        if (!_visuals.TryGetValue(session, out var visuals)) return;
        var view = new TerminalPaneView(pane);
        AutomationProperties.SetAutomationId(view, $"TerminalPane{visuals.Views.Count}");
        visuals.Views.Add(view);

        var grid = visuals.Body;
        if (visuals.Views.Count > 1)
        {
            // A divider between the two panes; dragging it moves the share between them.
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var splitter = new ResizeHandle(vertical: false)
            {
                Width = 4,
                Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            };
            AutomationProperties.SetAutomationId(splitter, "TerminalPaneSplitter");
            AutomationProperties.SetName(splitter, "Pane divider");
            AttachPaneSplitter(splitter, grid, grid.ColumnDefinitions.Count - 2);
            Grid.SetColumn(splitter, grid.ColumnDefinitions.Count - 1);
            grid.Children.Add(splitter);
        }
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(view, grid.ColumnDefinitions.Count - 1);
        grid.Children.Add(view);
    }

    /// <summary>Drag the divider between two panes: the left column takes the pointer's share of the width.</summary>
    private static void AttachPaneSplitter(ResizeHandle splitter, Grid grid, int leftColumn)
    {
        var dragging = false;
        splitter.PointerPressed += (_, e) => { dragging = splitter.CapturePointer(e.Pointer); e.Handled = true; };
        splitter.PointerMoved += (_, e) =>
        {
            if (!dragging || grid.ActualWidth <= 0) return;
            var x = Math.Clamp(e.GetCurrentPoint(grid).Position.X, 120, Math.Max(120, grid.ActualWidth - 120));
            grid.ColumnDefinitions[leftColumn].Width = new GridLength(x, GridUnitType.Star);
            grid.ColumnDefinitions[leftColumn + 2].Width = new GridLength(grid.ActualWidth - x, GridUnitType.Star);
        };
        splitter.PointerReleased += (_, e) => { dragging = false; splitter.ReleasePointerCapture(e.Pointer); };
        splitter.PointerCaptureLost += (_, _) => dragging = false;
    }

    private void ClearPaneViews(TerminalSession session)
    {
        if (!_visuals.TryGetValue(session, out var visuals)) return;
        foreach (var view in visuals.Views) view.Dispose();
        visuals.Views.Clear();
        visuals.Body.Children.Clear();
        visuals.Body.ColumnDefinitions.Clear();
    }

    /// <summary>Only the active session's panes are on screen, and its row is highlighted.</summary>
    private void ShowActive(TerminalSession? active)
    {
        foreach (var (session, visuals) in _visuals)
        {
            var on = ReferenceEquals(session, active);
            visuals.Body.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            visuals.Row.Background = on ? Theme("SubtleFillColorSecondaryBrush") : null;
        }
    }

    // ── Rows ─────────────────────────────────────────────────────────────

    private RowVisuals BuildRow(TerminalSession session)
    {
        var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(2, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        var directory = new TextBlock { FontSize = 11, Style = SecondaryText };
        var statusIcon = new FontIcon { FontSize = 9, Style = (Style)Resources["TerminalSecondaryIconStyle"], VerticalAlignment = VerticalAlignment.Center };
        var statusText = new TextBlock { FontSize = 10, Style = SecondaryText, VerticalAlignment = VerticalAlignment.Center };
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 1, 0, 0), Children = { statusIcon, statusText } };
        var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, directory, status } };
        var hint = new TextBlock { FontSize = 10, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Style = SecondaryText };

        var content = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        Grid.SetColumn(details, 1);
        Grid.SetColumn(hint, 2);
        content.Children.Add(dot);
        content.Children.Add(details);
        content.Children.Add(hint);

        // The row is a button, so it takes Tab, Enter, Space and Shift+F10.
        var surface = new Button
        {
            Content = content,
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(4, 5, 2, 5),
            CanDrag = true,
        };
        surface.Click += (_, _) => _list.Activate(session, true);
        var menu = new MenuFlyout();
        menu.Opening += (_, _) => FillRowMenu(menu, session, surface);
        surface.ContextFlyout = menu;
        surface.DragStarting += (_, e) =>
        {
            _dragging = session;
            e.Data.SetData(SessionDragFormat, session.GetHashCode().ToString());
            e.Data.RequestedOperation = DataPackageOperation.Move;
        };
        surface.DropCompleted += (_, _) => _dragging = null;

        var close = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 9 },
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Padding = new Thickness(6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        close.Click += (_, _) => _list.Close(session);

        var row = new Grid
        {
            Margin = new Thickness(4, 1, 4, 1),
            CornerRadius = (CornerRadius)Application.Current.Resources["ControlCornerRadius"],
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        Grid.SetColumn(close, 1);
        row.Children.Add(surface);
        row.Children.Add(close);
        AcceptSessionDrops(row, dropped => _list.MoveSession(dropped, before: session, section: session.Section));

        return new RowVisuals
        {
            Row = row, Surface = surface, Body = new Grid(), Dot = dot, TitleText = title, DirectoryText = directory,
            StatusIcon = statusIcon, StatusText = statusText, Status = status, HintText = hint, Details = details, Close = close,
        };
    }

    private void FillRowMenu(MenuFlyout menu, TerminalSession session, FrameworkElement anchor)
    {
        menu.Items.Clear();
        menu.Items.Add(Item("Rename Session…", () => EditName(anchor, "Rename session", session.DisplayName, name => _list.Rename(session, name))));
        menu.Items.Add(Item("Use Automatic Name", () => _list.UseAutomaticName(session), enabled: session.CustomName != null));
        menu.Items.Add(Item(session.Pinned ? "Unpin" : "Pin", () => _list.TogglePin(session)));
        var move = new MenuFlyoutSubItem { Text = "Move to Section" };
        foreach (var section in _list.Sections)
        {
            var target = section;
            move.Items.Add(Item(target, () => _list.MoveSession(session, before: null, section: target)));
        }
        menu.Items.Add(move);
        var switchAgent = new MenuFlyoutSubItem { Text = "Switch Agent" };
        AutomationProperties.SetAutomationId(switchAgent, "TerminalSwitchAgentMenu");
        foreach (var (command, label) in TerminalSessionActions.Presets)
        {
            var preset = command;
            var item = new ToggleMenuFlyoutItem
            {
                Text = label,
                IsChecked = TerminalSessionActions.IsCurrent(preset, session.Command),
                IsEnabled = _list.IsPresetInstalled(preset),
            };
            item.Click += async (_, _) => await SwitchAgentAsync(session, preset);
            switchAgent.Items.Add(item);
        }
        menu.Items.Add(switchAgent);
        var duplicate = new MenuFlyoutSubItem { Text = "Duplicate Session" };
        AutomationProperties.SetAutomationId(duplicate, "TerminalDuplicateSessionMenu");
        duplicate.Items.Add(Item("Same Agent", () => _list.DuplicateSession(session, preset: null)));
        duplicate.Items.Add(new MenuFlyoutSeparator());
        foreach (var (command, label) in TerminalSessionActions.Presets)
        {
            var preset = command;
            duplicate.Items.Add(Item(label, () => _list.DuplicateSession(session, preset), enabled: _list.IsPresetInstalled(preset)));
        }
        menu.Items.Add(duplicate);
        menu.Items.Add(new MenuFlyoutSeparator());
        var folder = session.Folder;
        menu.Items.Add(Item("Copy Path", () => CopyText(folder), enabled: folder != null, glyph: ""));
        menu.Items.Add(Item("Show in Explorer", () => TerminalSessionList.ShowInExplorer(folder!),
            enabled: !_list.IsDemo && folder != null && Directory.Exists(folder), glyph: ""));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Scroll to Top", () => session.Panes.FirstOrDefault()?.ScrollToTop(), enabled: session.IsStarted));
        menu.Items.Add(Item("Scroll to Bottom", () => session.Panes.FirstOrDefault()?.ScrollToBottom(), enabled: session.IsStarted));
        menu.Items.Add(Item(session.CloseTitle, () => _list.Close(session), glyph: ""));
    }

    /// <summary>Switch Agent, asking first only when the session is working.</summary>
    private async Task SwitchAgentAsync(TerminalSession session, string preset)
    {
        if (_list.SwitchAgentQuestion(session, preset) is { } question)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Switch Agent",
                Content = question,
                PrimaryButtonText = "Restart",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        _list.SwitchAgent(session, preset);
    }

    /// <summary>
    /// An anchored popover for a name: the current text selected, Return
    /// saves, Escape cancels, clicking away dismisses it.
    /// </summary>
    private void EditName(FrameworkElement anchor, string title, string initial, Action<string> save)
    {
        var input = new TextBox { Text = initial, MinWidth = 220, MaxWidth = 360, Header = title };
        AutomationProperties.SetAutomationId(input, "TerminalNameEditor");
        var flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
            Content = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    input,
                    new TextBlock { Text = "Return to save · Esc to cancel", FontSize = 12, Style = SecondaryText },
                },
            },
        };
        input.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            var name = input.Text.Trim();
            flyout.Hide();
            save(name);
        };
        flyout.Opened += (_, _) => { input.Focus(FocusState.Programmatic); input.SelectAll(); };
        // After the context menu has closed and handed focus back.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => { if (anchor.XamlRoot != null) flyout.ShowAt(anchor); });
    }

    private void RebuildRows()
    {
        Rows.Children.Clear();
        foreach (var entry in _list.Entries())
        {
            switch (entry)
            {
                case TerminalSectionEntry section:
                    Rows.Children.Add(SectionHeader(section));
                    break;
                case TerminalSessionEntry { Session: var session } when _visuals.TryGetValue(session, out var visuals):
                    Rows.Children.Add(visuals.Row);
                    break;
                case TerminalEmptyEntry:
                    Rows.Children.Add(EmptyState());
                    break;
            }
        }
        RenumberRows();
    }

    private StackPanel EmptyState()
    {
        var clear = new Button { Content = "Clear Filters", HorizontalAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetAutomationId(clear, "TerminalClearFilters");
        clear.Click += (_, _) => { SearchBox.Text = ""; _list.ClearFilters(); };
        return new StackPanel
        {
            Margin = new Thickness(8, 16, 8, 16),
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = "No Matching Sessions",
                    Style = SecondaryText,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                clear,
            },
        };
    }

    private Button SectionHeader(TerminalSectionEntry section)
    {
        var header = new Button
        {
            Content = section.Header,
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(4, 6, 4, 0),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        AutomationProperties.SetName(header, $"{(section.IsPinned ? "Pinned" : section.Name)}, {section.Running} running, {(section.Collapsed ? "collapsed" : "expanded")}");
        header.Click += (_, _) => _list.ToggleCollapsed(section);
        if (section.IsPinned)
        {
            AutomationProperties.SetAutomationId(header, "TerminalPinnedHeader");
            return header;
        }
        var name = section.Name;
        AutomationProperties.SetAutomationId(header, "TerminalSection_" + name.Replace(' ', '_'));
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Rename section…", () => EditName(header, "Rename section", name, renamed => _list.RenameSection(name, renamed)), enabled: section.CanRename));
        menu.Items.Add(Item("Remove section", () => _list.RemoveSection(name), enabled: section.CanRename));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Move section up", () => _list.MoveSection(name, -1), enabled: section.CanMoveUp));
        menu.Items.Add(Item("Move section down", () => _list.MoveSection(name, 1), enabled: section.CanMoveDown));
        header.ContextFlyout = menu;
        // A row dropped on the header goes to the end of the section.
        AcceptSessionDrops(header, dropped => _list.MoveSession(dropped, before: null, section: name));
        return header;
    }

    private void AcceptSessionDrops(FrameworkElement target, Action<TerminalSession> drop)
    {
        target.AllowDrop = true;
        target.DragOver += (_, e) =>
        {
            if (_dragging == null || !e.DataView.Contains(SessionDragFormat)) return;
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.IsCaptionVisible = false;
        };
        target.Drop += (_, e) =>
        {
            if (_dragging is { } dropped && e.DataView.Contains(SessionDragFormat) && _list.Sessions.Contains(dropped)) drop(dropped);
            _dragging = null;
        };
    }

    private void RenumberRows()
    {
        for (var i = 0; i < _list.Sessions.Count; i++)
        {
            if (!_visuals.TryGetValue(_list.Sessions[i], out var visuals)) continue;
            AutomationProperties.SetAutomationId(visuals.Surface, $"TerminalSessionRow{i}");
            visuals.HintText.Text = i < 9 ? $"Ctrl+{i + 1}" : "";
        }
    }

    private void Refresh(TerminalSession session)
    {
        if (!_visuals.TryGetValue(session, out var visuals)) return;
        var state = _list.RowState(session);
        visuals.TitleText.Text = state.Title;
        visuals.DirectoryText.Text = state.Breadcrumb;
        visuals.DirectoryText.Visibility = state.Breadcrumb.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (state.Dot == TerminalDot.Waiting)
        {
            // Waiting to start: an outlined dot.
            visuals.Dot.Fill = null;
            visuals.Dot.Stroke = Theme("TextFillColorTertiaryBrush");
            visuals.Dot.StrokeThickness = 1;
        }
        else
        {
            visuals.Dot.Stroke = null;
            visuals.Dot.StrokeThickness = 0;
            visuals.Dot.Fill = state.Dot switch
            {
                TerminalDot.Attention => Theme("SystemFillColorCautionBrush"),
                TerminalDot.Running => Theme("SystemFillColorSuccessBrush"),
                _ => Theme("TextFillColorTertiaryBrush"),
            };
        }
        visuals.StatusText.Text = state.Status;
        visuals.Status.Visibility = state.StatusVisible ? Visibility.Visible : Visibility.Collapsed;
        visuals.StatusIcon.Glyph = state.Glyph;
        visuals.StatusIcon.Visibility = state.Glyph.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(visuals.Surface, state.AutomationName);
        ToolTipService.SetToolTip(visuals.Surface, state.ToolTip);
        AutomationProperties.SetName(visuals.Close, $"{state.CloseTitle}: {state.Title}");
        AutomationProperties.SetAutomationId(visuals.Close, "TerminalSessionClose");
        ToolTipService.SetToolTip(visuals.Close, $"{state.CloseTitle} (Ctrl+Shift+W)");
    }

    // ── Header ───────────────────────────────────────────────────────────

    /// <summary>Follow the window's layout: the header's button fills the window or restores it.</summary>
    public void SetLayoutState(bool fullWindow)
    {
        var controls = TerminalBarControls.For(showing: true, fullWindow);
        foreach (var (button, glyph) in new[] { (FullWindowButton, FullWindowGlyph), (FullWindowButtonNarrow, FullWindowGlyphNarrow) })
        {
            glyph.Glyph = controls.FullWindow ? "" : "";
            ToolTipService.SetToolTip(button, controls.FullWindowTip);
            AutomationProperties.SetName(button, controls.FullWindowName);
        }
        foreach (var button in new[] { HideButton, HideButtonNarrow })
        {
            ToolTipService.SetToolTip(button, controls.ToggleTip);
            AutomationProperties.SetName(button, controls.ToggleName);
        }
    }

    private void UpdateFilterControls()
    {
        _syncingFilters = true;
        foreach (var filter in _list.FilterButtons())
        {
            var button = _filterButtons[filter.Kind];
            ((TextBlock)((StackPanel)button.Content).Children[1]).Text = filter.CountText;
            button.IsChecked = filter.On;
            button.IsEnabled = filter.IsEnabled;
            ToolTipService.SetToolTip(button, filter.ToolTip);
            AutomationProperties.SetName(button, filter.AutomationName);
        }
        _syncingFilters = false;
        SortGlyph.Glyph = _list.SortGlyph;
        SortGlyph.Foreground = _list.Sort == TerminalSessionSort.Manual ? Theme("TextFillColorSecondaryBrush") : Theme("AccentTextFillColorPrimaryBrush");
        ToolTipService.SetToolTip(SortButton, $"Sort: {_list.Sort.Title()}");
        AutomationProperties.SetHelpText(SortButton, _list.Sort.Title());
    }

    private void ApplyListCollapsed()
    {
        var collapsed = _list.ListCollapsed;
        // Collapsed, the divider still takes a double-click to expand, but a drag does nothing.
        ListColumn.MinWidth = collapsed ? CollapsedListWidth : MinListWidth;
        ListColumn.MaxWidth = collapsed ? CollapsedListWidth : MaxListWidth;
        ListColumn.Width = new GridLength(collapsed ? CollapsedListWidth : SavedListWidth);
        HeaderWide.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        HeaderNarrow.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        FilterRow.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SearchBox.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        foreach (var visuals in _visuals.Values) ApplyCollapsedTo(visuals);
    }

    private void ApplyCollapsedTo(RowVisuals visuals)
    {
        var shown = _list.ListCollapsed ? Visibility.Collapsed : Visibility.Visible;
        visuals.Details.Visibility = shown;
        visuals.HintText.Visibility = shown;
        visuals.Close.Visibility = shown;
    }

    private void NewSession_Click(object sender, RoutedEventArgs e) => _list.OpenDefaultSession();

    private void NewSection_Click(object sender, RoutedEventArgs e) => EditName(NewSectionButton, "New section", "", _list.AddSection);

    private void Split_Click(object sender, RoutedEventArgs e) => _list.SplitActive();

    private void Collapse_Click(object sender, RoutedEventArgs e) => _list.SetListCollapsed(true);

    private void Expand_Click(object sender, RoutedEventArgs e) => _list.SetListCollapsed(false);

    private void FullWindow_Click(object sender, RoutedEventArgs e) => FullWindowRequested?.Invoke();

    private void Hide_Click(object sender, RoutedEventArgs e) => HideRequested?.Invoke();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => _list.SetQuery(SearchBox.Text);

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && SearchBox.Text.Length > 0) { SearchBox.Text = ""; e.Handled = true; }
    }

    private void NewMenu_Opening(object sender, object e)
    {
        NewMenu.Items.Clear();
        foreach (var preset in _list.InstalledPresets())
        {
            var name = preset;
            NewMenu.Items.Add(Item(name, () => _list.OpenPreset(name)));
        }
        var custom = _list.CustomCommand;
        NewMenu.Items.Add(Item(custom != null ? $"custom: {custom}" : "custom (set in Settings ▸ Terminal)", _list.OpenCustom, enabled: custom != null));
        NewMenu.Items.Add(new MenuFlyoutSeparator());
        var repoMenu = new MenuFlyoutSubItem { Text = "Open in repository" };
        AutomationProperties.SetAutomationId(repoMenu, "TerminalOpenInRepositoryMenu");
        var repos = _list.IsDemo ? Array.Empty<RepoLocation>() : _list.Repositories();
        if (repos.Count == 0) repoMenu.Items.Add(Item("Add repos in Settings ▸ Terminal", () => { }, enabled: false));
        foreach (var repo in repos)
        {
            var target = repo;
            repoMenu.Items.Add(Item(target.Name, () => _list.OpenInRepository(target)));
        }
        NewMenu.Items.Add(repoMenu);
    }

    private void SortMenu_Opening(object sender, object e)
    {
        SortMenu.Items.Clear();
        foreach (var sort in Enum.GetValues<TerminalSessionSort>())
        {
            var option = sort;
            var item = new RadioMenuFlyoutItem { Text = option.Title(), GroupName = "TerminalSort", IsChecked = option == _list.Sort };
            AutomationProperties.SetAutomationId(item, "TerminalSort_" + option);
            item.Click += (_, _) => _list.SetSort(option);
            SortMenu.Items.Add(item);
        }
    }

    /// <summary>The workspaces menu, read from the file each time it opens so hand edits apply without a restart.</summary>
    private void WorkspacesMenu_Opening(object sender, object e)
    {
        var menu = WorkspacesMenu;
        menu.Items.Clear();
        var file = _list.LoadWorkspaces(out var error);
        if (file == null)
        {
            var unreadable = Item("The workspaces file could not be read", () => { }, enabled: false);
            ToolTipService.SetToolTip(unreadable, error);
            menu.Items.Add(unreadable);
        }
        else if (file.Workspaces.Count == 0)
        {
            menu.Items.Add(Item("No saved workspaces", () => { }, enabled: false));
            if (_list.RepoCheckouts().Count > 0) menu.Items.Add(Item("Create workspace from Repos", _list.RegenerateBaselineWorkspace));
            // Someone with nothing cloned yet: the way to get started.
            else menu.Items.Add(Item("Clone a repository in Repos…", _list.RequestRepos));
        }
        foreach (var workspace in file?.Workspaces ?? new())
        {
            var w = workspace;
            var count = w.SessionCount == 1 ? "1 session" : $"{w.SessionCount} sessions";
            menu.Items.Add(Item($"Open {w.Name} ({count})", () => _list.OpenWorkspace(w, show: true)));
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var anchor = HeaderWide.Children[0] as FrameworkElement ?? this;
        menu.Items.Add(Item("Save sessions as workspace…", () => EditName(anchor, "Save workspace", file?.LaunchWorkspace?.Name ?? "", _list.SaveWorkspace),
            enabled: _list.Sessions.Count > 0 && !_list.IsDemo));
        if (file is { Workspaces.Count: > 0 })
        {
            var launch = new MenuFlyoutSubItem { Text = "Open at launch" };
            AutomationProperties.SetAutomationId(launch, "TerminalWorkspaceOpenAtLaunch");
            var none = new RadioMenuFlyoutItem { Text = "None", GroupName = "TerminalLaunchWorkspace", IsChecked = file.LaunchWorkspace == null };
            none.Click += (_, _) => _list.SetOpenAtLaunch(null);
            launch.Items.Add(none);
            foreach (var workspace in file.Workspaces)
            {
                var name = workspace.Name;
                var item = new RadioMenuFlyoutItem { Text = name, GroupName = "TerminalLaunchWorkspace", IsChecked = ReferenceEquals(file.LaunchWorkspace, workspace) };
                item.Click += (_, _) => _list.SetOpenAtLaunch(name);
                launch.Items.Add(item);
            }
            menu.Items.Add(launch);
            var delete = new MenuFlyoutSubItem { Text = "Delete workspace" };
            foreach (var workspace in file.Workspaces)
            {
                var name = workspace.Name;
                delete.Items.Add(Item(name, () => _list.DeleteWorkspace(name)));
            }
            menu.Items.Add(delete);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var addMissing = Item("Add missing repos", _list.AddMissingReposToBaseline, enabled: !_list.IsDemo);
        ToolTipService.SetToolTip(addMissing, "Add checked-out repositories the Repos workspace lacks, keeping your edits");
        menu.Items.Add(addMissing);
        var regenerate = Item("Regenerate Repos workspace", _list.RegenerateBaselineWorkspace, enabled: !_list.IsDemo);
        ToolTipService.SetToolTip(regenerate, "Rebuild the Repos workspace from your active checkouts; other workspaces are left alone");
        menu.Items.Add(regenerate);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Edit workspaces file", () => _list.OpenWorkspacesFile(reveal: false), enabled: !_list.IsDemo));
        menu.Items.Add(Item("Show workspaces file in Explorer", () => _list.OpenWorkspacesFile(reveal: true), enabled: !_list.IsDemo));
    }

    // ── Session list divider ─────────────────────────────────────────────

    private void ListSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_list.ListCollapsed) return;
        _splitterStart = e.GetCurrentPoint(this).Position.X;
        _splitterWidth = ListColumn.ActualWidth;
        ListSplitter.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ListSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_splitterStart is not { } start) return;
        var width = Math.Clamp(_splitterWidth + e.GetCurrentPoint(this).Position.X - start, MinListWidth, MaxListWidth);
        ListColumn.Width = new GridLength(width);
    }

    private void ListSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_splitterStart == null) return;
        _splitterStart = null;
        ListSplitter.ReleasePointerCapture(e.Pointer);
        UserPreferences.SetTerminalListWidth(ListColumn.ActualWidth);
    }

    private void ListSplitter_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => _splitterStart = null;

    private void ListSplitter_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _list.ToggleSessionList();
        e.Handled = true;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static MenuFlyoutItem Item(string text, Action action, bool enabled = true, string? glyph = null)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
        if (glyph != null) item.Icon = new FontIcon { Glyph = glyph };
        AutomationProperties.SetAutomationId(item, "Terminal_" + new string(text.Where(char.IsLetterOrDigit).ToArray()));
        item.Click += (_, _) => action();
        return item;
    }

    private Style SecondaryText => (Style)Resources["TerminalSecondaryTextStyle"];

    private static Brush Theme(string key) => (Brush)Application.Current.Resources[key];

    private static void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var package = new DataPackage();
        package.SetText(text);
        try { Clipboard.SetContent(package); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Terminal: clipboard unavailable"); }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        if (XamlRoot == null) return;
        await new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = message, CloseButtonText = "OK" }.ShowAsync();
    }
}
