using FleetMate.Core.Config;
using FleetMate.Core.Services.Repos;
using Serilog;

namespace FleetMate.Core.Services.Terminal;

/// <summary>
/// The Agent Terminal's session list, everything but drawing it: the
/// sessions and their sections, pins, filters and sort, the Repos rows that
/// follow the registry, saved workspaces, reopening at launch, and what the
/// panel's keys do. FleetMate.GUI and FleetMate.WinUI each draw it, building
/// a row and a pane host for what this list raises, so the two apps behave
/// the same. Call it on the UI thread.
/// </summary>
public sealed class TerminalSessionList
{
    public const string DefaultSection = "Sessions";

    /// <summary>The Pinned group's key in the collapsed set, which no section name can take.</summary>
    internal const string PinnedGroup = "\u0001Pinned";

    private const string NoCheckouts = "No repositories are checked out yet. Clone one in Development ▸ Repos.";

    private static readonly RepoRegistryStore Registry = new();

    private readonly Func<TerminalSettings> _settings;
    private readonly AgentSessionHost? _agent;
    private readonly Action<Action> _post;
    private readonly Action<Action> _defer;
    private readonly Func<bool> _isDark;
    private readonly List<TerminalSession> _sessions = new();
    private readonly List<string> _sections = new() { DefaultSection };
    private readonly HashSet<string> _collapsedSections = new();
    /// <summary>Repos sections whose collapsed state has been read from the workspace once; after that the list's own state is written back.</summary>
    private readonly HashSet<string> _repoSectionsSeen = new(StringComparer.Ordinal);
    private TerminalSessionFilter _filter;
    private TerminalSessionSort _sort;
    /// <summary>Each session's traits, read on the slow tick; activity changes without the panel announcing it.</summary>
    private Dictionary<TerminalSession, TerminalSessionTraits> _traits = new();
    private string _shownKey = "";
    private int _tickCount;
    /// <summary>The registry's scan roots and clone root, which row breadcrumbs start below.</summary>
    private IReadOnlyList<string> _roots = Array.Empty<string>();
    private DateTime? _registryStamp;
    private DateTime _lastRepoSync = DateTime.MinValue;
    private bool _visible;

    /// <param name="settings">Settings ▸ Terminal as it is now.</param>
    /// <param name="agent">Starts real sessions; null in demo mode, where no process runs and no file is read or written.</param>
    /// <param name="post">Run on the UI thread soon, from any thread.</param>
    /// <param name="defer">Run on the UI thread once input has been handled, as focus after a menu closes needs.</param>
    /// <param name="isDark">Whether the app draws dark now.</param>
    public TerminalSessionList(Func<TerminalSettings> settings, AgentSessionHost? agent,
        Action<Action> post, Action<Action> defer, Func<bool> isDark)
    {
        _settings = settings;
        _agent = agent;
        _post = post;
        _defer = defer;
        _isDark = isDark;
        IsDemo = agent == null;
        _filter = IsDemo ? new() : new(UserPreferences.TerminalFilterActive, UserPreferences.TerminalFilterRunning, UserPreferences.TerminalFilterPaused);
        _sort = IsDemo ? TerminalSessionSort.Manual : UserPreferences.TerminalSort;
    }

    /// <summary>Demo mode: fictional sessions, no process, no workspace or registry file read or written.</summary>
    public bool IsDemo { get; }

    public IReadOnlyList<TerminalSession> Sessions => _sessions;
    public IReadOnlyList<string> Sections => _sections;
    public TerminalSession? Active { get; private set; }

    /// <summary>Whether a session is selected, started or waiting to start when the panel shows. Repos rows alone do not count.</summary>
    public bool HasSessions => Active != null;

    /// <summary>The session list is folded to its dots.</summary>
    public bool ListCollapsed { get; private set; }

    public TerminalSessionFilter Filter => _filter;
    public TerminalSessionSort Sort => _sort;

    /// <summary>Whether the terminal fills the window now, saved with a workspace. Set by the window.</summary>
    public Func<bool>? IsFullWindow { get; set; }

    // ── What the panel draws from ────────────────────────────────────────

    /// <summary>A session's row came into the list; build its row and its (empty) pane host.</summary>
    public event Action<TerminalSession>? SessionAdded;
    /// <summary>A session left the list; drop its row and pane host.</summary>
    public event Action<TerminalSession>? SessionRemoved;
    /// <summary>Redraw one row from <see cref="RowState"/>.</summary>
    public event Action<TerminalSession>? SessionChanged;
    /// <summary>The order, sections or numbering changed; lay the list out again from <see cref="Entries"/>.</summary>
    public event Action? RowsChanged;
    /// <summary>A pane was added to a session; host it after any it already has.</summary>
    public event Action<TerminalSession, TerminalPaneController>? PaneAdded;
    /// <summary>A session's panes ended and were disposed; empty its pane host.</summary>
    public event Action<TerminalSession>? PanesCleared;
    /// <summary>The selected session changed, or was shown: only its pane host is on screen.</summary>
    public event Action<TerminalSession?>? ActiveChanged;
    /// <summary>The filter buttons' counts or the sort changed.</summary>
    public event Action? FiltersChanged;
    /// <summary>The list folded or unfolded.</summary>
    public event Action? ListCollapsedChanged;
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
    /// <summary>Something about the workspaces file went wrong; say it.</summary>
    public event Action<string>? ErrorRaised;

    /// <summary>The list's lines, in order: section headers and rows, or the empty state.</summary>
    public IReadOnlyList<TerminalListEntry> Entries()
    {
        var entries = new List<TerminalListEntry>();
        if (FiltersApply) return FilteredEntries(entries);
        _shownKey = "";
        // Pinned rows come out of their sections into a group at the top.
        var pinned = _sessions.Where(s => s.Pinned).ToList();
        if (pinned.Count > 0)
        {
            entries.Add(PinnedHeader(pinned));
            if (!_collapsedSections.Contains(PinnedGroup)) entries.AddRange(pinned.Select(s => new TerminalSessionEntry(s)));
        }
        foreach (var section in _sections.ToList())
        {
            var members = _sessions.Where(s => s.Section == section && !s.Pinned).ToList();
            // A Repos section the person has emptied of rows still shows, so rows can be dragged back.
            entries.Add(SectionHeader(section, members));
            if (!_collapsedSections.Contains(section)) entries.AddRange(members.Select(s => new TerminalSessionEntry(s)));
        }
        return entries;
    }

    /// <summary>
    /// Filtered or sorted: sections with no match are left out and the rest
    /// open, so a search finds sessions in a collapsed section too; sorted by
    /// activity, one list across sections.
    /// </summary>
    private IReadOnlyList<TerminalListEntry> FilteredEntries(List<TerminalListEntry> entries)
    {
        var shown = ShownSessions().ToList();
        _shownKey = ShownKey(shown);
        if (_sort == TerminalSessionSort.Manual)
        {
            var pinned = shown.Where(s => s.Pinned).ToList();
            if (pinned.Count > 0)
            {
                entries.Add(PinnedHeader(pinned));
                entries.AddRange(pinned.Select(s => new TerminalSessionEntry(s)));
            }
            foreach (var section in _sections.ToList())
            {
                var members = shown.Where(s => s.Section == section && !s.Pinned).ToList();
                if (members.Count == 0) continue;
                entries.Add(SectionHeader(section, members));
                entries.AddRange(members.Select(s => new TerminalSessionEntry(s)));
            }
        }
        else entries.AddRange(shown.Select(s => new TerminalSessionEntry(s)));
        if (shown.Count == 0) entries.Add(new TerminalEmptyEntry());
        return entries;
    }

    private IEnumerable<TerminalSession> ShownSessions()
    {
        var all = _sessions.Select(TraitsOf).ToList();
        return TerminalSessionFilter.Order(all, _sort).Where(i => _filter.Matches(all[i])).Select(i => _sessions[i]);
    }

    private static string ShownKey(IEnumerable<TerminalSession> shown) =>
        string.Join("|", shown.Select(s => s.GetHashCode()));

    private TerminalSectionEntry PinnedHeader(List<TerminalSession> members) =>
        new(PinnedGroup, members.Count(s => s.IsRunning), _collapsedSections.Contains(PinnedGroup) && !FiltersApply, true, false, false, false);

    private TerminalSectionEntry SectionHeader(string section, List<TerminalSession> members)
    {
        // The count is of running sessions: Repos rows waiting to start are not counted.
        var index = _sections.IndexOf(section);
        return new(section, members.Count(s => s.IsRunning), _collapsedSections.Contains(section) && !FiltersApply, false,
            section != DefaultSection, index > 0, index >= 0 && index < _sections.Count - 1);
    }

    private bool FiltersApply => !ListCollapsed && (_filter.IsFiltering || _sort != TerminalSessionSort.Manual);

    /// <summary>What a row shows now.</summary>
    public TerminalRowState RowState(TerminalSession session)
    {
        var lead = session.Panes.FirstOrDefault();
        var displayName = session.DisplayName;
        string status, glyph;
        TerminalDot dot;
        if (lead == null)
        {
            // Waiting to start: an outlined dot.
            dot = TerminalDot.Waiting;
            (status, glyph) = session.Resume ? ("Resumes when opened", "\uE72C") : ("Not started", "");
        }
        else
        {
            var state = session.StateAt(DateTime.UtcNow);
            var working = state != ActivityState.Exited && TerminalTitleTask.Parse(lead.ProgramTitle).Working == true;
            dot = state == ActivityState.Attention ? TerminalDot.Attention
                : state == ActivityState.Active || working ? TerminalDot.Running
                : TerminalDot.Idle;
            var paused = _traits.TryGetValue(session, out var traits) && traits.IsPaused;
            (status, glyph) = state == ActivityState.Exited ? ("Ended", "")
                : state == ActivityState.Active || working ? ("Running", "\uE945")
                : paused || state == ActivityState.Attention ? ("Paused", "\uE769")
                : ("Idle", "");
        }
        var folder = session.Folder;
        var index = _sessions.IndexOf(session);
        return new TerminalRowState(
            displayName,
            Breadcrumb(session),
            status,
            glyph,
            // A row that has not started shows only its outlined dot; no status line.
            lead != null || session.Resume,
            dot,
            $"{displayName}, {status}",
            lead == null
                ? $"{displayName}\n{folder}\n{status} · starts when chosen"
                : $"{displayName}\nProgram: {lead.Title}\n{folder}\n{status} · directory from {(lead.DirectorySource == "osc" ? "the shell" : "the process")}",
            index is >= 0 and < 9 ? $"Ctrl+{index + 1}" : "",
            session.CloseTitle);
    }

    private string Breadcrumb(TerminalSession session) =>
        session.Folder is { Length: > 0 } folder ? TerminalBreadcrumb.Make(folder, _roots) : "";

    // ── Text size ────────────────────────────────────────────────────────

    /// <summary>The size every session draws at now: the app's text size plus the terminal's own offset.</summary>
    public static double TerminalTextSize => TerminalFontSize.For(UserPreferences.TextScale, UserPreferences.TerminalFontOffset);

    /// <summary>Draw every pane at the app's text size; call when the preferences change.</summary>
    public void ApplyFontSize()
    {
        var size = TerminalTextSize;
        foreach (var pane in _sessions.SelectMany(s => s.Panes)) pane.SetFontSize(size);
    }

    /// <summary>Ctrl+= and Ctrl+- inside a terminal: a step from the app's text size, for the terminal alone.</summary>
    private static void StepFont(double points) =>
        UserPreferences.SetTerminalFontOffset(
            TerminalFontSize.Step(UserPreferences.TextScale, UserPreferences.TerminalFontOffset, points));

    /// <summary>Follow the app into light or dark.</summary>
    public void ApplyTheme(bool dark)
    {
        foreach (var pane in _sessions.SelectMany(s => s.Panes)) pane.SetTheme(dark);
    }

    // ── Commands ─────────────────────────────────────────────────────────

    private TerminalSettings Settings => _settings();

    /// <summary>
    /// What a new session runs: the setting, except that the built-in default
    /// (Codex) gives way to the shell on a PC without Codex.
    /// </summary>
    public string DefaultCommand =>
        IsDemo ? "codex"
        : Settings.AgentCommandIsBuiltInDefault && AgentCommands.FindInstalled(Settings.AgentCommand) == null
            ? AgentCommands.Shell
            : Settings.AgentCommand;

    /// <summary>Every command a default launch may have been written into a Repos session as, before those sessions followed the default.</summary>
    private IReadOnlyCollection<string> FormerDefaultCommands => new[]
    {
        DefaultCommand, Settings.AgentCommand, Settings.AgentCommandFallback, TerminalSettings.DefaultAgentCommand, AgentCommands.Shell,
        "", "claude", "codex", "claude-remote", "codex-remote",
    };

    /// <summary>The name of last resort for a session running <paramref name="command"/>.</summary>
    public static string CommandLabel(string command) =>
        string.IsNullOrWhiteSpace(command) || command.Equals(AgentCommands.Shell, StringComparison.OrdinalIgnoreCase)
            ? AgentCommands.Shell
            : command.Split(' ')[0];

    private static string PresetCommand(string preset) =>
        preset.Equals(AgentCommands.Shell, StringComparison.OrdinalIgnoreCase) ? "" : preset;

    /// <summary>The New menu's presets that are installed (the shell always is).</summary>
    public IReadOnlyList<string> InstalledPresets() =>
        IsDemo ? AgentCommands.Choices : AgentCommands.InstalledChoices(AgentCommands.FindInstalled);

    /// <summary>Whether a Switch Agent or Duplicate preset can run here.</summary>
    public bool IsPresetInstalled(string preset) =>
        IsDemo || AgentCommands.IsInstalled(preset, AgentCommands.FindInstalled);

    /// <summary>Settings ▸ Terminal's own command when it is not a preset, for the New menu's custom item.</summary>
    public string? CustomCommand
    {
        get
        {
            var custom = Settings.AgentCommand;
            return custom.Length > 0 && !AgentCommands.Choices.Contains(custom, StringComparer.OrdinalIgnoreCase) ? custom : null;
        }
    }

    /// <summary>The repositories Settings ▸ Terminal lists, for Open in repository.</summary>
    public IReadOnlyList<RepoLocation> Repositories() =>
        Settings.EffectiveRepos.Select(entry => RepoLocator.Resolve(entry, RepoLocator.DefaultRoot)).ToList();

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
        var label = CommandLabel(agentCommand);
        return new TerminalLaunch(repo != null ? $"{label} · {repo.Name}" : label, command, repo?.Path, repo)
        {
            AgentCommand = label == AgentCommands.Shell ? "" : agentCommand,
        };
    }

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

    /// <summary>A New menu preset: the shell, or an agent CLI.</summary>
    public void OpenPreset(string preset) =>
        OpenSession(preset == AgentCommands.Shell ? ShellLaunch(null) : AgentLaunch(preset, null));

    /// <summary>The New menu's custom command, from Settings ▸ Terminal.</summary>
    public void OpenCustom()
    {
        if (CustomCommand is { } custom) OpenSession(AgentLaunch(custom, null));
    }

    /// <summary>The default launch in <paramref name="repo"/>, cloning it first when it is a URL not yet cloned.</summary>
    public void OpenInRepository(RepoLocation repo) => OpenSession(AgentLaunch(DefaultCommand, repo));

    public void OpenSession(TerminalLaunch launch)
    {
        var session = new TerminalSession { Label = launch.Title, Command = launch.AgentCommand, StartedAt = DateTime.UtcNow };
        _sessions.Add(session);
        SessionAdded?.Invoke(session);
        RowsChanged?.Invoke();
        AddPane(session, launch);
        Activate(session, launch.TakeFocus);
    }

    /// <summary>Split the selected session into two panes; it never opens the first one.</summary>
    public void SplitActive()
    {
        if (Active == null || Active.Panes.Count >= 2) return;
        AddPane(Active, AgentLaunch(DefaultCommand, null));
        Show(Active);
    }

    private void AddPane(TerminalSession session, TerminalLaunch launch)
    {
        if (IsDemo) launch = launch with { DemoTranscript = launch.DemoTranscript ?? DemoFixtures.Transcript(launch.Title) };
        var pane = new TerminalPaneController(launch, _agent, _post, _isDark);
        pane.SetFontSize(TerminalTextSize);
        pane.ToggleRequested += (_, _) => HideRequested?.Invoke();
        pane.StateChanged += (_, _) => SessionChanged?.Invoke(session);
        pane.KeyAction += OnKeyAction;
        pane.TextDropped += (p, text) => TextDropped?.Invoke(p, text);
        pane.Exited += (_, _) => OnPaneExited(session, pane);
        session.Panes.Add(pane);
        PaneAdded?.Invoke(session, pane);
    }

    /// <summary>Tell every pane whether a row is being dragged, so they show the copy cursor only then.</summary>
    public void SetAgentDragActive(bool active)
    {
        foreach (var pane in _sessions.SelectMany(s => s.Panes)) pane.SetAgentDragActive(active);
    }

    public void FocusActive()
    {
        if (Active == null) return;
        if (!Active.IsStarted && _visible) Start(Active);
        Active.Panes.FirstOrDefault()?.RequestFocus();
    }

    /// <summary>
    /// Put <paramref name="text"/> into an agent's input without pressing
    /// Return, activating and focusing that session. Only a pane running an
    /// agent CLI that has asked for bracketed paste receives it, never a bare
    /// shell; <paramref name="preferred"/> (the pane a row was dropped on) is
    /// tried first, then the active session. Returns false when no agent
    /// is running, so the caller copies the text instead and nothing is ever
    /// sent on the person's behalf.
    /// </summary>
    public bool Insert(string text, TerminalPaneController? preferred = null)
    {
        var candidates = (Active == null ? _sessions : _sessions.Where(s => !ReferenceEquals(s, Active)).Prepend(Active))
            .SelectMany(s => s.Panes.Select(p => (Session: s, Pane: p)))
            .OrderBy(c => ReferenceEquals(c.Pane, preferred) ? 0 : 1);
        foreach (var (session, pane) in candidates)
        {
            if (!pane.AcceptsBracketedPaste || !pane.AgentIsRunning) continue;
            if (!pane.PasteText(text)) continue;
            Activate(session, false);
            _defer(pane.RequestFocus);
            return true;
        }
        return false;
    }

    // ── Keys from inside a terminal ──────────────────────────────────────

    private void OnKeyAction(TerminalPaneController? source, TerminalAction action, int index)
    {
        switch (action)
        {
            case TerminalAction.NewSession: OpenDefaultSession(); break;
            case TerminalAction.CloseSession: if (Active != null) Close(Active); break;
            case TerminalAction.Split: SplitActive(); break;
            case TerminalAction.Clear: (source ?? Active?.Panes.FirstOrDefault())?.Clear(); break;
            case TerminalAction.Select: if (index >= 0 && index < _sessions.Count) Activate(_sessions[index], true); break;
            case TerminalAction.Next: Cycle(+1); break;
            case TerminalAction.Previous: Cycle(-1); break;
            case TerminalAction.FullWindow: FullWindowRequested?.Invoke(); break;
            case TerminalAction.ZoomIn: StepFont(1); break;
            case TerminalAction.ZoomOut: StepFont(-1); break;
            case TerminalAction.ActualSize: UserPreferences.SetTerminalFontOffset(0); break;
            case TerminalAction.ToggleSessionList: ToggleSessionList(); break;
        }
    }

    /// <summary>The same chords, pressed while focus is elsewhere in the app (the window forwards them).</summary>
    public bool HandleAppKey(TerminalAction action, int index)
    {
        if (action == TerminalAction.None) return false;
        OnKeyAction(null, action, index);
        return true;
    }

    /// <summary>Next or previous session, skipping rows that have not started, so cycling never starts one.</summary>
    private void Cycle(int step)
    {
        var started = _sessions.Where(s => s.IsStarted || ReferenceEquals(s, Active)).ToList();
        if (started.Count == 0 || Active == null) return;
        var i = (started.IndexOf(Active) + step + started.Count) % started.Count;
        Activate(started[i], true);
    }

    // ── Row and section actions ──────────────────────────────────────────

    public void Rename(TerminalSession session, string name)
    {
        if (!_sessions.Contains(session)) return;
        session.CustomName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        SessionChanged?.Invoke(session);
        WriteBackRepos();
    }

    public void UseAutomaticName(TerminalSession session)
    {
        session.CustomName = null;
        SessionChanged?.Invoke(session);
        WriteBackRepos();
    }

    public void TogglePin(TerminalSession session)
    {
        session.Pinned = !session.Pinned;
        RowsChanged?.Invoke();
        WriteBackRepos();
    }

    /// <summary>A new, empty section at the end; a blank or taken name does nothing.</summary>
    public void AddSection(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || _sections.Contains(name)) return;
        _sections.Add(name);
        RowsChanged?.Invoke();
    }

    /// <summary>The Pinned group's header, or a section's: collapse or expand it.</summary>
    public void ToggleCollapsed(TerminalSectionEntry header)
    {
        var key = header.IsPinned ? PinnedGroup : header.Name;
        if (!_collapsedSections.Add(key)) _collapsedSections.Remove(key);
        RowsChanged?.Invoke();
        if (!header.IsPinned) WriteBackRepos();
    }

    public void RenameSection(string section, string name)
    {
        name = name.Trim();
        var index = _sections.IndexOf(section);
        if (index < 0 || section == DefaultSection || name.Length == 0 || _sections.Contains(name)) return;
        _sections[index] = name;
        foreach (var member in _sessions.Where(s => s.Section == section).ToList()) member.Section = name;
        if (_collapsedSections.Remove(section)) _collapsedSections.Add(name);
        if (_repoSectionsSeen.Remove(section)) _repoSectionsSeen.Add(name);
        RowsChanged?.Invoke();
        WriteBackRepos();
    }

    public void RemoveSection(string section)
    {
        if (section == DefaultSection) return;
        _sections.Remove(section);
        _collapsedSections.Remove(section);
        foreach (var member in _sessions.Where(s => s.Section == section).ToList()) member.Section = DefaultSection;
        RowsChanged?.Invoke();
        WriteBackRepos();
    }

    public void MoveSection(string section, int offset)
    {
        var index = _sections.IndexOf(section);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= _sections.Count) return;
        (_sections[index], _sections[target]) = (_sections[target], _sections[index]);
        RowsChanged?.Invoke();
        WriteBackRepos();
    }

    /// <summary>Put <paramref name="session"/> before <paramref name="before"/>, or at the end of <paramref name="section"/>.</summary>
    public void MoveSession(TerminalSession session, TerminalSession? before, string section)
    {
        if (ReferenceEquals(session, before) || !_sections.Contains(section) || !_sessions.Contains(session)) return;
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
        RowsChanged?.Invoke();
        WriteBackRepos();
    }

    /// <summary>
    /// Whether Switch Agent to <paramref name="preset"/> would interrupt
    /// work, and the question to ask first; null when it can go ahead.
    /// </summary>
    public string? SwitchAgentQuestion(TerminalSession session, string preset)
    {
        if (TerminalSessionActions.IsCurrent(PresetCommand(preset), session.Command) || !session.IsBusy) return null;
        var label = TerminalSessionActions.Presets.First(p => p.Command == preset).Label;
        return $"{session.DisplayName} is working. Restart it with {label}?";
    }

    /// <summary>
    /// Run another agent in the session: it restarts in the folder it is in,
    /// keeping its name and place, and runs that agent from now on rather
    /// than the default. A row waiting to start runs it when chosen. Ask
    /// <see cref="SwitchAgentQuestion"/> first.
    /// </summary>
    public void SwitchAgent(TerminalSession session, string preset)
    {
        if (!_sessions.Contains(session)) return;
        var command = PresetCommand(preset);
        var same = TerminalSessionActions.IsCurrent(command, session.Command);
        if (same && !session.FollowsDefault) return;
        session.FollowsDefault = false;
        if (!same)
        {
            var folder = session.Folder;
            var wasStarted = session.IsStarted;
            StopProcesses(session);
            session.Command = command;
            session.Label = CommandLabel(command);
            session.Resume = false;
            session.StartedResuming = false;
            if (!session.IsRepoRow) session.StartDirectory = folder;
            if (wasStarted)
            {
                session.StartedAt = DateTime.UtcNow;
                AddPane(session, WorkspaceLaunch(command, folder) with { TakeFocus = false });
                if (ReferenceEquals(Active, session)) Show(session);
            }
        }
        SessionChanged?.Invoke(session);
        if (session.IsRepoRow) WriteBackRepos();
    }

    /// <summary>
    /// A new session in the same folder, directly below the original in its
    /// section, named "&lt;name&gt; 2", and selected. <paramref name="preset"/> null
    /// runs what the original runs. In a Repos section it is one the person
    /// added, so sync never takes it out.
    /// </summary>
    public void DuplicateSession(TerminalSession original, string? preset)
    {
        if (!_sessions.Contains(original)) return;
        var command = preset == null ? original.Command : PresetCommand(preset);
        var followsDefault = preset == null && original.FollowsDefault;
        var folder = original.Folder;
        var addedId = original.IsRepoRow ? TerminalSessionActions.NewId() : null;
        var copy = new TerminalSession
        {
            Label = CommandLabel(command),
            Command = command,
            FollowsDefault = followsDefault,
            CustomName = TerminalSessionActions.DuplicateName(original.DisplayName, _sessions.Select(s => s.DisplayName)),
            WorkspaceLabel = original.WorkspaceLabel,
            Section = original.Section,
            StartDirectory = folder,
            RepoFolder = addedId != null ? TerminalWorkspaceBaseline.RowKey(folder ?? original.StartDirectory ?? "", addedId) : null,
            AddedId = addedId,
        };
        _sessions.Insert(_sessions.IndexOf(original) + 1, copy);
        SessionAdded?.Invoke(copy);
        RowsChanged?.Invoke();
        Activate(copy, true);
        if (copy.IsRepoRow) WriteBackRepos();
    }

    /// <summary>
    /// Close a row: a running Repos row stops its program and stays in the
    /// list; a Repos row that has not started leaves the list, and the Repos
    /// workspace with it; any other session closes.
    /// </summary>
    public void Close(TerminalSession session)
    {
        if (session.IsRepoRow && session.IsStarted)
        {
            StopProcesses(session);
            SessionChanged?.Invoke(session);
            if (ReferenceEquals(Active, session)) SelectNearestRunning(_sessions.IndexOf(session));
            RowsChanged?.Invoke();
            return;
        }
        var wasRepoRow = session.IsRepoRow;
        RemoveRow(session);
        if (wasRepoRow) WriteBackRepos();
    }

    /// <summary>Show <paramref name="folder"/> in File Explorer.</summary>
    public static void ShowInExplorer(string folder)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"\"{folder}\""); }
        catch (System.ComponentModel.Win32Exception ex) { Log.Warning(ex, "Terminal: could not show {Folder}", folder); }
    }

    // ── Starting, showing, activating ────────────────────────────────────

    /// <summary>Start a waiting row's program in its folder, resuming the agent's last conversation when the row asks for it.</summary>
    private void Start(TerminalSession session)
    {
        if (session.IsStarted) return;
        if (session.FollowsDefault)
        {
            session.Command = DefaultCommand;
            session.Label = CommandLabel(session.Command);
        }
        var resume = session.Resume ? AgentResume.Command(session.Command) : null;
        session.Resume = false;
        session.StartedResuming = resume != null;
        session.StartedAt = DateTime.UtcNow;
        AddPane(session, WorkspaceLaunch(resume ?? session.Command, session.StartDirectory) with { TakeFocus = false });
        SessionChanged?.Invoke(session);
    }

    /// <summary>
    /// A resumed agent that quits within moments found nothing to resume (or
    /// failed to): start it again fresh, once.
    /// </summary>
    private void OnPaneExited(TerminalSession session, TerminalPaneController pane)
    {
        if (!session.StartedResuming || DateTime.UtcNow - session.StartedAt > TimeSpan.FromSeconds(15)) return;
        session.StartedResuming = false;
        _post(() =>
        {
            if (!_sessions.Contains(session) || !session.Panes.Contains(pane)) return;
            Log.Information("Terminal: resuming {Command} ended at once; starting it fresh", session.Command);
            StopProcesses(session);
            Start(session);
            if (ReferenceEquals(Active, session)) Show(session);
        });
    }

    /// <summary>Select a session; a Repos row or a reopened one starts now.</summary>
    public void Activate(TerminalSession session, bool takeFocus)
    {
        if (!_sessions.Contains(session)) return;
        if (session.Pinned) _collapsedSections.Remove(PinnedGroup);
        else _collapsedSections.Remove(session.Section);
        Start(session);
        RowsChanged?.Invoke();
        Active = session;
        Show(session);
        if (takeFocus) _defer(FocusActive);
    }

    /// <summary>Only the active session is on screen; showing it answers any bell. A selected row waiting to start starts once it is on screen.</summary>
    private void Show(TerminalSession session)
    {
        if (_visible && !session.IsStarted) Start(session);
        foreach (var s in _sessions)
        {
            var on = ReferenceEquals(s, session) && _visible;
            foreach (var pane in s.Panes)
            {
                pane.IsShown = on;
                if (on) pane.Activity.Shown();
            }
            SessionChanged?.Invoke(s);
        }
        ActiveChanged?.Invoke(Active);
    }

    /// <summary>End the row's programs and empty its pane host, leaving the row.</summary>
    private void StopProcesses(TerminalSession session)
    {
        foreach (var pane in session.Panes) pane.Dispose();
        session.Panes.Clear();
        PanesCleared?.Invoke(session);
        _traits.Remove(session);
    }

    private void RemoveRow(TerminalSession session)
    {
        StopProcesses(session);
        var index = _sessions.IndexOf(session);
        _sessions.Remove(session);
        SessionRemoved?.Invoke(session);
        RowsChanged?.Invoke();
        if (ReferenceEquals(Active, session)) SelectNearestRunning(index);
    }

    /// <summary>After a close: the running session nearest where the closed one was, never a row that would start.</summary>
    private void SelectNearestRunning(int index)
    {
        Active = null;
        var nearest = _sessions.Select((s, i) => (Session: s, Distance: Math.Abs(i - index)))
            .Where(c => c.Session.IsStarted)
            .OrderBy(c => c.Distance)
            .Select(c => c.Session)
            .FirstOrDefault();
        if (nearest != null) Activate(nearest, true);
        else ActiveChanged?.Invoke(null);
    }

    /// <summary>
    /// The panel was shown or hidden, so bells from a hidden panel count as
    /// unseen. Shown with nothing selected, it opens a fresh session, never a Repos row.
    /// </summary>
    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (Active != null) Show(Active);
        else if (visible) OpenDefaultSession(takeFocus: false);
    }

    /// <summary>Ctrl+Shift+S: show or hide the session list, as the divider's double-click does.</summary>
    public void ToggleSessionList() => SetListCollapsed(!ListCollapsed);

    public void SetListCollapsed(bool collapsed)
    {
        ListCollapsed = collapsed;
        ListCollapsedChanged?.Invoke();
        RowsChanged?.Invoke();
    }

    // ── Filters, sort and search ─────────────────────────────────────────

    private TerminalSessionTraits TraitsOf(TerminalSession session) =>
        _traits.TryGetValue(session, out var traits) ? traits : ReadTraits(session, null);

    private TerminalSessionTraits ReadTraits(TerminalSession session, List<AgentProcessTree.Entry>? snapshot)
    {
        var now = DateTime.UtcNow;
        var lead = session.Panes.FirstOrDefault();
        // A row that has not started is never running, paused or an agent; search still finds it.
        var state = session.IsStarted ? session.StateAt(now) : ActivityState.Idle;
        var isAgent = snapshot != null && session.Panes.Any(p =>
            p.Activity.State(now) != ActivityState.Exited && p.ProcessId > 0 && AgentProcessTree.HasAgent(snapshot, p.ProcessId));
        var working = state != ActivityState.Exited && TerminalTitleTask.Parse(lead?.ProgramTitle).Working == true;
        var directory = TerminalPathDisplay.Compact(session.Folder, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return new TerminalSessionTraits(
            isAgent,
            state == ActivityState.Active || working,
            TerminalSessionTraits.Paused(state, isAgent),
            new[] { session.DisplayName, directory ?? "", Breadcrumb(session), session.WorkspaceLabel ?? "", session.Section, lead?.Title ?? session.Label },
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
        FiltersChanged?.Invoke();
        if (!FiltersApply) return;
        if (ShownKey(ShownSessions()) != _shownKey) RowsChanged?.Invoke();
    }

    /// <summary>The three status filters as their buttons show them.</summary>
    public IReadOnlyList<TerminalFilterButton> FilterButtons()
    {
        var all = _traits.Values.ToList();
        return new[]
        {
            new TerminalFilterButton(TerminalFilterKind.Active, "\uE91F", "Active", _filter.Active, all.Count(t => t.IsAgent)),
            new TerminalFilterButton(TerminalFilterKind.Running, "\uE945", "Running", _filter.Running, all.Count(t => t.IsRunning)),
            new TerminalFilterButton(TerminalFilterKind.Paused, "\uE769", "Paused", _filter.Paused, all.Count(t => t.IsPaused)),
        };
    }

    public void ToggleFilter(TerminalFilterKind kind) => SetFilter(kind switch
    {
        TerminalFilterKind.Active => _filter with { Active = !_filter.Active },
        TerminalFilterKind.Running => _filter with { Running = !_filter.Running },
        _ => _filter with { Paused = !_filter.Paused },
    });

    /// <summary>The search box's text; not saved.</summary>
    public void SetQuery(string query) => SetFilter(_filter with { Query = query }, save: false);

    /// <summary>No toggles and no search.</summary>
    public void ClearFilters() => SetFilter(new TerminalSessionFilter());

    private void SetFilter(TerminalSessionFilter filter, bool save = true)
    {
        _filter = filter;
        if (save && !IsDemo) UserPreferences.SetTerminalFilters(filter.Active, filter.Running, filter.Paused);
        FiltersChanged?.Invoke();
        RowsChanged?.Invoke();
    }

    public void SetSort(TerminalSessionSort sort)
    {
        _sort = sort;
        if (!IsDemo) UserPreferences.SetTerminalSort(sort);
        FiltersChanged?.Invoke();
        RowsChanged?.Invoke();
    }

    /// <summary>The sort button's glyph: by section, or recent first or last.</summary>
    public string SortGlyph => _sort switch
    {
        TerminalSessionSort.RecentFirst => "\uE74A",
        TerminalSessionSort.RecentLast => "\uE74B",
        _ => "\uE8CB",
    };

    // ── Timers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Every half second: activity dots decay, shells that never report their
    /// directory are polled, traits are read every third tick, and an agent's
    /// own title and section are picked up from its session file.
    /// </summary>
    public void Tick()
    {
        if (++_tickCount % 4 == 0)
            foreach (var pane in _sessions.SelectMany(s => s.Panes)) pane.PollDirectory();
        if (_tickCount % 3 == 0) RefreshTraits();
        var sectionsChanged = false;
        foreach (var session in _sessions.ToList())
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
            SessionChanged?.Invoke(session);
        }
        if (sectionsChanged) RowsChanged?.Invoke();
    }

    /// <summary>Every five seconds: the Repos rows follow the registry when it changed.</summary>
    public void RepoTick()
    {
        if (IsDemo) return;
        var stamp = RegistryStamp();
        if (stamp == _registryStamp) return;
        _registryStamp = stamp;
        SyncRepos();
    }

    /// <summary>The app came to the front: bring the Repos rows up to date, at most every two seconds.</summary>
    public void OnAppActivated()
    {
        if (DateTime.UtcNow - _lastRepoSync > TimeSpan.FromSeconds(2)) SyncRepos();
    }

    // ── Repos rows ───────────────────────────────────────────────────────

    /// <summary>The checkouts the Repos tab knows, with their activity.</summary>
    public IReadOnlyList<TerminalWorkspaceBaseline.Checkout> RepoCheckouts()
    {
        if (IsDemo) return Array.Empty<TerminalWorkspaceBaseline.Checkout>();
        try { return TerminalWorkspaceBaseline.Checkouts(Registry.Load()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return Array.Empty<TerminalWorkspaceBaseline.Checkout>();
        }
    }

    private static DateTime? RegistryStamp()
    {
        try { return File.Exists(Registry.RegistryPath) ? File.GetLastWriteTimeUtc(Registry.RegistryPath) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The registry's scan roots and clone root, expanded, for breadcrumbs.</summary>
    private static IReadOnlyList<string> RepoRoots()
    {
        try
        {
            var settings = Registry.Load().Settings;
            return settings.ScanRoots.Append(settings.CloneRoot).Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(RepoSettings.Expand).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Bring the Repos workspace in step with the registry (written whenever
    /// the file has none, unless the person deleted it), then the list's Repos
    /// rows in step with the workspace. Runs at launch, when the app comes to
    /// the front, and within five seconds of the registry changing.
    /// </summary>
    public void SyncRepos()
    {
        if (IsDemo) return;
        _lastRepoSync = DateTime.UtcNow;
        _registryStamp ??= RegistryStamp();
        _roots = RepoRoots();
        var file = LoadWorkspaces(out _);
        if (file == null) return;
        if (TerminalWorkspaceBaseline.SyncBaseline(file, RepoCheckouts(), FormerDefaultCommands, DateTime.UtcNow))
        {
            try { TerminalWorkspaceStore.Save(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning(ex, "Terminal workspaces: could not write the Repos workspace");
            }
        }
        ApplyRepoWorkspace(file.Baseline);
    }

    /// <summary>
    /// Rows for the Repos workspace's sessions, matched by folder: new folders
    /// get a row that waits to start, and a row whose folder left the workspace
    /// goes, or, when it is running, stays as an ordinary session.
    /// </summary>
    private void ApplyRepoWorkspace(TerminalWorkspace? workspace)
    {
        var entries = new Dictionary<string, (TerminalWorkspaceSession Entry, TerminalWorkspaceSection Section)>(StringComparer.Ordinal);
        foreach (var section in workspace?.Sections ?? new())
            foreach (var entry in section.Sessions)
                if (entry.Directory is { Length: > 0 })
                    entries.TryAdd(TerminalWorkspaceBaseline.RowKey(entry), (entry, section));

        var changed = false;
        foreach (var session in _sessions.Where(s => s.IsRepoRow && !entries.ContainsKey(s.RepoFolder!)).ToList())
        {
            if (session.IsStarted) { session.RepoFolder = null; session.FollowsDefault = false; }
            else RemoveRow(session);
            changed = true;
        }

        var present = _sessions.Where(s => s.IsRepoRow).Select(s => s.RepoFolder!).ToHashSet(StringComparer.Ordinal);
        foreach (var section in workspace?.Sections ?? new())
        {
            var name = section.Name?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            // Repos sections follow the person's own; a new session never lands in one.
            if (!_sections.Contains(name)) { _sections.Add(name); changed = true; }
            if (_repoSectionsSeen.Add(name) && section.Collapsed == true) _collapsedSections.Add(name);
            foreach (var entry in section.Sessions)
            {
                if (entry.Directory is not { Length: > 0 }) continue;
                var key = TerminalWorkspaceBaseline.RowKey(entry);
                if (!present.Add(key)) continue;
                AddWaitingRow(entry, name, key);
                changed = true;
            }
        }
        if (changed) RowsChanged?.Invoke();
    }

    /// <summary>A row that runs nothing until it is chosen. A Repos row with no command follows the default launch.</summary>
    internal TerminalSession AddWaitingRow(TerminalWorkspaceSession entry, string section, string? repoFolder)
    {
        var followsDefault = repoFolder != null && entry.Command == null;
        var command = followsDefault ? DefaultCommand : entry.Command ?? "";
        var session = new TerminalSession
        {
            Label = CommandLabel(command),
            Command = command,
            FollowsDefault = followsDefault,
            CustomName = string.IsNullOrWhiteSpace(entry.Name) ? null : entry.Name.Trim(),
            WorkspaceLabel = string.IsNullOrWhiteSpace(entry.Label) ? null : entry.Label.Trim(),
            Section = section,
            RepoFolder = repoFolder,
            StartDirectory = entry.Directory is { Length: > 0 } d ? TerminalWorkspaceStore.Expand(d) : null,
            Pinned = entry.Pinned == true,
            Resume = entry.Resume == true,
            AddedId = repoFolder != null && entry.Added == true ? entry.Id : null,
        };
        if (!_sections.Contains(section)) _sections.Add(section);
        _sessions.Add(session);
        SessionAdded?.Invoke(session);
        return session;
    }

    /// <summary>
    /// Write the Repos rows back to the Repos workspace by folder: names,
    /// pins, sections and their order and collapsed state, and the rows the
    /// person removed (they stay out, since the workspace remembers placing them).
    /// </summary>
    private void WriteBackRepos()
    {
        if (IsDemo) return;
        var file = LoadWorkspaces(out _);
        if (file?.Baseline is not { } workspace) return;
        var rows = _sessions.Where(s => s.IsRepoRow).Select(s => new TerminalSessionActions.RepoRow(
            s.RepoFolder!, s.Section, s.WorkspaceLabel, s.FollowsDefault ? null : s.Command, s.StartDirectory,
            s.CustomName, s.Pinned, s.AddedId)).ToList();
        if (!TerminalSessionActions.WriteBack(workspace, _sections, _collapsedSections, rows)) return;
        try { TerminalWorkspaceStore.Save(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Terminal workspaces: could not write back the Repos rows");
        }
    }

    // ── Saved workspaces ─────────────────────────────────────────────────

    /// <summary>The sessions open now, by section, as a workspace named <paramref name="name"/>. A session keeps its name only when the person gave it one.</summary>
    public TerminalWorkspace Snapshot(string name)
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
                    Label = s.WorkspaceLabel,
                    Command = s.Command,
                    Directory = s.Folder is { Length: > 0 } dir ? TerminalWorkspaceStore.Abbreviate(dir) : null,
                    Pinned = s.Pinned ? true : null,
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
        // The Repos workspace is always in the list as rows that start when chosen; opening it only brings them up to date.
        if (workspace.Source == TerminalWorkspaceBaseline.Source)
        {
            SyncRepos();
            if (show ?? workspace.Show ?? true) WorkspaceShowRequested?.Invoke(workspace.FullWindow ?? false);
            return;
        }
        TerminalSession? first = null;
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
                if (_sessions.Any(s => !s.IsRepoRow && string.Equals(s.Command, command, StringComparison.OrdinalIgnoreCase) && s.CustomName == title)) continue;
                var directory = entry.Directory is { Length: > 0 } d ? TerminalWorkspaceStore.Expand(d) : null;
                OpenSession(WorkspaceLaunch(command, directory) with { TakeFocus = false });
                var session = _sessions[^1];
                session.CustomName = title;
                session.WorkspaceLabel = string.IsNullOrWhiteSpace(entry.Label) ? null : entry.Label.Trim();
                session.Pinned = entry.Pinned == true;
                session.Section = name;
                SessionChanged?.Invoke(session);
                first ??= session;
            }
        }
        RowsChanged?.Invoke();
        if (first != null) Activate(first, false);
        if (show ?? workspace.Show ?? true) WorkspaceShowRequested?.Invoke(workspace.FullWindow ?? false);
    }

    /// <summary>Save the sessions open now as a workspace, replacing one of the same name.</summary>
    public void SaveWorkspace(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var snapshot = Snapshot(name.Trim());
        ChangeWorkspaces(f => f.Upsert(snapshot));
    }

    /// <summary>Name the workspace FleetMate opens at launch; null opens none.</summary>
    public void SetOpenAtLaunch(string? name) => ChangeWorkspaces(f => f.OpenAtLaunch = name);

    public void DeleteWorkspace(string name) => ChangeWorkspaces(f => f.Remove(name));

    /// <summary>Show the Repos tab, for someone with nothing cloned yet.</summary>
    public void RequestRepos() => ReposRequested?.Invoke();

    /// <summary>Rebuild the Repos workspace from the checkouts now, keeping its name and place; every other workspace is left alone.</summary>
    public void RegenerateBaselineWorkspace()
    {
        if (TerminalWorkspaceBaseline.Make(RepoCheckouts(), DateTime.UtcNow) is not { } baseline)
        {
            ErrorRaised?.Invoke(NoCheckouts);
            return;
        }
        ChangeWorkspaces(file =>
        {
            file.GenerateFromRepos = null;
            if (file.BaselineIndex is { } index)
            {
                baseline.Name = file.Workspaces[index].Name;
                file.Workspaces[index] = baseline;
            }
            else file.Workspaces.Insert(0, baseline);
        });
        // Rows for folders the new workspace no longer has go; the rest are kept as they are.
        SyncRepos();
    }

    /// <summary>Add checkouts the Repos workspace lacks, keeping every edit to it.</summary>
    public void AddMissingReposToBaseline()
    {
        var checkouts = RepoCheckouts();
        if (checkouts.Count == 0) { ErrorRaised?.Invoke(NoCheckouts); return; }
        ChangeWorkspaces(file =>
        {
            file.GenerateFromRepos = null;
            if (file.Baseline is { } existing) TerminalWorkspaceBaseline.AddMissing(checkouts, existing, DateTime.UtcNow);
            else if (TerminalWorkspaceBaseline.Make(checkouts, DateTime.UtcNow) is { } baseline) file.Workspaces.Insert(0, baseline);
        });
        SyncRepos();
    }

    /// <summary>The workspaces file, read now so hand edits apply without a restart; null with <paramref name="error"/> when it cannot be read.</summary>
    public TerminalWorkspaceFile? LoadWorkspaces(out string? error)
    {
        error = null;
        if (IsDemo) return new TerminalWorkspaceFile();
        try { return TerminalWorkspaceStore.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            error = $"Could not read {TerminalWorkspaceStore.DefaultPath}: {ex.Message}";
            return null;
        }
    }

    private void ChangeWorkspaces(Action<TerminalWorkspaceFile> change)
    {
        if (IsDemo) return;
        var file = LoadWorkspaces(out var error);
        if (file == null) { ErrorRaised?.Invoke(error!); return; }
        change(file);
        try { TerminalWorkspaceStore.Save(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ErrorRaised?.Invoke($"Could not save the workspaces: {ex.Message}"); }
    }

    /// <summary>Open the workspaces file in its editor, or show it in Explorer, writing an empty one first so there is something to edit.</summary>
    public void OpenWorkspacesFile(bool reveal)
    {
        if (IsDemo) return;
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
            ErrorRaised?.Invoke($"Could not open {path}: {ex.Message}");
        }
    }

    /// <summary>The workspace terminal-workspaces.json names to open at launch, or null.</summary>
    public TerminalWorkspace? LaunchWorkspace()
    {
        if (IsDemo) return null;
        try { return TerminalWorkspaceStore.Load().LaunchWorkspace; }
        catch (Exception ex)
        {
            Log.Warning(ex, "Terminal workspaces: could not read {Path}", TerminalWorkspaceStore.DefaultPath);
            return null;
        }
    }

    // ── Reopening at launch ──────────────────────────────────────────────

    /// <summary>
    /// The launch sequence both windows run once they are loaded: the Repos
    /// rows first, so reopened sessions find theirs; then what was open at the
    /// last quit; then the workspace named to open at launch, or with
    /// AgentAutoStart (on by default) one session that does not take the
    /// keyboard. In demo mode, the fictional sessions instead.
    /// </summary>
    public void StartAtLaunch()
    {
        if (IsDemo) { DemoFixtures.Seed(this); return; }
        SyncRepos();
        var restored = RestoreSessions();
        if (LaunchWorkspace() is { } launchWorkspace) OpenWorkspace(launchWorkspace);
        else if (!restored && Settings.AgentAutoStart) OpenDefaultSession(takeFocus: false);
    }

    /// <summary>
    /// At quit: the running sessions, saved so the next launch puts them back.
    /// The person's own sessions are saved by their current folder, Repos rows
    /// by their repository's folder. Nothing is saved with the setting off.
    /// </summary>
    private void SaveRestore()
    {
        if (IsDemo) return;
        var file = LoadWorkspaces(out _);
        if (file == null) return;
        if (!UserPreferences.TerminalRestoreSessions) { file.Restore = null; }
        else
        {
            var restore = new TerminalWorkspace { Name = "Restore" };
            foreach (var name in _sections)
            {
                var running = _sessions.Where(s => s.Section == name && s.IsRunning).ToList();
                if (running.Count == 0) continue;
                restore.Sections.Add(new TerminalWorkspaceSection
                {
                    Name = name,
                    Sessions = running.Select(s => new TerminalWorkspaceSession
                    {
                        Name = s.IsRepoRow ? null : s.CustomName,
                        Label = s.WorkspaceLabel,
                        Command = s.Command,
                        Directory = (s.IsRepoRow ? s.StartDirectory : s.Folder) is { Length: > 0 } dir ? TerminalWorkspaceStore.Abbreviate(dir) : null,
                        Pinned = !s.IsRepoRow && s.Pinned ? true : null,
                        Added = s.AddedId != null ? true : null,
                        Id = s.AddedId,
                        // Only Claude Code and Codex can resume; a shell or a remote wrapper starts fresh.
                        Resume = AgentResume.Command(s.Command) != null ? true : null,
                    }).ToList(),
                });
            }
            file.Restore = restore.Sections.Count > 0 ? restore : null;
        }
        try { TerminalWorkspaceStore.Save(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Terminal workspaces: could not save the sessions to reopen");
        }
    }

    /// <summary>
    /// At launch: put back the sessions open at the last quit. Repos rows are
    /// marked to resume; the person's own sessions come back as rows that wait
    /// to start, the first of them selected. The saved list is kept, so a crash
    /// reopens them again. Returns whether any of the person's own came back.
    /// </summary>
    public bool RestoreSessions()
    {
        if (IsDemo || !UserPreferences.TerminalRestoreSessions) return false;
        var file = LoadWorkspaces(out _);
        if (file?.Restore is not { } restore) return false;
        TerminalSession? first = null;
        foreach (var section in restore.Sections)
        {
            var name = string.IsNullOrWhiteSpace(section.Name) ? DefaultSection : section.Name.Trim();
            foreach (var entry in section.Sessions)
            {
                var key = entry.Directory is { Length: > 0 } ? TerminalWorkspaceBaseline.RowKey(entry) : null;
                if (key != null && _sessions.FirstOrDefault(s => s.RepoFolder == key) is { } repoRow)
                {
                    repoRow.Resume = entry.Resume == true;
                    SessionChanged?.Invoke(repoRow);
                    continue;
                }
                var row = AddWaitingRow(entry, name, repoFolder: null);
                first ??= row;
            }
        }
        RowsChanged?.Invoke();
        if (first == null) return false;
        // Selected, but it starts only once the panel is shown.
        Active = first;
        ActiveChanged?.Invoke(Active);
        if (_visible) Activate(first, false);
        return true;
    }

    /// <summary>The window is closing: save what to reopen, then end every session.</summary>
    public void DisposeAll()
    {
        SaveRestore();
        foreach (var session in _sessions.ToList()) StopProcesses(session);
        _sessions.Clear();
    }

    /// <summary>
    /// Demo mode's fictional sessions: made-up names, folders and output, so a
    /// capture shows the list and a session without running anything.
    /// </summary>
    internal static class DemoFixtures
    {
        public static void Seed(TerminalSessionList list)
        {
            if (list.Sessions.Count > 0) return;
            // Rows read as their repository's folder, as they do under a real clone root.
            list._roots = new[] { Root };
            list.AddSection("Sample Repos");
            list.OpenSession(new TerminalLaunch("codex · sample-app", Shell(), @"C:\Users\demo\FleetMate\repos\sample-app")
            {
                TakeFocus = false,
                AgentCommand = "codex",
                DemoTranscript = Transcript("codex"),
            });
            list.Sessions[^1].CustomName = "Tidy the sample app's README";
            list.OpenSession(new TerminalLaunch("shell", Shell(), @"C:\Users\demo\FleetMate\repos\example-tools")
            {
                TakeFocus = false,
                DemoTranscript = "PS C:\\Users\\demo\\FleetMate\\repos\\example-tools> git status\n"
                    + "On branch main\nYour branch is up to date with 'origin/main'.\n\nnothing to commit, working tree clean\n"
                    + "PS C:\\Users\\demo\\FleetMate\\repos\\example-tools> ",
            });
            list.AddWaitingRow(new TerminalWorkspaceSession
            {
                Label = "example-org/docs-site",
                Directory = @"C:\Users\demo\FleetMate\repos\docs-site",
            }, "Sample Repos", repoFolder: null);
            list.Activate(list.Sessions[0], takeFocus: false);
        }

        private const string Root = @"C:\Users\demo\FleetMate\repos";

        private static TerminalCommand Shell() => new("pwsh.exe", Array.Empty<string>());

        /// <summary>What a fictional pane shows: an agent at its prompt, or a note that nothing runs.</summary>
        public static string Transcript(string title) => title.StartsWith("codex", StringComparison.OrdinalIgnoreCase)
            ? "\x1b[1m>_ Codex\x1b[0m  (demo)\n\n"
              + "\x1b[90mmodel:\x1b[0m sample-model   \x1b[90mdirectory:\x1b[0m ~\\FleetMate\\repos\\sample-app\n\n"
              + "\x1b[36muser\x1b[0m\nTidy the README's install section.\n\n"
              + "\x1b[35mcodex\x1b[0m\nI read README.md and moved the install steps under one heading.\n"
              + "  \x1b[32m+\x1b[0m ## Install\n  \x1b[32m+\x1b[0m Run the installer, then sign in.\n\n"
              + "\x1b[90m▌ Ask Codex to do anything\x1b[0m\n"
            : "Demo mode: fictional data. No shell or agent runs here.\n";
    }
}
