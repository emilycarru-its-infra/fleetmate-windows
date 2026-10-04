using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using FleetMate.Core.Config;
using FleetMate.Core.Services.Terminal;
using ModernWpf.Controls;

namespace FleetMate.GUI.Views.Terminal;

/// <summary>
/// The bottom terminal panel, reachable from every tab: session tabs, a new-
/// session menu (shell, agent, or a session in one of the operator's repos),
/// and a side-by-side split, like VS Code's terminal.
/// </summary>
public sealed class TerminalPanel : UserControl
{
    private sealed class Session
    {
        public required string Title { get; init; }
        public required Grid Body { get; init; }
        public List<TerminalView> Panes { get; } = new();
        public ToggleButton Tab { get; set; } = null!;
    }

    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    /// <summary>
    /// Every session's body stays in this grid and only its visibility
    /// changes: a WebView2 is a hosted window, and taking it out of the tree
    /// would tear it down.
    /// </summary>
    private readonly Grid _body = new();
    private readonly List<Session> _sessions = new();
    private Session? _active;

    /// <summary>Raised when the panel should hide (its close button, or Ctrl+` inside a terminal).</summary>
    public event EventHandler? HideRequested;

    public bool HasSessions => _sessions.Count > 0;

    public TerminalPanel()
    {
        AutomationProperties.SetAutomationId(this, "TerminalPanel");

        var header = new DockPanel { Margin = new Thickness(8, 4, 8, 4) };

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(actions, Dock.Right);
        actions.Children.Add(IconButton("", "New session", "TerminalNewButton", (b, _) => OpenNewMenu((Button)b!)));
        actions.Children.Add(IconButton("", "New session menu", "TerminalNewMenuButton", (b, _) => OpenNewMenu((Button)b!, showAll: true)));
        actions.Children.Add(IconButton("", "Split", "TerminalSplitButton", (_, _) => SplitActive()));
        actions.Children.Add(IconButton("", "Close session", "TerminalKillButton", (_, _) => { if (_active != null) CloseSession(_active); }));
        actions.Children.Add(IconButton("", "Hide panel (Ctrl+`)", "TerminalHideButton", (_, _) => HideRequested?.Invoke(this, EventArgs.Empty)));
        header.Children.Add(actions);

        header.Children.Add(new ScrollViewer
        {
            Content = _tabs,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        });

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(_body);

        var border = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = root
        };
        border.SetResourceReference(Border.BorderBrushProperty, "SystemControlForegroundBaseLowBrush");
        border.SetResourceReference(Border.BackgroundProperty, "AppBackgroundBrush");
        Content = border;
    }

    private static TerminalSettings Settings => ((App)Application.Current).Config.Terminal;

    /// <summary>
    /// The session a new tab opens with: the agent command when AgentAutoStart
    /// is on, otherwise the shell.
    /// </summary>
    public void OpenDefaultSession() =>
        OpenSession(Settings.AgentAutoStart ? AgentLaunch(null) : ShellLaunch(null));

    public void OpenAgentSession() => OpenSession(AgentLaunch(null));

    public void FocusActive() => _active?.Panes.FirstOrDefault()?.FocusTerminal();

    private void OpenNewMenu(Button anchor, bool showAll = false)
    {
        // The + button opens the default session; its chevron offers every kind.
        if (!showAll) { OpenDefaultSession(); return; }

        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        menu.Items.Add(MenuItem("New shell", (_, _) => OpenSession(ShellLaunch(null))));
        menu.Items.Add(MenuItem($"New agent session ({Settings.AgentCommand})", (_, _) => OpenAgentSession()));
        var repos = Settings.EffectiveRepos;
        if (repos.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var entry in repos)
            {
                var repo = RepoLocator.Resolve(entry, RepoLocator.DefaultRoot);
                menu.Items.Add(MenuItem($"New session in {repo.Name}", (_, _) => OpenSession(AgentLaunch(repo))));
            }
        }
        else
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Add repos in Settings ▸ Terminal", IsEnabled = false });
        }
        menu.IsOpen = true;
    }

    private static TerminalLaunch ShellLaunch(RepoLocation? repo) =>
        new(repo?.Name ?? "shell", AgentCommands.DefaultShell(AgentCommands.FindOnPath), repo?.Path, repo);

    private static TerminalLaunch AgentLaunch(RepoLocation? repo)
    {
        var command = AgentCommands.Resolve(Settings.AgentCommand, AgentCommands.FindOnPath);
        var label = string.IsNullOrWhiteSpace(Settings.AgentCommand) ? AgentCommands.Shell : Settings.AgentCommand.Split(' ')[0];
        return new TerminalLaunch(repo != null ? $"{label} · {repo.Name}" : label, command, repo?.Path, repo);
    }

    public void OpenSession(TerminalLaunch launch)
    {
        var session = new Session { Title = launch.Title, Body = new Grid() };
        session.Tab = SessionTab(session);
        _sessions.Add(session);
        _body.Children.Add(session.Body);
        _tabs.Children.Add(session.Tab);
        AddPane(session, launch);
        Activate(session);
    }

    /// <summary>Split the active session side by side with a second pane running the same kind of session.</summary>
    private void SplitActive()
    {
        if (_active == null) { OpenDefaultSession(); return; }
        if (_active.Panes.Count >= 2) return;
        AddPane(_active, Settings.AgentAutoStart ? AgentLaunch(null) : ShellLaunch(null));
    }

    private void AddPane(Session session, TerminalLaunch launch)
    {
        var view = new TerminalView(launch);
        AutomationProperties.SetAutomationId(view, $"TerminalPane{session.Panes.Count}");
        view.ToggleRequested += (_, _) => HideRequested?.Invoke(this, EventArgs.Empty);
        session.Panes.Add(view);
        Layout(session);
    }

    /// <summary>Append the newest pane, after a splitter when it is not the first. Existing panes stay put.</summary>
    private static void Layout(Session session)
    {
        var grid = session.Body;
        var pane = session.Panes[^1];
        if (session.Panes.Count > 1)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var splitter = new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
            splitter.SetResourceReference(BackgroundProperty, "SystemControlForegroundBaseLowBrush");
            AutomationProperties.SetAutomationId(splitter, "TerminalSplitter");
            Grid.SetColumn(splitter, grid.ColumnDefinitions.Count - 1);
            grid.Children.Add(splitter);
        }
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(pane, grid.ColumnDefinitions.Count - 1);
        grid.Children.Add(pane);
    }

    private ToggleButton SessionTab(Session session)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(new FontIcon { Glyph = "", FontSize = 12, Margin = new Thickness(0, 0, 6, 0) });
        label.Children.Add(new TextBlock { Text = session.Title, VerticalAlignment = VerticalAlignment.Center });
        var close = new Button { Content = new FontIcon { Glyph = "", FontSize = 9 }, Padding = new Thickness(4), Margin = new Thickness(6, 0, 0, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = "Close" };
        close.Click += (_, e) => { CloseSession(session); e.Handled = true; };
        label.Children.Add(close);

        var tab = new ToggleButton { Content = label, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(8, 2, 4, 2), FontSize = 12 };
        AutomationProperties.SetName(tab, session.Title);
        AutomationProperties.SetAutomationId(tab, $"TerminalTab{_sessions.Count}");
        tab.Click += (_, _) => Activate(session);
        return tab;
    }

    private void Activate(Session session)
    {
        _active = session;
        foreach (var s in _sessions)
        {
            var on = ReferenceEquals(s, session);
            s.Tab.IsChecked = on;
            s.Body.Visibility = on ? Visibility.Visible : Visibility.Hidden;
        }
        Dispatcher.BeginInvoke(FocusActive, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CloseSession(Session session)
    {
        foreach (var pane in session.Panes) pane.Dispose();
        var index = _sessions.IndexOf(session);
        _sessions.Remove(session);
        _tabs.Children.Remove(session.Tab);
        _body.Children.Remove(session.Body);
        if (ReferenceEquals(_active, session))
        {
            _active = null;
            if (_sessions.Count > 0) Activate(_sessions[Math.Min(index, _sessions.Count - 1)]);
        }
    }

    public void DisposeAll()
    {
        foreach (var session in _sessions.ToList()) CloseSession(session);
    }

    private static Button IconButton(string glyph, string tip, string automationId, RoutedEventHandler click)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
            ToolTip = tip,
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(2, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        AutomationProperties.SetAutomationId(button, automationId);
        AutomationProperties.SetName(button, tip);
        button.Click += click;
        return button;
    }

    private static MenuItem MenuItem(string header, RoutedEventHandler click)
    {
        var item = new MenuItem { Header = header };
        item.Click += click;
        return item;
    }
}
