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
    private Session? _active;
    private bool _collapsed;
    private int _tickCount;

    /// <summary>Raised when the panel should hide (its Hide button, or Ctrl+` inside a terminal).</summary>
    public event EventHandler? HideRequested;

    /// <summary>Raised when full-window mode should turn on or off (its button, or Ctrl+Shift+Enter inside a terminal).</summary>
    public event EventHandler? FullWindowRequested;

    public bool HasSessions => _sessions.Count > 0;

    /// <summary>Text was dropped on a pane; the window decides whether it is a row's block to hand over.</summary>
    public event Action<TerminalView, string>? TextDropped;

    public TerminalPanel()
    {
        AutomationProperties.SetAutomationId(this, "TerminalPanel");

        // ── Session list ──
        var newButton = IconButton("", "New session (Ctrl+Shift+T)", "TerminalNewButton", (_, _) => OpenDefaultSession());
        var newMenu = IconButton("", "New session…", "TerminalNewMenuButton", (b, _) => OpenNewMenu((Button)b!));
        newMenu.Padding = new Thickness(2, 4, 4, 4);
        _headerWide.Children.Add(newButton);
        _headerWide.Children.Add(newMenu);
        _headerWide.Children.Add(IconButton("", "Split (Alt+Shift+D)", "TerminalSplitButton", (_, _) => SplitActive()));
        _headerWide.Children.Add(IconButton("", "Full Window (Ctrl+Shift+Enter)", "TerminalFullWindowButton", (_, _) => FullWindowRequested?.Invoke(this, EventArgs.Empty)));
        _headerWide.Children.Add(IconButton("", "Hide (Ctrl+`)", "TerminalHideButton", (_, _) => HideRequested?.Invoke(this, EventArgs.Empty)));
        _headerWide.Children.Add(IconButton("", "Collapse the list", "TerminalCollapseButton", (_, _) => SetCollapsed(true)));

        _headerNarrow.Children.Add(IconButton("", "Expand the list", "TerminalExpandButton", (_, _) => SetCollapsed(false)));
        _headerNarrow.Children.Add(IconButton("", "New session (Ctrl+Shift+T)", "TerminalNewButtonCollapsed", (_, _) => OpenDefaultSession()));

        var header = new StackPanel { Margin = new Thickness(4, 4, 4, 4) };
        header.Children.Add(_headerWide);
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
            foreach (var session in _sessions) Refresh(session);
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

    private static TerminalLaunch AgentLaunch(string agentCommand, RepoLocation? repo)
    {
        var command = AgentCommands.Resolve(agentCommand, AgentCommands.FindInstalled);
        var label = string.IsNullOrWhiteSpace(agentCommand) || agentCommand.Equals(AgentCommands.Shell, StringComparison.OrdinalIgnoreCase)
            ? AgentCommands.Shell
            : agentCommand.Split(' ')[0];
        return new TerminalLaunch(repo != null ? $"{label} · {repo.Name}" : label, command, repo?.Path, repo);
    }

    public void OpenSession(TerminalLaunch launch)
    {
        var session = new Session { Label = launch.Title, Body = new Grid() };
        _sessions.Add(session);
        _body.Children.Add(session.Body);
        _rows.Children.Add(BuildRow(session));
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
        session.Row = row;
        session.Details.Tag = close; // hidden with the details when collapsed
        Refresh(session);
        return row;
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
        session.TitleText.Text = lead.Title;
        session.DirectoryText.Text = TerminalPathDisplay.Compact(lead.Directory,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var rowName = $"{lead.Title}, {state}";
        AutomationProperties.SetName(session.Row, rowName);
        session.Row.ToolTip = $"{lead.Title}\n{lead.Directory}\n{state} · directory from {(lead.DirectorySource == "osc" ? "the shell" : "the process")}";
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
        foreach (var s in _sessions)
        {
            var hidden = collapsed ? Visibility.Collapsed : Visibility.Visible;
            s.Details.Visibility = hidden;
            s.HintText.Visibility = hidden;
            ((Button)s.Details.Tag).Visibility = hidden;
        }
    }

    // ── Showing, activating, closing ─────────────────────────────────────

    private void Activate(Session session, bool takeFocus)
    {
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
        _rows.Children.Remove(session.Row);
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
        if (_active != null) Show(_active);
    }

    public void DisposeAll()
    {
        _tick.Stop();
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
