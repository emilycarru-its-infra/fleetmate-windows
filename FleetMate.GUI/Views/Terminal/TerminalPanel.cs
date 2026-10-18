using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using FleetMate.Core.Config;
using FleetMate.Core.Services.Terminal;
using FleetMate.GUI.Views.Shared;
using ModernWpf.Controls;

namespace FleetMate.GUI.Views.Terminal;

/// <summary>
/// The bottom terminal panel, reachable from every tab. Sessions are a list
/// down the left, since people run ten or more at once: each row shows the
/// session's title, its directory, an activity dot and its Ctrl+number. The
/// list collapses to just the dots. A session can split into two panes.
/// </summary>
public sealed class TerminalPanel : UserControl
{
    private sealed class Session
    {
        public required string Label { get; init; }
        public string? CustomName { get; set; }
        public string? AutomaticName { get; set; }
        public string Section { get; set; } = "Sessions";
        /// <summary>What it runs, as a workspace saves it.</summary>
        public string Command { get; init; } = "";
        public required Grid Body { get; init; }
        public List<TerminalView> Panes { get; } = new();
        public Border Row { get; set; } = null!;
        public Ellipse Dot { get; set; } = null!;
        public TextBlock TitleText { get; set; } = null!;
        public TextBlock DirectoryText { get; set; } = null!;
        public TextBlock HintText { get; set; } = null!;
        public StackPanel Details { get; set; } = null!;
    }

    private const double ListWidth = 230;
    private const double CollapsedListWidth = 34;

    private readonly StackPanel _rows = new();
    private readonly Grid _body = new();
    private readonly ColumnDefinition _listColumn = new() { Width = new GridLength(ListWidth) };
    private readonly StackPanel _headerWide = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _headerNarrow = new() { Orientation = Orientation.Vertical, Visibility = Visibility.Collapsed };
    private readonly List<Session> _sessions = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly List<string> _sections = new() { "Sessions" };
    private readonly HashSet<string> _collapsedSections = new();
    private readonly StackPanel _filterRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Button _filterActive;
    private readonly Button _filterRunning;
    private readonly Button _filterPaused;
    private readonly Button _sortButton;
    private readonly TextBox _search = new() { MinWidth = 60, Width = 92, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
    private TerminalSessionFilter _filter = new(UserPreferences.TerminalFilterActive, UserPreferences.TerminalFilterRunning, UserPreferences.TerminalFilterPaused);
    private TerminalSessionSort _sort = UserPreferences.TerminalSort;
    /// <summary>Each session's traits, read on the slow tick; activity changes without the panel announcing it.</summary>
    private Dictionary<Session, TerminalSessionTraits> _traits = new();
    private string _shownKey = "";
    private Popup? _nameEditor;
    private Session? _active;
    private bool _collapsed;
    private int _tickCount;

    /// <summary>Raised when the panel should hide (Ctrl+` inside a terminal; the bar below holds the button).</summary>
    public event EventHandler? HideRequested;

    /// <summary>Raised when full-window mode should turn on or off (Ctrl+Shift+Enter inside a terminal; the bar below holds the button).</summary>
    public event EventHandler? FullWindowRequested;

    public bool HasSessions => _sessions.Count > 0;

    /// <summary>Raised when a workspace opens: show the terminal, split or full window.</summary>
    public event Action<bool>? WorkspaceShowRequested;

    /// <summary>Whether the terminal fills the window now, saved with a workspace. Set by the window.</summary>
    public Func<bool>? IsFullWindow { get; set; }

    /// <summary>Text was dropped on a pane; the window decides whether it is a row's block to hand over.</summary>
    public event Action<TerminalView, string>? TextDropped;

    public TerminalPanel()
    {
        AutomationProperties.SetAutomationId(this, "TerminalPanel");

        // ── Session list ──
        var newButton = IconButton("", "New session (Ctrl+Shift+T)", "TerminalNewButton", (_, _) => OpenDefaultSession());
        var newMenu = IconButton("", "New session…", "TerminalNewMenuButton", (b, _) => OpenNewMenu((Button)b!));
        newMenu.Padding = new Thickness(2, 4, 4, 4);
        _headerWide.Children.Add(IconButton("\uE8F1", "Workspaces: open, save, and choose the one FleetMate opens at launch",
            "TerminalWorkspacesButton", (b, _) => OpenWorkspacesMenu((Button)b!)));
        _headerWide.Children.Add(newButton);
        _headerWide.Children.Add(newMenu);
        _headerWide.Children.Add(IconButton("", "New section", "TerminalNewSectionButton", (sender, _) =>
            EditName((Button)sender!, "New section", "", name => {
                if (!string.IsNullOrWhiteSpace(name) && !_sections.Contains(name)) { _sections.Add(name); RebuildRows(); }
            })));
        _headerWide.Children.Add(IconButton("", "Split (Alt+Shift+D)", "TerminalSplitButton", (_, _) => SplitActive()));
        _headerWide.Children.Add(IconButton("", "Collapse the list", "TerminalCollapseButton", (_, _) => SetCollapsed(true)));

        _headerNarrow.Children.Add(IconButton("", "Expand the list", "TerminalExpandButton", (_, _) => SetCollapsed(false)));
        _headerNarrow.Children.Add(IconButton("", "New session (Ctrl+Shift+T)", "TerminalNewButtonCollapsed", (_, _) => OpenDefaultSession()));

        // Status filters with neutral counts, the sort menu and search.
        _filterActive = FilterButton("\uE91F", "Active", "Show only sessions running an agent", "TerminalFilterActive",
            () => SetFilter(_filter with { Active = !_filter.Active }));
        _filterRunning = FilterButton("\uE945", "Running", "Show only sessions that are working", "TerminalFilterRunning",
            () => SetFilter(_filter with { Running = !_filter.Running }));
        _filterPaused = FilterButton("\uE769", "Paused", "Show only agents waiting on you", "TerminalFilterPaused",
            () => SetFilter(_filter with { Paused = !_filter.Paused }));
        _sortButton = IconButton("\uE8CB", "Sort sessions", "TerminalSortButton", (b, _) => OpenSortMenu((Button)b!));
        ModernWpf.Controls.Primitives.ControlHelper.SetPlaceholderText(_search, "Search");
        AutomationProperties.SetName(_search, "Search sessions");
        AutomationProperties.SetAutomationId(_search, "TerminalSessionSearch");
        _search.TextChanged += (_, _) => SetFilter(_filter with { Query = _search.Text }, save: false);
        _search.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape && _search.Text.Length > 0) { _search.Clear(); e.Handled = true; }
        };
        _filterRow.Children.Add(_filterActive);
        _filterRow.Children.Add(_filterRunning);
        _filterRow.Children.Add(_filterPaused);
        _filterRow.Children.Add(_sortButton);
        _filterRow.Children.Add(_search);
        UpdateFilterControls();

        var header = new StackPanel { Margin = new Thickness(4, 4, 4, 4) };
        header.Children.Add(_headerWide);
        header.Children.Add(_filterRow);
        header.Children.Add(_headerNarrow);

        var list = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        list.Children.Add(header);
        var scroller = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetAutomationId(scroller, "TerminalSessionList");
        list.Children.Add(scroller);

        var listBorder = new Border { BorderThickness = new Thickness(0, 0, 1, 0), Child = list };
        listBorder.SetResourceReference(Border.BorderBrushProperty, "SystemControlForegroundBaseLowBrush");

        var root = new Grid();
        root.ColumnDefinitions.Add(_listColumn);
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(listBorder, 0);
        Grid.SetColumn(_body, 1);
        root.Children.Add(listBorder);
        root.Children.Add(_body);

        var border = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = root };
        border.SetResourceReference(Border.BorderBrushProperty, "SystemControlForegroundBaseLowBrush");
        border.SetResourceReference(Border.BackgroundProperty, "AppBackgroundBrush");
        Content = border;

        // Activity dots decay, and shells that never report their directory are polled.
        _tick.Tick += (_, _) =>
        {
            if (++_tickCount % 4 == 0)
                foreach (var pane in _sessions.SelectMany(s => s.Panes)) pane.PollDirectory();
            if (_tickCount % 3 == 0) RefreshTraits();
            var sectionsChanged = false;
            foreach (var session in _sessions)
            {
                foreach (var pane in session.Panes)
                {
                    if (pane.ReadMetadataChange() is not { } metadata) continue;
                    if (metadata.Title != null) session.AutomaticName = metadata.Title.Length == 0 ? null : metadata.Title;
                    if (!string.IsNullOrWhiteSpace(metadata.Section) && metadata.Section != session.Section)
                    {
                        if (!_sections.Contains(metadata.Section)) _sections.Add(metadata.Section);
                        session.Section = metadata.Section;
                        sectionsChanged = true;
                    }
                }
                Refresh(session);
            }
            if (sectionsChanged) RebuildRows();
        };
        _tick.Start();

        // The text follows the app's text size as it changes.
        UserPreferences.Changed += OnPreferencesChanged;
    }

    // ── Text size ────────────────────────────────────────────────────────

    /// <summary>The size every session draws at now: the app's text size plus the terminal's own offset.</summary>
    private static double TerminalTextSize => TerminalFontSize.For(UserPreferences.TextScale, UserPreferences.TerminalFontOffset);

    private void OnPreferencesChanged() => Dispatcher.BeginInvoke(ApplyFontSize);

    private void ApplyFontSize()
    {
        var size = TerminalTextSize;
        foreach (var pane in _sessions.SelectMany(s => s.Panes)) pane.SetFontSize(size);
    }

    /// <summary>Ctrl+= and Ctrl+- inside a terminal: a step from the app's text size, for the terminal alone.</summary>
    private static void StepFont(double points) =>
        UserPreferences.SetTerminalFontOffset(
            TerminalFontSize.Step(UserPreferences.TextScale, UserPreferences.TerminalFontOffset, points));

    private static TerminalSettings Settings => ((App)Application.Current).Config.Terminal;

    /// <summary>
    /// What a new session runs: the setting, except that the built-in default
    /// (Codex) gives way to the shell on a PC without Codex.
    /// </summary>
    private static string DefaultCommand =>
        Settings.AgentCommandIsBuiltInDefault && AgentCommands.FindInstalled(Settings.AgentCommand) == null
            ? AgentCommands.Shell
            : Settings.AgentCommand;

    // ── Opening sessions ─────────────────────────────────────────────────

    /// <summary>
    /// A session running what Settings ▸ Terminal ▸ Runs names (the shell
    /// when it names nothing). Sessions open only here, from the New menu,
    /// and at launch: never from a tab change or from showing the panel.
    /// <paramref name="takeFocus"/> is false for the session started at
    /// launch, so it does not take the keyboard.
    /// </summary>
    public void OpenDefaultSession(bool takeFocus = true) =>
        OpenSession(AgentLaunch(DefaultCommand, null) with { TakeFocus = takeFocus });

    /// <summary>Tell every pane whether a row is being dragged, so they show the copy cursor only then.</summary>
    public void SetAgentDragActive(bool active)
    {
        foreach (var pane in _sessions.SelectMany(s => s.Panes)) pane.SetAgentDragActive(active);
    }

    public void FocusActive() => _active?.Panes.FirstOrDefault()?.FocusTerminal();

    /// <summary>
    /// Put <paramref name="text"/> into an agent's input without pressing
    /// Return, activating and focusing that session. Only a pane running an
    /// agent CLI that has asked for bracketed paste receives it, never a bare
    /// shell; <paramref name="preferred"/> (the pane a row was dropped on) is
    /// tried first, then the active session. Returns false when no agent
    /// is running, so the caller copies the text instead and nothing is ever
    /// sent on the person's behalf.
    /// </summary>
    public bool Insert(string text, TerminalView? preferred = null)
    {
        var candidates = (_active == null ? _sessions : _sessions.Where(s => !ReferenceEquals(s, _active)).Prepend(_active))
            .SelectMany(s => s.Panes.Select(p => (Session: s, Pane: p)))
            .OrderBy(c => ReferenceEquals(c.Pane, preferred) ? 0 : 1);
        foreach (var (session, pane) in candidates)
        {
            if (!pane.AcceptsBracketedPaste || !pane.AgentIsRunning) continue;
            if (!pane.PasteText(text)) continue;
            Activate(session, false);
            Dispatcher.BeginInvoke(pane.FocusTerminal, DispatcherPriority.Input);
            return true;
        }
        return false;
    }

    private void OpenNewMenu(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var preset in AgentCommands.InstalledChoices(AgentCommands.FindInstalled))
        {
            var name = preset;
            menu.Items.Add(MenuItem(name, (_, _) =>
                OpenSession(name == AgentCommands.Shell ? ShellLaunch(null) : AgentLaunch(name, null))));
        }
        var custom = Settings.AgentCommand;
        var isCustom = custom.Length > 0 && !AgentCommands.Choices.Contains(custom, StringComparer.OrdinalIgnoreCase);
        menu.Items.Add(MenuItem(isCustom ? $"custom: {custom}" : "custom (set in Settings ▸ Terminal)",
            (_, _) => OpenSession(AgentLaunch(custom, null)), enabled: isCustom));

        menu.Items.Add(new Separator());
        var repoMenu = new MenuItem { Header = "Open in repository" };
        AutomationProperties.SetAutomationId(repoMenu, "TerminalOpenInRepositoryMenu");
        var repos = Settings.EffectiveRepos;
        if (repos.Count == 0)
            repoMenu.Items.Add(new MenuItem { Header = "Add repos in Settings ▸ Terminal", IsEnabled = false });
        foreach (var entry in repos)
        {
            var repo = RepoLocator.Resolve(entry, RepoLocator.DefaultRoot);
            repoMenu.Items.Add(MenuItem(repo.Name, (_, _) => OpenSession(AgentLaunch(DefaultCommand, repo))));
        }
        menu.Items.Add(repoMenu);
        menu.IsOpen = true;
    }

    private static TerminalLaunch ShellLaunch(RepoLocation? repo) =>
        new(repo?.Name ?? AgentCommands.Shell, AgentCommands.DefaultShell(AgentCommands.FindOnPath), repo?.Path, repo);

    /// <summary>A session as a workspace names it: the shell for "" or "shell", otherwise the agent command, in its folder.</summary>
    private static TerminalLaunch WorkspaceLaunch(string? command, string? directory)
    {
        var launch = string.IsNullOrWhiteSpace(command) || command.Equals(AgentCommands.Shell, StringComparison.OrdinalIgnoreCase)
            ? ShellLaunch(null)
            : AgentLaunch(command, null);
        return launch with { WorkingDirectory = directory ?? launch.WorkingDirectory };
    }

    private static TerminalLaunch AgentLaunch(string agentCommand, RepoLocation? repo)
    {
        var command = AgentCommands.Resolve(agentCommand, AgentCommands.FindInstalled);
        var label = string.IsNullOrWhiteSpace(agentCommand) || agentCommand.Equals(AgentCommands.Shell, StringComparison.OrdinalIgnoreCase)
            ? AgentCommands.Shell
            : agentCommand.Split(' ')[0];
        return new TerminalLaunch(repo != null ? $"{label} · {repo.Name}" : label, command, repo?.Path, repo)
        {
            AgentCommand = label == AgentCommands.Shell ? "" : agentCommand,
        };
    }

    public void OpenSession(TerminalLaunch launch)
    {
        var session = new Session { Label = launch.Title, Body = new Grid(), Command = launch.AgentCommand };
        _sessions.Add(session);
        _body.Children.Add(session.Body);
        BuildRow(session);
        RebuildRows();
        AddPane(session, launch);
        Activate(session, launch.TakeFocus);
        RenumberRows();
    }

    private void SplitActive()
    {
        // Split needs a session to split; it never opens the first one.
        if (_active == null) return;
        if (_active.Panes.Count >= 2) return;
        AddPane(_active, AgentLaunch(DefaultCommand, null));
        Show(_active);
    }

    private void AddPane(Session session, TerminalLaunch launch)
    {
        var view = new TerminalView(launch);
        view.SetFontSize(TerminalTextSize);
        AutomationProperties.SetAutomationId(view, $"TerminalPane{session.Panes.Count}");
        view.ToggleRequested += (_, _) => HideRequested?.Invoke(this, EventArgs.Empty);
        view.StateChanged += (_, _) => Refresh(session);
        view.KeyAction += OnKeyAction;
        view.TextDropped += (pane, text) => TextDropped?.Invoke(pane, text);
        session.Panes.Add(view);

        // Append after a splitter when it is not the first; existing panes stay put
        // (a WebView2 is a hosted window, and taking it out of the tree tears it down).
        var grid = session.Body;
        if (session.Panes.Count > 1)
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

    // ── Keys from inside a terminal ──────────────────────────────────────

    private void OnKeyAction(TerminalView? source, TerminalAction action, int index)
    {
        switch (action)
        {
            case TerminalAction.NewSession: OpenDefaultSession(); break;
            case TerminalAction.CloseSession: if (_active != null) CloseSession(_active); break;
            case TerminalAction.Split: SplitActive(); break;
            case TerminalAction.Clear: (source ?? _active?.Panes.FirstOrDefault())?.Clear(); break;
            case TerminalAction.Select: if (index < _sessions.Count) Activate(_sessions[index], true); break;
            case TerminalAction.Next: Cycle(+1); break;
            case TerminalAction.Previous: Cycle(-1); break;
            case TerminalAction.FullWindow: FullWindowRequested?.Invoke(this, EventArgs.Empty); break;
            case TerminalAction.ZoomIn: StepFont(1); break;
            case TerminalAction.ZoomOut: StepFont(-1); break;
            case TerminalAction.ActualSize: UserPreferences.SetTerminalFontOffset(0); break;
        }
    }

    /// <summary>The same chords, pressed while focus is elsewhere in the app (MainWindow forwards them).</summary>
    public bool HandleAppKey(TerminalAction action, int index)
    {
        if (action == TerminalAction.None) return false;
        OnKeyAction(null, action, index);
        return true;
    }

    private void Cycle(int step)
    {
        if (_sessions.Count == 0 || _active == null) return;
        var i = (_sessions.IndexOf(_active) + step + _sessions.Count) % _sessions.Count;
        Activate(_sessions[i], true);
    }

    // ── List rows ────────────────────────────────────────────────────────

    private Border BuildRow(Session session)
    {
        session.Dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(4, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        session.TitleText = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        session.DirectoryText = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        session.DirectoryText.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        session.HintText = new TextBlock { FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        session.HintText.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");

        session.Details = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        session.Details.Children.Add(session.TitleText);
        session.Details.Children.Add(session.DirectoryText);

        var close = new Button { Content = new FontIcon { Glyph = "", FontSize = 9 }, Padding = new Thickness(4), Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = "Close (Ctrl+Shift+W)", VerticalAlignment = VerticalAlignment.Center };
        close.Click += (_, e) => { CloseSession(session); e.Handled = true; };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(session.Details, 1);
        Grid.SetColumn(session.HintText, 2);
        Grid.SetColumn(close, 3);
        grid.Children.Add(session.Dot);
        grid.Children.Add(session.Details);
        grid.Children.Add(session.HintText);
        grid.Children.Add(close);

        var row = new Border { Child = grid, Padding = new Thickness(4, 5, 2, 5), Margin = new Thickness(4, 1, 4, 1), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand };
        row.MouseLeftButtonUp += (_, _) => Activate(session, true);
        row.MouseRightButtonUp += (_, _) => {
            var menu = new ContextMenu();
            menu.Items.Add(MenuItem("Rename session…", (_, _) => {
                EditName(row, "Rename session", session.CustomName ?? session.AutomaticName ?? session.Panes.FirstOrDefault()?.Title ?? session.Label, name => {
                    if (!_sessions.Contains(session)) return;
                    session.CustomName = string.IsNullOrWhiteSpace(name) ? null : name;
                    Refresh(session);
                });
            }));
            menu.Items.Add(MenuItem("Use automatic title", (_, _) => { session.CustomName = null; Refresh(session); }));
            var move = new MenuItem { Header = "Move to section" };
            foreach (var section in _sections) {
                var target = section;
                move.Items.Add(MenuItem(target, (_, _) => { session.Section = target; RebuildRows(); }));
            }
            menu.Items.Add(move);
            row.ContextMenu = menu;
            menu.IsOpen = true;
        };
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
        AcceptSessionDrops(row, dropped => MoveSession(dropped, before: session, section: session.Section));
        session.Row = row;
        session.Details.Tag = close; // hidden with the details when collapsed
        Refresh(session);
        return row;
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
        input.PreviewKeyDown += (_, e) => {
            if (e.Key == System.Windows.Input.Key.Enter) {
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

    private bool FiltersApply => !_collapsed && (_filter.IsFiltering || _sort != TerminalSessionSort.Manual);

    private void RebuildRows()
    {
        _rows.Children.Clear();
        if (FiltersApply) { RebuildFilteredRows(); return; }
        _shownKey = "";
        foreach (var section in _sections.ToList())
        {
            var members = _sessions.Where(s => s.Section == section).ToList();
            var header = SectionHeader(section, members);
            _rows.Children.Add(header);
            if (!_collapsedSections.Contains(section)) foreach (var member in members) _rows.Children.Add(member.Row);
        }
    }

    /// <summary>
    /// Filtered or sorted: sections with no match are left out and the rest
    /// open, so a search finds sessions in a collapsed section too; sorted by
    /// activity, one list across sections.
    /// </summary>
    private void RebuildFilteredRows()
    {
        var all = _sessions.Select(TraitsOf).ToList();
        var shown = TerminalSessionFilter.Order(all, _sort).Where(i => _filter.Matches(all[i])).Select(i => _sessions[i]).ToList();
        _shownKey = ShownKey(shown);
        if (_sort == TerminalSessionSort.Manual)
        {
            foreach (var section in _sections.ToList())
            {
                var members = shown.Where(s => s.Section == section).ToList();
                if (members.Count == 0) continue;
                _rows.Children.Add(SectionHeader(section, members));
                foreach (var member in members) _rows.Children.Add(member.Row);
            }
        }
        else foreach (var session in shown) _rows.Children.Add(session.Row);
        if (shown.Count > 0) return;

        var empty = new StackPanel { Margin = new Thickness(8, 16, 8, 16), HorizontalAlignment = HorizontalAlignment.Center };
        var text = new TextBlock { Text = "No matching sessions", FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        var clear = new Button { Content = "Clear Filters", Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetAutomationId(clear, "TerminalClearFilters");
        clear.Click += (_, _) => { _search.Clear(); SetFilter(new TerminalSessionFilter()); };
        empty.Children.Add(text);
        empty.Children.Add(clear);
        _rows.Children.Add(empty);
    }

    private static string ShownKey(IEnumerable<Session> shown) =>
        string.Join("|", shown.Select(s => s.GetHashCode()));

    private Button SectionHeader(string section, List<Session> members)
    {
        var header = new Button { Content = $"{(_collapsedSections.Contains(section) && !FiltersApply ? "▸" : "▾")} {section} ({members.Count})", HorizontalContentAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(4) };
        header.Click += (_, _) => { if (!_collapsedSections.Add(section)) _collapsedSections.Remove(section); RebuildRows(); };
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Rename section…", (_, _) => {
            EditName(header, "Rename section", section, name => {
                var index = _sections.IndexOf(section);
                if (index < 0 || string.IsNullOrWhiteSpace(name) || _sections.Contains(name)) return;
                _sections[index] = name;
                foreach (var member in _sessions.Where(s => s.Section == section).ToList()) member.Section = name;
                if (_collapsedSections.Remove(section)) _collapsedSections.Add(name);
                RebuildRows();
            });
        }, enabled: section != "Sessions"));
        menu.Items.Add(MenuItem("Remove section", (_, _) => {
            _sections.Remove(section); _collapsedSections.Remove(section);
            foreach (var member in _sessions.Where(s => s.Section == section).ToList()) member.Section = "Sessions";
            RebuildRows();
        }, enabled: section != "Sessions"));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Move section up", (_, _) => MoveSection(section, -1), enabled: _sections.IndexOf(section) > 0));
        menu.Items.Add(MenuItem("Move section down", (_, _) => MoveSection(section, 1), enabled: _sections.IndexOf(section) < _sections.Count - 1));
        header.ContextMenu = menu;
        // A row dropped on the header goes to the end of the section.
        AcceptSessionDrops(header, dropped => MoveSession(dropped, before: null, section: section));
        return header;
    }

    // ── Reordering ───────────────────────────────────────────────────────

    /// <summary>Rows drag as this private format, so a row dropped on a terminal is not pasted as text.</summary>
    private const string SessionDragFormat = "FleetMate.TerminalSession";

    private void AcceptSessionDrops(Control target, Action<Session> drop)
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
            if (e.Data.GetData(SessionDragFormat) is Session dropped && _sessions.Contains(dropped)) drop(dropped);
            e.Handled = true;
        };
    }

    private void AcceptSessionDrops(Border target, Action<Session> drop)
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
            if (e.Data.GetData(SessionDragFormat) is Session dropped && _sessions.Contains(dropped)) drop(dropped);
            e.Handled = true;
        };
    }

    /// <summary>Put <paramref name="session"/> before <paramref name="before"/>, or at the end of <paramref name="section"/>.</summary>
    private void MoveSession(Session session, Session? before, string section)
    {
        if (ReferenceEquals(session, before) || !_sections.Contains(section)) return;
        _sessions.Remove(session);
        session.Section = section;
        int index;
        if (before != null) index = _sessions.IndexOf(before);
        else
        {
            var last = _sessions.FindLastIndex(s => s.Section == section);
            index = last >= 0 ? last + 1 : _sessions.Count;
        }
        _sessions.Insert(Math.Max(0, index), session);
        RebuildRows();
        RenumberRows();
    }

    private void MoveSection(string section, int offset)
    {
        var index = _sections.IndexOf(section);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= _sections.Count) return;
        (_sections[index], _sections[target]) = (_sections[target], _sections[index]);
        RebuildRows();
    }

    // ── Saved workspaces ─────────────────────────────────────────────────

    /// <summary>The sessions open now, by section, as a workspace named <paramref name="name"/>. A session keeps its name only when the person gave it one.</summary>
    private TerminalWorkspace Snapshot(string name)
    {
        var workspace = new TerminalWorkspace { Name = name, Show = true, FullWindow = IsFullWindow?.Invoke() == true ? true : null };
        foreach (var section in _sections)
            workspace.Sections.Add(new TerminalWorkspaceSection
            {
                Name = section,
                Collapsed = _collapsedSections.Contains(section) ? true : null,
                Sessions = _sessions.Where(s => s.Section == section).Select(s => new TerminalWorkspaceSession
                {
                    Name = string.IsNullOrWhiteSpace(s.CustomName) ? null : s.CustomName,
                    Command = s.Command,
                    Directory = s.Panes.FirstOrDefault()?.Directory is { Length: > 0 } dir ? TerminalWorkspaceStore.Abbreviate(dir) : null,
                }).ToList(),
            });
        return workspace;
    }

    /// <summary>
    /// Open a workspace's sessions beside any already open. A session already
    /// open with the same name and command is not opened twice, so opening a
    /// workspace again only fills in what was closed.
    /// </summary>
    public void OpenWorkspace(TerminalWorkspace workspace, bool? show = null)
    {
        Session? first = null;
        foreach (var section in workspace.Sections)
        {
            var name = section.Name?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            if (!_sections.Contains(name)) _sections.Add(name);
            if (section.Collapsed == true) _collapsedSections.Add(name); else _collapsedSections.Remove(name);
            foreach (var entry in section.Sessions ?? new())
            {
                var command = entry.Command ?? "";
                var title = string.IsNullOrWhiteSpace(entry.Name) ? null : entry.Name.Trim();
                if (_sessions.Any(s => string.Equals(s.Command, command, StringComparison.OrdinalIgnoreCase) && s.CustomName == title)) continue;
                var directory = entry.Directory is { Length: > 0 } d ? TerminalWorkspaceStore.Expand(d) : null;
                OpenSession(WorkspaceLaunch(command, directory) with { TakeFocus = false });
                var session = _sessions[^1];
                session.CustomName = title;
                session.Section = name;
                Refresh(session);
                first ??= session;
            }
        }
        RebuildRows();
        if (first != null) Activate(first, false);
        if (show ?? workspace.Show ?? true) WorkspaceShowRequested?.Invoke(workspace.FullWindow ?? false);
    }

    private static TerminalWorkspaceFile? LoadWorkspaces(out string? error)
    {
        try { error = null; return TerminalWorkspaceStore.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            error = $"Could not read {TerminalWorkspaceStore.DefaultPath}: {ex.Message}";
            return null;
        }
    }

    private static void ChangeWorkspaces(Action<TerminalWorkspaceFile> change)
    {
        var file = LoadWorkspaces(out var error);
        if (file == null) { ShowWorkspaceError(error!); return; }
        change(file);
        try { TerminalWorkspaceStore.Save(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowWorkspaceError($"Could not save the workspaces: {ex.Message}"); }
    }

    private static void ShowWorkspaceError(string message) =>
        MessageBox.Show(message, "Terminal Workspaces", MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>The workspaces menu, read from the file each time it opens so hand edits apply without a restart.</summary>
    private void OpenWorkspacesMenu(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        var file = LoadWorkspaces(out var error);
        if (file == null)
            menu.Items.Add(new MenuItem { Header = "The workspaces file could not be read", IsEnabled = false, ToolTip = error });
        else if (file.Workspaces.Count == 0)
            menu.Items.Add(new MenuItem { Header = "No saved workspaces", IsEnabled = false });
        foreach (var workspace in file?.Workspaces ?? new())
        {
            var w = workspace;
            var count = w.SessionCount == 1 ? "1 session" : $"{w.SessionCount} sessions";
            menu.Items.Add(MenuItem($"Open {w.Name} ({count})", (_, _) => OpenWorkspace(w, show: true)));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Save sessions as workspace…", (_, _) =>
            EditName(anchor, "Save workspace", file?.LaunchWorkspace?.Name ?? "", name => {
                if (string.IsNullOrWhiteSpace(name)) return;
                var snapshot = Snapshot(name.Trim());
                ChangeWorkspaces(f => f.Upsert(snapshot));
            }), enabled: _sessions.Count > 0));
        if (file is { Workspaces.Count: > 0 })
        {
            var launch = new MenuItem { Header = "Open at launch" };
            AutomationProperties.SetAutomationId(launch, "TerminalWorkspaceOpenAtLaunch");
            var none = new MenuItem { Header = "None", IsCheckable = true, IsChecked = file.LaunchWorkspace == null };
            none.Click += (_, _) => ChangeWorkspaces(f => f.OpenAtLaunch = null);
            launch.Items.Add(none);
            foreach (var workspace in file.Workspaces)
            {
                var name = workspace.Name;
                var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = ReferenceEquals(file.LaunchWorkspace, workspace) };
                item.Click += (_, _) => ChangeWorkspaces(f => f.OpenAtLaunch = name);
                launch.Items.Add(item);
            }
            menu.Items.Add(launch);
            var delete = new MenuItem { Header = "Delete workspace" };
            foreach (var workspace in file.Workspaces)
            {
                var name = workspace.Name;
                delete.Items.Add(MenuItem(name, (_, _) => ChangeWorkspaces(f => f.Remove(name))));
            }
            menu.Items.Add(delete);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Edit workspaces file", (_, _) => OpenWorkspacesFile(reveal: false)));
        menu.Items.Add(MenuItem("Show workspaces file in Explorer", (_, _) => OpenWorkspacesFile(reveal: true)));
        menu.IsOpen = true;
    }

    /// <summary>Open the workspaces file in its editor, or show it in Explorer, writing an empty one first so there is something to edit.</summary>
    private static void OpenWorkspacesFile(bool reveal)
    {
        var path = TerminalWorkspaceStore.DefaultPath;
        try
        {
            if (!File.Exists(path)) TerminalWorkspaceStore.Save(new TerminalWorkspaceFile(), path);
            if (reveal)
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
                // JSON may have no default app; Notepad always opens it.
                catch (System.ComponentModel.Win32Exception) { System.Diagnostics.Process.Start("notepad.exe", $"\"{path}\""); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowWorkspaceError($"Could not open {path}: {ex.Message}");
        }
    }

    // ── Filters, sort and search ─────────────────────────────────────────

    private TerminalSessionTraits TraitsOf(Session session) =>
        _traits.TryGetValue(session, out var traits) ? traits : ReadTraits(session, null);

    private TerminalSessionTraits ReadTraits(Session session, List<AgentProcessTree.Entry>? snapshot)
    {
        var now = DateTime.UtcNow;
        var state = StateOf(session, now);
        var lead = session.Panes.FirstOrDefault();
        var isAgent = snapshot != null && session.Panes.Any(p =>
            p.Activity.State(now) != ActivityState.Exited && p.ProcessId > 0 && AgentProcessTree.HasAgent(snapshot, p.ProcessId));
        var name = session.CustomName ?? session.AutomaticName ?? lead?.Title ?? session.Label;
        var directory = TerminalPathDisplay.Compact(lead?.Directory, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return new TerminalSessionTraits(
            isAgent,
            state == ActivityState.Active,
            TerminalSessionTraits.Paused(state, isAgent),
            new[] { name, directory ?? "", session.Section, lead?.Title ?? session.Label },
            session.Panes.Select(p => p.Activity.LastActivity).Max());
    }

    /// <summary>Read every session's traits with one process snapshot, update the counts, and redraw a filtered list that changed.</summary>
    private void RefreshTraits()
    {
        if (_sessions.Count == 0 && _traits.Count == 0) return;
        List<AgentProcessTree.Entry> snapshot;
        try { snapshot = AgentProcessTree.Snapshot(); }
        catch (Exception) { snapshot = new(); }
        _traits = _sessions.ToDictionary(s => s, s => ReadTraits(s, snapshot));
        UpdateFilterControls();
        if (!FiltersApply) return;
        var all = _sessions.Select(TraitsOf).ToList();
        var shown = TerminalSessionFilter.Order(all, _sort).Where(i => _filter.Matches(all[i])).Select(i => _sessions[i]);
        if (ShownKey(shown) != _shownKey) RebuildRows();
    }

    private void SetFilter(TerminalSessionFilter filter, bool save = true)
    {
        _filter = filter;
        if (save) UserPreferences.SetTerminalFilters(filter.Active, filter.Running, filter.Paused);
        UpdateFilterControls();
        RebuildRows();
    }

    private void SetSort(TerminalSessionSort sort)
    {
        _sort = sort;
        UserPreferences.SetTerminalSort(sort);
        UpdateFilterControls();
        RebuildRows();
    }

    private void OpenSortMenu(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var sort in Enum.GetValues<TerminalSessionSort>())
        {
            var option = sort;
            var item = new MenuItem { Header = option.Title(), IsCheckable = true, IsChecked = option == _sort };
            AutomationProperties.SetAutomationId(item, "TerminalSort_" + option);
            item.Click += (_, _) => SetSort(option);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void UpdateFilterControls()
    {
        var all = _traits.Values.ToList();
        SetFilterButton(_filterActive, _filter.Active, all.Count(t => t.IsAgent));
        SetFilterButton(_filterRunning, _filter.Running, all.Count(t => t.IsRunning));
        SetFilterButton(_filterPaused, _filter.Paused, all.Count(t => t.IsPaused));
        var sortGlyph = _sort switch { TerminalSessionSort.RecentFirst => "\uE74A", TerminalSessionSort.RecentLast => "\uE74B", _ => "\uE8CB" };
        var sortIcon = (FontIcon)_sortButton.Content;
        sortIcon.Glyph = sortGlyph;
        if (_sort == TerminalSessionSort.Manual) sortIcon.SetResourceReference(FontIcon.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        else sortIcon.SetResourceReference(FontIcon.ForegroundProperty, "SystemControlHighlightAccentBrush");
        _sortButton.ToolTip = $"Sort: {_sort.Title()}";
        AutomationProperties.SetHelpText(_sortButton, _sort.Title());
    }

    /// <summary>A status filter: an icon and a neutral count, tinted while it is on.</summary>
    private static Button FilterButton(string glyph, string name, string tip, string automationId, Action toggle)
    {
        var icon = new FontIcon { Glyph = glyph, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var count = new TextBlock { FontSize = 11, Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        count.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(icon);
        content.Children.Add(count);
        var button = new Button
        {
            Content = content, Tag = (name, tip), Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(0, 0, 1, 0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetAutomationId(button, automationId);
        button.Click += (_, _) => toggle();
        return button;
    }

    private static void SetFilterButton(Button button, bool on, int count)
    {
        var content = (StackPanel)button.Content;
        var icon = (FontIcon)content.Children[0];
        var text = (TextBlock)content.Children[1];
        var (name, tip) = ((string, string))button.Tag;
        text.Text = count > 0 ? count.ToString(System.Globalization.CultureInfo.CurrentCulture) : "";
        // One that is on stays clickable so it can be turned off with no matches.
        button.IsEnabled = on || count > 0;
        button.Opacity = button.IsEnabled ? 1 : 0.45;
        if (on)
        {
            icon.SetResourceReference(FontIcon.ForegroundProperty, "SystemControlHighlightAccentBrush");
            button.SetResourceReference(BackgroundProperty, "SubtleFillBrush");
        }
        else
        {
            icon.SetResourceReference(FontIcon.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
            button.Background = Brushes.Transparent;
        }
        button.ToolTip = on ? "Show all sessions" : tip;
        AutomationProperties.SetName(button, $"{name}, {count} {(count == 1 ? "session" : "sessions")}{(on ? ", on" : "")}");
    }

    private void RenumberRows()
    {
        for (var i = 0; i < _sessions.Count; i++)
        {
            var s = _sessions[i];
            AutomationProperties.SetAutomationId(s.Row, $"TerminalSessionRow{i}");
            s.HintText.Text = i < 9 ? $"Ctrl+{i + 1}" : "";
        }
    }

    private void Refresh(Session session)
    {
        var lead = session.Panes.FirstOrDefault();
        if (lead == null) return;
        var state = StateOf(session, DateTime.UtcNow);
        session.Dot.Fill = state switch
        {
            ActivityState.Active => new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x4F)),
            ActivityState.Attention => new SolidColorBrush(Color.FromRgb(0xE8, 0x89, 0x0C)),
            _ => Brushes.Gray,
        };
        var displayName = session.CustomName ?? session.AutomaticName ?? lead.Title;
        session.TitleText.Text = displayName;
        session.DirectoryText.Text = TerminalPathDisplay.Compact(lead.Directory,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var rowName = $"{displayName}, {state}";
        AutomationProperties.SetName(session.Row, rowName);
        session.Row.ToolTip = $"{displayName}\nProgram: {lead.Title}\n{lead.Directory}\n{state} · directory from {(lead.DirectorySource == "osc" ? "the shell" : "the process")}";
    }

    private static ActivityState StateOf(Session session, DateTime now)
    {
        var states = session.Panes.Select(p => p.Activity.State(now)).ToList();
        return states.Contains(ActivityState.Attention) ? ActivityState.Attention
            : states.Contains(ActivityState.Active) ? ActivityState.Active
            : states.All(s => s == ActivityState.Exited) ? ActivityState.Exited
            : ActivityState.Idle;
    }

    /// <summary>Each session's state, for the strip shown while the panel is closed.</summary>
    public IReadOnlyCollection<ActivityState> SessionStates()
    {
        var now = DateTime.UtcNow;
        return _sessions.Select(s => StateOf(s, now)).ToList();
    }

    /// <summary>The name of what a new session runs, for the strip: "codex", "claude", "shell".</summary>
    public static string DefaultAgentLabel
    {
        get
        {
            var command = Settings.AgentCommand;
            return string.IsNullOrWhiteSpace(command) || command.Equals(AgentCommands.Shell, StringComparison.OrdinalIgnoreCase)
                ? AgentCommands.Shell
                : command.Split(' ')[0];
        }
    }

    private void SetCollapsed(bool collapsed)
    {
        _collapsed = collapsed;
        _listColumn.Width = new GridLength(collapsed ? CollapsedListWidth : ListWidth);
        _headerWide.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        _headerNarrow.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        _filterRow.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        foreach (var s in _sessions)
        {
            var hidden = collapsed ? Visibility.Collapsed : Visibility.Visible;
            s.Details.Visibility = hidden;
            s.HintText.Visibility = hidden;
            ((Button)s.Details.Tag).Visibility = hidden;
        }
        RebuildRows();
    }

    // ── Showing, activating, closing ─────────────────────────────────────

    private void Activate(Session session, bool takeFocus)
    {
        _collapsedSections.Remove(session.Section);
        RebuildRows();
        _active = session;
        Show(session);
        foreach (var s in _sessions)
        {
            if (ReferenceEquals(s, session)) s.Row.SetResourceReference(Border.BackgroundProperty, "SubtleFillBrush");
            else s.Row.Background = Brushes.Transparent;
        }
        if (takeFocus) Dispatcher.BeginInvoke(FocusActive, DispatcherPriority.Input);
    }

    /// <summary>Only the active session is on screen; showing it answers any bell.</summary>
    private void Show(Session session)
    {
        foreach (var s in _sessions)
        {
            var on = ReferenceEquals(s, session) && IsVisible;
            s.Body.Visibility = ReferenceEquals(s, session) ? Visibility.Visible : Visibility.Hidden;
            foreach (var pane in s.Panes)
            {
                pane.IsShown = on;
                if (on) pane.Activity.Shown();
            }
            Refresh(s);
        }
    }

    private void CloseSession(Session session)
    {
        foreach (var pane in session.Panes) pane.Dispose();
        var index = _sessions.IndexOf(session);
        _sessions.Remove(session);
        _traits.Remove(session);
        RebuildRows();
        _body.Children.Remove(session.Body);
        RenumberRows();
        if (ReferenceEquals(_active, session))
        {
            _active = null;
            if (_sessions.Count > 0) Activate(_sessions[Math.Min(index, _sessions.Count - 1)], true);
        }
    }

    /// <summary>Called when the panel is shown or hidden, so bells from a hidden panel count as unseen.</summary>
    public void OnVisibilityChanged()
    {
        if (!IsVisible && _nameEditor != null) _nameEditor.IsOpen = false;
        if (_active != null) Show(_active);
    }

    public void DisposeAll()
    {
        _tick.Stop();
        if (_nameEditor != null) _nameEditor.IsOpen = false;
        UserPreferences.Changed -= OnPreferencesChanged;
        foreach (var session in _sessions.ToList()) CloseSession(session);
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
