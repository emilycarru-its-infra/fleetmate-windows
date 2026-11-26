using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using FleetMate.Core.Config;
using FleetMate.Core.Services.Terminal;
using ModernWpf.Controls;

namespace FleetMate.GUI.Views.Terminal;

/// <summary>
/// The bottom terminal panel, reachable from every tab. Sessions are a list
/// down the left, since people run ten or more at once: each row shows the
/// session's title, its directory, an activity dot and its Ctrl+number. The
/// list collapses to just the dots. A session can split into two panes.
/// What the list holds and does is <see cref="TerminalSessionList"/>, in
/// FleetMate.Core and shared with the WinUI app; this draws it.
/// </summary>
public sealed class TerminalPanel : UserControl
{
    /// <summary>The visuals of one session: its row and the grid its panes sit in.</summary>
    private sealed class RowVisuals
    {
        public required Border Row { get; init; }
        public required Grid Body { get; init; }
        public required Ellipse Dot { get; init; }
        public required TextBlock TitleText { get; init; }
        public required TextBlock DirectoryText { get; init; }
        public required FontIcon StatusIcon { get; init; }
        public required TextBlock StatusText { get; init; }
        public required TextBlock HintText { get; init; }
        public required StackPanel Details { get; init; }
        public required Button Close { get; init; }
        public List<TerminalView> Views { get; } = new();
    }

    private const double ListWidth = 230;
    private const double CollapsedListWidth = 34;
    private const double MinListWidth = 180;
    private const double MaxListWidth = 520;
    private readonly GridSplitter _listSplitter = new()
    {
        Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent,
        ResizeBehavior = GridResizeBehavior.PreviousAndNext, ResizeDirection = GridResizeDirection.Columns,
        Cursor = System.Windows.Input.Cursors.SizeWE, Focusable = false,
        ToolTip = "Drag to resize the session list; double-click to collapse or expand it",
    };

    /// <summary>The expanded list's width, remembered across launches.</summary>
    private static double SavedListWidth => Math.Clamp(UserPreferences.TerminalListWidth ?? ListWidth, MinListWidth, MaxListWidth);

    private readonly TerminalSessionList _list;
    private readonly Dictionary<TerminalSession, RowVisuals> _visuals = new();
    private readonly StackPanel _rows = new();
    private readonly Grid _body = new();
    private readonly ColumnDefinition _listColumn = new() { Width = new GridLength(ListWidth), MinWidth = MinListWidth, MaxWidth = MaxListWidth };
    private readonly StackPanel _headerWide = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _headerNarrow = new() { Orientation = Orientation.Vertical, Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _repoTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly StackPanel _filterRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Dictionary<TerminalFilterKind, Button> _filterButtons = new();
    private readonly Button _sortButton;
    private readonly Button _layoutButton;
    private readonly Button _hideButton;
    private readonly Button _layoutButtonNarrow;
    private readonly Button _hideButtonNarrow;
    // Search gets its own row under the filters, the list's width less 8 each side.
    private readonly TextBox _search = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(4, 6, 4, 0) };
    private Popup? _nameEditor;

    /// <summary>Raised when the panel should hide : its header's button, or Ctrl+` inside a terminal.</summary>
    public event EventHandler? HideRequested;

    /// <summary>Raised when full-window mode should turn on or off : its header's button, or Ctrl+Shift+Enter inside a terminal.</summary>
    public event EventHandler? FullWindowRequested;

    /// <summary>Whether a session is selected, started or waiting to start when the panel shows. Repos rows alone do not count.</summary>
    public bool HasSessions => _list.HasSessions;

    /// <summary>Raised when a workspace opens: show the terminal, split or full window.</summary>
    public event Action<bool>? WorkspaceShowRequested;

    /// <summary>Raised to show the Repos tab, where a repository is cloned.</summary>
    public event EventHandler? ReposRequested;

    /// <summary>Whether the terminal fills the window now, saved with a workspace. Set by the window.</summary>
    public Func<bool>? IsFullWindow { get => _list.IsFullWindow; set => _list.IsFullWindow = value; }

    /// <summary>Text was dropped on a pane; the window decides whether it is a row's block to hand over.</summary>
    public event Action<TerminalPaneController, string>? TextDropped;

    public TerminalPanel()
    {
        AutomationProperties.SetAutomationId(this, "TerminalPanel");
        var app = Application.Current as App;
        _list = new TerminalSessionList(
            () => app?.Config.Terminal ?? new TerminalSettings(),
            app?.Agent,
            action => Dispatcher.BeginInvoke(action),
            action => Dispatcher.BeginInvoke(action, DispatcherPriority.Input),
            () => TerminalView.IsDark);
        _list.SessionAdded += OnSessionAdded;
        _list.SessionRemoved += OnSessionRemoved;
        _list.SessionChanged += Refresh;
        _list.RowsChanged += RebuildRows;
        _list.PaneAdded += AddPaneView;
        _list.PanesCleared += ClearPaneViews;
        _list.ActiveChanged += ShowActive;
        _list.FiltersChanged += UpdateFilterControls;
        _list.ListCollapsedChanged += ApplyListCollapsed;
        _list.HideRequested += () => HideRequested?.Invoke(this, EventArgs.Empty);
        _list.FullWindowRequested += () => FullWindowRequested?.Invoke(this, EventArgs.Empty);
        _list.WorkspaceShowRequested += full => WorkspaceShowRequested?.Invoke(full);
        _list.ReposRequested += () => ReposRequested?.Invoke(this, EventArgs.Empty);
        _list.TextDropped += (pane, text) => TextDropped?.Invoke(pane, text);
        _list.ErrorRaised += ShowWorkspaceError;

        // ── Session list ──
        var newButton = IconButton("", "New session (Ctrl+Shift+T)", "TerminalNewButton", (_, _) => _list.OpenDefaultSession());
        var newMenu = IconButton("", "New session…", "TerminalNewMenuButton", (b, _) => OpenNewMenu((Button)b!));
        newMenu.Padding = new Thickness(2, 4, 4, 4);
        _headerWide.Children.Add(IconButton("", "Workspaces: open, save, and choose the one FleetMate opens at launch",
            "TerminalWorkspacesButton", (b, _) => OpenWorkspacesMenu((Button)b!)));
        _headerWide.Children.Add(newButton);
        _headerWide.Children.Add(newMenu);
        _headerWide.Children.Add(IconButton("", "New section", "TerminalNewSectionButton", (sender, _) =>
            EditName((Button)sender!, "New section", "", _list.AddSection)));
        _headerWide.Children.Add(IconButton("", "Split (Alt+Shift+D)", "TerminalSplitButton", (_, _) => _list.SplitActive()));
        _headerWide.Children.Add(IconButton("", "Collapse the list (Ctrl+Shift+S)", "TerminalCollapseButton", (_, _) => _list.SetListCollapsed(true)));
        // The terminal's own layout: fill the window or restore, and hide. The bar that held them is gone.
        _layoutButton = IconButton("", "", "TerminalFullWindowButton", (_, _) => FullWindowRequested?.Invoke(this, EventArgs.Empty));
        _hideButton = IconButton("", "", "TerminalHideButton", (_, _) => HideRequested?.Invoke(this, EventArgs.Empty));
        _headerWide.Children.Add(_layoutButton);
        _headerWide.Children.Add(_hideButton);

        _headerNarrow.Children.Add(IconButton("", "Expand the list (Ctrl+Shift+S)", "TerminalExpandButton", (_, _) => _list.SetListCollapsed(false)));
        _headerNarrow.Children.Add(IconButton("", "New session (Ctrl+Shift+T)", "TerminalNewButtonCollapsed", (_, _) => _list.OpenDefaultSession()));
        _layoutButtonNarrow = IconButton("", "", "TerminalFullWindowButtonCollapsed", (_, _) => FullWindowRequested?.Invoke(this, EventArgs.Empty));
        _hideButtonNarrow = IconButton("", "", "TerminalHideButtonCollapsed", (_, _) => HideRequested?.Invoke(this, EventArgs.Empty));
        _headerNarrow.Children.Add(_layoutButtonNarrow);
        _headerNarrow.Children.Add(_hideButtonNarrow);
        SetLayoutState(fullWindow: false);

        // Status filters with neutral counts, the sort menu and search.
        foreach (var filter in _list.FilterButtons())
        {
            var kind = filter.Kind;
            var button = FilterButton(filter.Glyph, filter.AutomationId, () => _list.ToggleFilter(kind));
            _filterButtons[kind] = button;
            _filterRow.Children.Add(button);
        }
        _sortButton = IconButton("", "Sort sessions", "TerminalSortButton", (b, _) => OpenSortMenu((Button)b!));
        ModernWpf.Controls.Primitives.ControlHelper.SetPlaceholderText(_search, "Search");
        AutomationProperties.SetName(_search, "Search sessions");
        AutomationProperties.SetAutomationId(_search, "TerminalSessionSearch");
        _search.TextChanged += (_, _) => _list.SetQuery(_search.Text);
        _search.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape && _search.Text.Length > 0) { _search.Clear(); e.Handled = true; }
        };
        _filterRow.Children.Add(_sortButton);
        UpdateFilterControls();

        var header = new StackPanel { Margin = new Thickness(4, 4, 4, 4) };
        header.Children.Add(_headerWide);
        header.Children.Add(_filterRow);
        header.Children.Add(_search);
        header.Children.Add(_headerNarrow);

        var list = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        list.Children.Add(header);
        var scroller = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetAutomationId(scroller, "TerminalSessionList");
        list.Children.Add(scroller);

        // Clip, so nothing in the list draws past its divider over the terminal.
        var listBorder = new Border { BorderThickness = new Thickness(0, 0, 1, 0), Child = list, ClipToBounds = true };
        listBorder.SetResourceReference(Border.BorderBrushProperty, "SystemControlForegroundBaseLowBrush");

        // Drag the divider to widen or narrow the list; double-click collapses or expands it.
        _listColumn.Width = new GridLength(SavedListWidth);
        AutomationProperties.SetAutomationId(_listSplitter, "TerminalSessionListSplitter");
        AutomationProperties.SetName(_listSplitter, "Session list divider");
        _listSplitter.DragCompleted += (_, _) => { if (!_list.ListCollapsed) UserPreferences.SetTerminalListWidth(_listColumn.ActualWidth); };
        _listSplitter.MouseDoubleClick += (_, e) => { _list.ToggleSessionList(); e.Handled = true; };

        var root = new Grid();
        root.ColumnDefinitions.Add(_listColumn);
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(listBorder, 0);
        Grid.SetColumn(_listSplitter, 1);
        Grid.SetColumn(_body, 2);
        root.Children.Add(listBorder);
        root.Children.Add(_listSplitter);
        root.Children.Add(_body);

        var border = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = root };
        border.SetResourceReference(Border.BorderBrushProperty, "SystemControlForegroundBaseLowBrush");
        border.SetResourceReference(Border.BackgroundProperty, "AppBackgroundBrush");
        Content = border;

        // Activity dots decay, and shells that never report their directory are polled.
        _tick.Tick += (_, _) => _list.Tick();
        _tick.Start();

        // The Repos rows follow the registry: within 5 seconds of a change, and when the app comes back to the front.
        _repoTimer.Tick += (_, _) => _list.RepoTick();
        _repoTimer.Start();
        if (Application.Current != null)
            Application.Current.Activated += (_, _) => _list.OnAppActivated();

        // The text follows the app's text size as it changes.
        UserPreferences.Changed += OnPreferencesChanged;
    }

    private void OnPreferencesChanged() => Dispatcher.BeginInvoke(_list.ApplyFontSize);

    // ── The list's surface for the window ────────────────────────────────

    public void OpenDefaultSession(bool takeFocus = true) => _list.OpenDefaultSession(takeFocus);

    public void SetAgentDragActive(bool active) => _list.SetAgentDragActive(active);

    public void FocusActive() => _list.FocusActive();

    public bool Insert(string text, TerminalPaneController? preferred = null) => _list.Insert(text, preferred);

    public bool HandleAppKey(TerminalAction action, int index) => _list.HandleAppKey(action, index);

    public void ToggleSessionList() => _list.ToggleSessionList();

    /// <summary>Repos rows, the sessions open at the last quit, then the launch workspace or the auto-started session.</summary>
    public void StartAtLaunch() => _list.StartAtLaunch();

    /// <summary>The pane's control, for anchoring the copy preview.</summary>
    public FrameworkElement? ViewFor(TerminalPaneController? pane) =>
        pane == null ? null : _visuals.Values.SelectMany(v => v.Views).FirstOrDefault(v => ReferenceEquals(v.Controller, pane));

    /// <summary>Called when the panel is shown or hidden, so bells from a hidden panel count as unseen.</summary>
    public void OnVisibilityChanged()
    {
        if (!IsVisible && _nameEditor != null) _nameEditor.IsOpen = false;
        _list.SetVisible(IsVisible);
    }

    public void DisposeAll()
    {
        _tick.Stop();
        _repoTimer.Stop();
        if (_nameEditor != null) _nameEditor.IsOpen = false;
        UserPreferences.Changed -= OnPreferencesChanged;
        _list.DisposeAll();
    }

    // ── Sessions and panes ───────────────────────────────────────────────

    private void OnSessionAdded(TerminalSession session)
    {
        var visuals = BuildRow(session);
        _visuals[session] = visuals;
        visuals.Body.Visibility = Visibility.Hidden;
        _body.Children.Add(visuals.Body);
        ApplyCollapsedTo(visuals);
        Refresh(session);
    }

    private void OnSessionRemoved(TerminalSession session)
    {
        if (!_visuals.Remove(session, out var visuals)) return;
        _body.Children.Remove(visuals.Body);
    }

    private void AddPaneView(TerminalSession session, TerminalPaneController pane)
    {
        if (!_visuals.TryGetValue(session, out var visuals)) return;
        var view = new TerminalView(pane);
        AutomationProperties.SetAutomationId(view, $"TerminalPane{visuals.Views.Count}");
        visuals.Views.Add(view);

        // Append after a splitter when it is not the first; existing panes stay put
        // (a WebView2 is a hosted window, and taking it out of the tree tears it down).
        var grid = visuals.Body;
        if (visuals.Views.Count > 1)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var splitter = new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
            splitter.SetResourceReference(BackgroundProperty, "SystemControlForegroundBaseLowBrush");
            AutomationProperties.SetAutomationId(splitter, "TerminalPaneSplitter");
            Grid.SetColumn(splitter, grid.ColumnDefinitions.Count - 1);
            grid.Children.Add(splitter);
        }
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(view, grid.ColumnDefinitions.Count - 1);
        grid.Children.Add(view);
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
            visuals.Body.Visibility = on ? Visibility.Visible : Visibility.Hidden;
            if (on) visuals.Row.SetResourceReference(Border.BackgroundProperty, "SubtleFillBrush");
            else visuals.Row.Background = Brushes.Transparent;
        }
    }

    // ── List rows ────────────────────────────────────────────────────────

    private RowVisuals BuildRow(TerminalSession session)
    {
        var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(4, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        var directory = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        directory.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        var hint = new TextBlock { FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");

        var statusIcon = new FontIcon { FontSize = 9, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        var statusText = new TextBlock { FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        statusText.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        statusIcon.SetResourceReference(FontIcon.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        var status = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 0) };
        status.Children.Add(statusIcon);
        status.Children.Add(statusText);

        var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        details.Children.Add(title);
        details.Children.Add(directory);
        details.Children.Add(status);

        var close = new Button { Content = new FontIcon { Glyph = "", FontSize = 9 }, Padding = new Thickness(4), Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = "Close (Ctrl+Shift+W)", VerticalAlignment = VerticalAlignment.Center };
        close.Click += (_, e) => { _list.Close(session); e.Handled = true; };
        close.ToolTipOpening += (_, _) => close.ToolTip = $"{session.CloseTitle} (Ctrl+Shift+W)";

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(details, 1);
        Grid.SetColumn(hint, 2);
        Grid.SetColumn(close, 3);
        grid.Children.Add(dot);
        grid.Children.Add(details);
        grid.Children.Add(hint);
        grid.Children.Add(close);

        var row = new Border { Child = grid, Padding = new Thickness(4, 5, 2, 5), Margin = new Thickness(4, 1, 4, 1), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand };
        row.MouseLeftButtonUp += (_, _) => _list.Activate(session, true);
        row.MouseRightButtonUp += (_, _) => OpenRowMenu(session, row);
        // Drag a row onto another to put it there, in that row's section.
        Point? pressedAt = null;
        row.PreviewMouseLeftButtonDown += (_, e) => pressedAt = e.GetPosition(row);
        row.PreviewMouseLeftButtonUp += (_, _) => pressedAt = null;
        row.MouseMove += (_, e) =>
        {
            if (pressedAt is not { } start || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
            var delta = e.GetPosition(row) - start;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            pressedAt = null;
            DragDrop.DoDragDrop(row, new DataObject(SessionDragFormat, session), DragDropEffects.Move);
        };
        AcceptSessionDrops(row, dropped => _list.MoveSession(dropped, before: session, section: session.Section));
        return new RowVisuals
        {
            Row = row, Body = new Grid(), Dot = dot, TitleText = title, DirectoryText = directory,
            StatusIcon = statusIcon, StatusText = statusText, HintText = hint, Details = details, Close = close,
        };
    }

    private void OpenRowMenu(TerminalSession session, Border row)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Rename Session…", (_, _) => EditName(row, "Rename session", session.DisplayName, name => _list.Rename(session, name))));
        menu.Items.Add(MenuItem("Use Automatic Name", (_, _) => _list.UseAutomaticName(session), enabled: session.CustomName != null));
        menu.Items.Add(MenuItem(session.Pinned ? "Unpin" : "Pin", (_, _) => _list.TogglePin(session)));
        var move = new MenuItem { Header = "Move to Section" };
        foreach (var section in _list.Sections)
        {
            var target = section;
            move.Items.Add(MenuItem(target, (_, _) => _list.MoveSession(session, before: null, section: target)));
        }
        menu.Items.Add(move);
        var switchAgent = new MenuItem { Header = "Switch Agent" };
        AutomationProperties.SetAutomationId(switchAgent, "TerminalSwitchAgentMenu");
        foreach (var (command, label) in TerminalSessionActions.Presets)
        {
            var preset = command;
            var item = MenuItem(label, (_, _) => SwitchAgent(session, preset), enabled: _list.IsPresetInstalled(preset));
            item.IsChecked = TerminalSessionActions.IsCurrent(preset, session.Command);
            switchAgent.Items.Add(item);
        }
        menu.Items.Add(switchAgent);
        var duplicate = new MenuItem { Header = "Duplicate Session" };
        AutomationProperties.SetAutomationId(duplicate, "TerminalDuplicateSessionMenu");
        duplicate.Items.Add(MenuItem("Same Agent", (_, _) => _list.DuplicateSession(session, preset: null)));
        duplicate.Items.Add(new Separator());
        foreach (var (command, label) in TerminalSessionActions.Presets)
        {
            var preset = command;
            duplicate.Items.Add(MenuItem(label, (_, _) => _list.DuplicateSession(session, preset), enabled: _list.IsPresetInstalled(preset)));
        }
        menu.Items.Add(duplicate);
        menu.Items.Add(new Separator());
        var folder = session.Folder;
        menu.Items.Add(MenuItem("Copy Path", (_, _) => { try { Clipboard.SetText(folder!); } catch (System.Runtime.InteropServices.COMException) { } },
            enabled: folder != null));
        menu.Items.Add(MenuItem("Show in Explorer", (_, _) => TerminalSessionList.ShowInExplorer(folder!), enabled: folder != null && System.IO.Directory.Exists(folder)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Scroll to Top", (_, _) => session.Panes.FirstOrDefault()?.ScrollToTop(), enabled: session.IsStarted));
        menu.Items.Add(MenuItem("Scroll to Bottom", (_, _) => session.Panes.FirstOrDefault()?.ScrollToBottom(), enabled: session.IsStarted));
        menu.Items.Add(MenuItem(session.CloseTitle, (_, _) => _list.Close(session)));
        row.ContextMenu = menu;
        menu.IsOpen = true;
    }

    /// <summary>Switch Agent, asking first only when the session is working.</summary>
    private void SwitchAgent(TerminalSession session, string preset)
    {
        if (_list.SwitchAgentQuestion(session, preset) is { } question
            && MessageBox.Show(question, "Switch Agent", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        _list.SwitchAgent(session, preset);
    }

    private void EditName(FrameworkElement anchor, string title, string initial, Action<string> save)
    {
        if (_nameEditor != null) _nameEditor.IsOpen = false;
        var input = new TextBox { Text = initial, MinWidth = 220, MaxWidth = 360, Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetName(input, title);
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        content.Children.Add(input);
        content.Children.Add(new TextBlock { Text = "Return to save · Esc to cancel", FontSize = 11, Margin = new Thickness(0, 6, 0, 0) });
        var surface = new Border { Child = content, Padding = new Thickness(10), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
        surface.SetResourceReference(Border.BackgroundProperty, "AppBackgroundBrush");
        surface.SetResourceReference(Border.BorderBrushProperty, "SystemControlForegroundBaseLowBrush");
        surface.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseHighBrush");
        var popup = new Popup { Child = surface, PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
        _nameEditor = popup;
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                var name = input.Text.Trim();
                popup.IsOpen = false;
                save(name);
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Escape) { popup.IsOpen = false; e.Handled = true; }
        };
        popup.Opened += (_, _) => Dispatcher.BeginInvoke(() => { input.Focus(); input.SelectAll(); }, DispatcherPriority.Input);
        popup.Closed += (_, _) => { if (ReferenceEquals(_nameEditor, popup)) _nameEditor = null; };
        // Context-menu close restores focus; open after that completes.
        Dispatcher.BeginInvoke(() => { if (anchor.IsLoaded && ReferenceEquals(_nameEditor, popup)) popup.IsOpen = true; }, DispatcherPriority.Input);
    }

    private void RebuildRows()
    {
        _rows.Children.Clear();
        foreach (var entry in _list.Entries())
        {
            switch (entry)
            {
                case TerminalSectionEntry section:
                    _rows.Children.Add(SectionHeader(section));
                    break;
                case TerminalSessionEntry { Session: var session } when _visuals.TryGetValue(session, out var visuals):
                    _rows.Children.Add(visuals.Row);
                    break;
                case TerminalEmptyEntry:
                    _rows.Children.Add(EmptyState());
                    break;
            }
        }
        RenumberRows();
    }

    private StackPanel EmptyState()
    {
        var empty = new StackPanel { Margin = new Thickness(8, 16, 8, 16), HorizontalAlignment = HorizontalAlignment.Center };
        var text = new TextBlock { Text = "No Matching Sessions", FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        var clear = new Button { Content = "Clear Filters", Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetAutomationId(clear, "TerminalClearFilters");
        clear.Click += (_, _) => { _search.Clear(); _list.ClearFilters(); };
        empty.Children.Add(text);
        empty.Children.Add(clear);
        return empty;
    }

    private Button SectionHeader(TerminalSectionEntry section)
    {
        var header = new Button { Content = section.Header, HorizontalContentAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(4) };
        header.Click += (_, _) => _list.ToggleCollapsed(section);
        if (section.IsPinned)
        {
            AutomationProperties.SetAutomationId(header, "TerminalPinnedHeader");
            return header;
        }
        var name = section.Name;
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Rename section…", (_, _) => EditName(header, "Rename section", name, renamed => _list.RenameSection(name, renamed)),
            enabled: section.CanRename));
        menu.Items.Add(MenuItem("Remove section", (_, _) => _list.RemoveSection(name), enabled: section.CanRename));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Move section up", (_, _) => _list.MoveSection(name, -1), enabled: section.CanMoveUp));
        menu.Items.Add(MenuItem("Move section down", (_, _) => _list.MoveSection(name, 1), enabled: section.CanMoveDown));
        header.ContextMenu = menu;
        // A row dropped on the header goes to the end of the section.
        AcceptSessionDrops(header, dropped => _list.MoveSession(dropped, before: null, section: name));
        return header;
    }

    // ── Reordering ───────────────────────────────────────────────────────

    /// <summary>Rows drag as this private format, so a row dropped on a terminal is not pasted as text.</summary>
    private const string SessionDragFormat = "FleetMate.TerminalSession";

    private void AcceptSessionDrops(Control target, Action<TerminalSession> drop)
    {
        target.AllowDrop = true;
        target.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(SessionDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        target.DragEnter += (_, e) =>
        {
            if (e.Data.GetDataPresent(SessionDragFormat)) target.SetResourceReference(BackgroundProperty, "SubtleFillBrush");
        };
        target.DragLeave += (_, _) => target.Background = Brushes.Transparent;
        target.Drop += (_, e) =>
        {
            target.Background = Brushes.Transparent;
            if (e.Data.GetData(SessionDragFormat) is TerminalSession dropped && _list.Sessions.Contains(dropped)) drop(dropped);
            e.Handled = true;
        };
    }

    private void AcceptSessionDrops(Border target, Action<TerminalSession> drop)
    {
        target.AllowDrop = true;
        var thickness = target.BorderThickness;
        target.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(SessionDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        target.DragEnter += (_, e) =>
        {
            if (!e.Data.GetDataPresent(SessionDragFormat)) return;
            target.BorderThickness = new Thickness(0, 2, 0, 0);
            target.SetResourceReference(Border.BorderBrushProperty, "SystemControlHighlightAccentBrush");
        };
        target.DragLeave += (_, _) => { target.BorderThickness = thickness; target.ClearValue(Border.BorderBrushProperty); };
        target.Drop += (_, e) =>
        {
            target.BorderThickness = thickness;
            target.ClearValue(Border.BorderBrushProperty);
            if (e.Data.GetData(SessionDragFormat) is TerminalSession dropped && _list.Sessions.Contains(dropped)) drop(dropped);
            e.Handled = true;
        };
    }

    // ── Workspaces, the New menu and sort ────────────────────────────────

    private void OpenNewMenu(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var preset in _list.InstalledPresets())
        {
            var name = preset;
            menu.Items.Add(MenuItem(name, (_, _) => _list.OpenPreset(name)));
        }
        var custom = _list.CustomCommand;
        menu.Items.Add(MenuItem(custom != null ? $"custom: {custom}" : "custom (set in Settings ▸ Terminal)",
            (_, _) => _list.OpenCustom(), enabled: custom != null));

        menu.Items.Add(new Separator());
        var repoMenu = new MenuItem { Header = "Open in repository" };
        AutomationProperties.SetAutomationId(repoMenu, "TerminalOpenInRepositoryMenu");
        var repos = _list.Repositories();
        if (repos.Count == 0)
            repoMenu.Items.Add(new MenuItem { Header = "Add repos in Settings ▸ Terminal", IsEnabled = false });
        foreach (var repo in repos)
        {
            var target = repo;
            repoMenu.Items.Add(MenuItem(target.Name, (_, _) => _list.OpenInRepository(target)));
        }
        menu.Items.Add(repoMenu);
        menu.IsOpen = true;
    }

    private static void ShowWorkspaceError(string message) =>
        MessageBox.Show(message, "Terminal Workspaces", MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>The workspaces menu, read from the file each time it opens so hand edits apply without a restart.</summary>
    private void OpenWorkspacesMenu(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        var file = _list.LoadWorkspaces(out var error);
        if (file == null)
            menu.Items.Add(new MenuItem { Header = "The workspaces file could not be read", IsEnabled = false, ToolTip = error });
        else if (file.Workspaces.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No saved workspaces", IsEnabled = false });
            if (_list.RepoCheckouts().Count > 0)
                menu.Items.Add(MenuItem("Create workspace from Repos", (_, _) => _list.RegenerateBaselineWorkspace()));
            else
                // Someone with nothing cloned yet: the way to get started.
                menu.Items.Add(MenuItem("Clone a repository in Repos…", (_, _) => _list.RequestRepos()));
        }
        foreach (var workspace in file?.Workspaces ?? new())
        {
            var w = workspace;
            var count = w.SessionCount == 1 ? "1 session" : $"{w.SessionCount} sessions";
            menu.Items.Add(MenuItem($"Open {w.Name} ({count})", (_, _) => _list.OpenWorkspace(w, show: true)));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Save sessions as workspace…", (_, _) =>
            EditName(anchor, "Save workspace", file?.LaunchWorkspace?.Name ?? "", _list.SaveWorkspace), enabled: _list.Sessions.Count > 0));
        if (file is { Workspaces.Count: > 0 })
        {
            var launch = new MenuItem { Header = "Open at launch" };
            AutomationProperties.SetAutomationId(launch, "TerminalWorkspaceOpenAtLaunch");
            var none = new MenuItem { Header = "None", IsCheckable = true, IsChecked = file.LaunchWorkspace == null };
            none.Click += (_, _) => _list.SetOpenAtLaunch(null);
            launch.Items.Add(none);
            foreach (var workspace in file.Workspaces)
            {
                var name = workspace.Name;
                var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = ReferenceEquals(file.LaunchWorkspace, workspace) };
                item.Click += (_, _) => _list.SetOpenAtLaunch(name);
                launch.Items.Add(item);
            }
            menu.Items.Add(launch);
            var delete = new MenuItem { Header = "Delete workspace" };
            foreach (var workspace in file.Workspaces)
            {
                var name = workspace.Name;
                delete.Items.Add(MenuItem(name, (_, _) => _list.DeleteWorkspace(name)));
            }
            menu.Items.Add(delete);
        }
        menu.Items.Add(new Separator());
        var addMissing = MenuItem("Add missing repos", (_, _) => _list.AddMissingReposToBaseline());
        addMissing.ToolTip = "Add checked-out repositories the Repos workspace lacks, keeping your edits";
        menu.Items.Add(addMissing);
        var regenerate = MenuItem("Regenerate Repos workspace", (_, _) => _list.RegenerateBaselineWorkspace());
        regenerate.ToolTip = "Rebuild the Repos workspace from your active checkouts; other workspaces are left alone";
        menu.Items.Add(regenerate);
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Edit workspaces file", (_, _) => _list.OpenWorkspacesFile(reveal: false)));
        menu.Items.Add(MenuItem("Show workspaces file in Explorer", (_, _) => _list.OpenWorkspacesFile(reveal: true)));
        menu.IsOpen = true;
    }

    private void OpenSortMenu(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var sort in Enum.GetValues<TerminalSessionSort>())
        {
            var option = sort;
            var item = new MenuItem { Header = option.Title(), IsCheckable = true, IsChecked = option == _list.Sort };
            AutomationProperties.SetAutomationId(item, "TerminalSort_" + option);
            item.Click += (_, _) => _list.SetSort(option);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void UpdateFilterControls()
    {
        foreach (var filter in _list.FilterButtons()) SetFilterButton(_filterButtons[filter.Kind], filter);
        var sortIcon = (FontIcon)_sortButton.Content;
        sortIcon.Glyph = _list.SortGlyph;
        if (_list.Sort == TerminalSessionSort.Manual) sortIcon.SetResourceReference(FontIcon.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        else sortIcon.SetResourceReference(FontIcon.ForegroundProperty, "SystemControlHighlightAccentBrush");
        _sortButton.ToolTip = $"Sort: {_list.Sort.Title()}";
        AutomationProperties.SetHelpText(_sortButton, _list.Sort.Title());
    }

    /// <summary>A status filter: an icon and a neutral count, tinted while it is on.</summary>
    private static Button FilterButton(string glyph, string automationId, Action toggle)
    {
        var icon = new FontIcon { Glyph = glyph, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var count = new TextBlock { FontSize = 11, Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        count.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(icon);
        content.Children.Add(count);
        var button = new Button
        {
            Content = content, Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(0, 0, 1, 0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetAutomationId(button, automationId);
        button.Click += (_, _) => toggle();
        return button;
    }

    private static void SetFilterButton(Button button, TerminalFilterButton state)
    {
        var content = (StackPanel)button.Content;
        var icon = (FontIcon)content.Children[0];
        var text = (TextBlock)content.Children[1];
        text.Text = state.CountText;
        button.IsEnabled = state.IsEnabled;
        button.Opacity = button.IsEnabled ? 1 : 0.45;
        if (state.On)
        {
            icon.SetResourceReference(FontIcon.ForegroundProperty, "SystemControlHighlightAccentBrush");
            button.SetResourceReference(BackgroundProperty, "SubtleFillBrush");
        }
        else
        {
            icon.SetResourceReference(FontIcon.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
            button.Background = Brushes.Transparent;
        }
        button.ToolTip = state.ToolTip;
        AutomationProperties.SetName(button, state.AutomationName);
    }

    private void RenumberRows()
    {
        for (var i = 0; i < _list.Sessions.Count; i++)
        {
            var session = _list.Sessions[i];
            if (!_visuals.TryGetValue(session, out var visuals)) continue;
            AutomationProperties.SetAutomationId(visuals.Row, $"TerminalSessionRow{i}");
            visuals.HintText.Text = i < 9 ? $"Ctrl+{i + 1}" : "";
        }
    }

    private static readonly Brush RunningBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x4F)));
    private static readonly Brush AttentionBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0x89, 0x0C)));
    private static Brush Frozen(SolidColorBrush brush) { brush.Freeze(); return brush; }

    private void Refresh(TerminalSession session)
    {
        if (!_visuals.TryGetValue(session, out var visuals)) return;
        var state = _list.RowState(session);
        visuals.TitleText.Text = state.Title;
        visuals.DirectoryText.Text = state.Breadcrumb;
        if (state.Dot == TerminalDot.Waiting)
        {
            // Waiting to start: an outlined dot.
            visuals.Dot.Fill = Brushes.Transparent;
            visuals.Dot.Stroke = Brushes.Gray;
            visuals.Dot.StrokeThickness = 1;
        }
        else
        {
            visuals.Dot.Stroke = null;
            visuals.Dot.StrokeThickness = 0;
            visuals.Dot.Fill = state.Dot switch
            {
                TerminalDot.Attention => AttentionBrush,
                TerminalDot.Running => RunningBrush,
                _ => Brushes.Gray,
            };
        }
        visuals.StatusText.Text = state.Status;
        ((FrameworkElement)visuals.StatusText.Parent).Visibility = state.StatusVisible ? Visibility.Visible : Visibility.Collapsed;
        visuals.StatusIcon.Glyph = state.Glyph;
        visuals.StatusIcon.Visibility = state.Glyph.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(visuals.Row, state.AutomationName);
        visuals.Row.ToolTip = state.ToolTip;
    }

    /// <summary>Follow the window's layout: the header's button fills the window or restores it.</summary>
    public void SetLayoutState(bool fullWindow)
    {
        var controls = TerminalBarControls.For(showing: true, fullWindow);
        foreach (var button in new[] { _layoutButton, _layoutButtonNarrow })
        {
            ((FontIcon)button.Content).Glyph = controls.FullWindow ? "" : "";
            button.ToolTip = controls.FullWindowTip;
            AutomationProperties.SetName(button, controls.FullWindowName);
        }
        foreach (var button in new[] { _hideButton, _hideButtonNarrow })
        {
            button.ToolTip = controls.ToggleTip;
            AutomationProperties.SetName(button, controls.ToggleName);
        }
    }

    private void ApplyListCollapsed()
    {
        var collapsed = _list.ListCollapsed;
        // Collapsed, the divider still takes a double-click to expand, but a drag does nothing.
        _listColumn.MinWidth = collapsed ? CollapsedListWidth : MinListWidth;
        _listColumn.MaxWidth = collapsed ? CollapsedListWidth : MaxListWidth;
        _listColumn.Width = new GridLength(collapsed ? CollapsedListWidth : SavedListWidth);
        _headerWide.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        _headerNarrow.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        _filterRow.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        _search.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        foreach (var visuals in _visuals.Values) ApplyCollapsedTo(visuals);
    }

    private void ApplyCollapsedTo(RowVisuals visuals)
    {
        var shown = _list.ListCollapsed ? Visibility.Collapsed : Visibility.Visible;
        visuals.Details.Visibility = shown;
        visuals.HintText.Visibility = shown;
        visuals.Close.Visibility = shown;
    }

    private static Button IconButton(string glyph, string tip, string automationId, RoutedEventHandler click)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
            ToolTip = tip,
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(1, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        AutomationProperties.SetAutomationId(button, automationId);
        AutomationProperties.SetName(button, tip);
        button.Click += click;
        return button;
    }

    private static MenuItem MenuItem(string header, RoutedEventHandler click, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        AutomationProperties.SetAutomationId(item, "TerminalNew_" + header.Split(' ')[0].TrimEnd(':'));
        item.Click += click;
        return item;
    }
}
