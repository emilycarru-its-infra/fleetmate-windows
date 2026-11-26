using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FleetMate.Core.Shared;
using Serilog;

namespace FleetMate.Core.Services.Terminal;

/// <summary>
/// One terminal pane, everything but the WebView2 that draws it: the process
/// behind a Windows pseudo console, the terminal page's messages in and out,
/// and what the session list reads (title, directory, activity, whether an
/// agent takes a paste). Both desktop apps host one in their own WebView2
/// control and hand it the page's messages; the page and xterm.js are the
/// same files in both.
///
/// Every member is called on the UI thread, except that process output
/// arrives on a reader thread and is marshalled through the
/// <c>post</c> callback the pane is built with.
/// </summary>
public sealed class TerminalPaneController : IDisposable
{
    /// <summary>The private host the page is served from; nothing else may load.</summary>
    public const string Host = "terminal.fleetmate.invalid";

    /// <summary>The page's origin, the only one whose messages are read.</summary>
    public const string Origin = "https://" + Host;

    /// <summary>The page, starting in the app's theme.</summary>
    public static string PageUrl(bool dark) => $"{Origin}/terminal.html?theme={(dark ? "dark" : "light")}";

    /// <summary>The files the page loads, and nothing else.</summary>
    public static readonly IReadOnlyList<string> AssetNames = new[] { "terminal.html", "xterm.js", "xterm.css", "addon-fit.js" };

    /// <summary>Whether <paramref name="uri"/> is the terminal page or one of its files.</summary>
    public static bool IsOwnUri(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        && parsed.Scheme == Uri.UriSchemeHttps
        && string.Equals(parsed.Host, Host, StringComparison.OrdinalIgnoreCase)
        && parsed.IsDefaultPort;

    /// <summary>
    /// Whether the page may have a browser permission: only reading the
    /// clipboard, for Ctrl+V, and only from the terminal page itself.
    /// Everything else (camera, location, notifications...) is refused.
    /// </summary>
    public static bool AllowsPermission(string? uri, string permission) =>
        permission == "ClipboardRead" && IsOwnUri(uri);

    /// <summary>The longest text the page may hand over as one drop or one input; anything longer is ignored.</summary>
    internal const int MaxMessageText = 1 << 20;

    private readonly TerminalLaunch _launch;
    private readonly AgentSessionHost? _agent;
    private readonly Action<Action> _post;
    private readonly Func<bool> _isDark;
    private readonly StringBuilder _pending = new();
    private readonly object _gate = new();
    private readonly TerminalSignalScanner _scanner = new();
    private Action<string>? _send;
    private PseudoConsoleSession? _session;
    private bool _ready;
    private bool _started;
    private bool _disposed;
    private short _cols = 120, _rows = 30;
    private bool _focusWhenReady;
    private double _fontSize = TerminalFontSize.Base;
    /// <summary>Names this session's own brief, removed when the pane closes.</summary>
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private DateTime _metadataModified;

    /// <param name="launch">What runs, and where.</param>
    /// <param name="agent">The brief, context and start directory; null in demo mode, where no process ever starts.</param>
    /// <param name="post">Run an action on the UI thread, from any thread.</param>
    /// <param name="isDark">Whether the app draws dark now.</param>
    public TerminalPaneController(TerminalLaunch launch, AgentSessionHost? agent, Action<Action> post, Func<bool> isDark)
    {
        _launch = launch;
        _agent = agent;
        _post = post;
        _isDark = isDark;
        Title = launch.Title;
        Directory = launch.WorkingDirectory;
        _focusWhenReady = launch.TakeFocus;
    }

    public TerminalLaunch Launch => _launch;

    public event EventHandler? ToggleRequested;
    public event EventHandler? Exited;
    /// <summary>The title, directory or activity changed; the session list redraws the row.</summary>
    public event EventHandler? StateChanged;
    /// <summary>A panel key binding was pressed inside this terminal.</summary>
    public event Action<TerminalPaneController, TerminalAction, int>? KeyAction;
    /// <summary>Text was dropped on this terminal; it reaches the program only through Send to Agent.</summary>
    public event Action<TerminalPaneController, string>? TextDropped;
    /// <summary>The host control should take keyboard focus now; the page is told to focus xterm.js.</summary>
    public event EventHandler? FocusRequested;

    /// <summary>The command's label, until the program sets a title with OSC 0 or 2.</summary>
    public string Title { get; private set; }
    /// <summary>The last title the program set (OSC 0 or 2), as sent; null until it sets one. Read for the session row's task and working glyph.</summary>
    public string? ProgramTitle { get; private set; }
    public string? Directory { get; private set; }
    /// <summary>"osc" once the shell has reported its directory itself; "poll" while it is read from the process.</summary>
    public string DirectorySource { get; private set; } = "poll";
    public SessionActivity Activity { get; } = new();
    /// <summary>Whether this pane is on screen now; a bell from a hidden pane asks for attention.</summary>
    public bool IsShown { get; set; }
    public int ProcessId => _session?.ProcessId ?? 0;
    /// <summary>The page has loaded and said so.</summary>
    public bool IsReady => _ready;
    public double FontSize => _fontSize;

    /// <summary>
    /// Whether the program in the terminal takes pasted text as one block
    /// (Claude Code and Codex both ask for it once they are drawn), as the
    /// page last reported.
    /// </summary>
    public bool AcceptsBracketedPaste { get; private set; }

    /// <summary>
    /// Whether an agent CLI, not a bare shell, is running in this pane now:
    /// the pane's process or one of its descendants is one. Reads a process
    /// snapshot, so it is not free; call it on a click, not on a timer.
    /// </summary>
    public bool AgentIsRunning => Activity.State(DateTime.UtcNow) != ActivityState.Exited && AgentProcessTree.IsAgentRunning(ProcessId);

    /// <summary>The agent's session file changed since last read: its title and section, or null when it has not.</summary>
    public AgentSessionMetadata? ReadMetadataChange()
    {
        if (_agent == null) return null;
        var path = AgentSessionMetadata.FilePath(_sessionId);
        try
        {
            var modified = File.GetLastWriteTimeUtc(path);
            if (modified == _metadataModified) return null;
            var metadata = AgentSessionMetadata.Read(path);
            if (metadata != null) _metadataModified = modified;
            return metadata;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Connect the page: <paramref name="send"/> posts one JSON message to it.
    /// Call once the control has navigated; messages before then are dropped,
    /// and output is held until the page says it is ready.
    /// </summary>
    public void AttachPage(Action<string> send) => _send = send;

    /// <summary>
    /// Type <paramref name="text"/> into the program's input as one bracketed
    /// paste, never followed by Return. Control characters and escape
    /// sequences are stripped first, so the text cannot end the paste early,
    /// submit it or drive the terminal. Refuses (returns false) when the
    /// program has not asked for bracketed paste, so nothing ever lands in a
    /// bare shell line by line.
    /// </summary>
    public bool PasteText(string text)
    {
        if (_session == null || !AcceptsBracketedPaste) return false;
        _session.Write(Paste(text));
        return true;
    }

    /// <summary>The bracketed paste written for <paramref name="text"/>.</summary>
    public static string Paste(string text) => "\x1b[200~" + AgentContextSanitizer.PastePayload(text) + "\x1b[201~";

    /// <summary>Give the terminal the keyboard, now or as soon as the page is ready.</summary>
    public void RequestFocus()
    {
        if (!_ready) { _focusWhenReady = true; return; }
        FocusRequested?.Invoke(this, EventArgs.Empty);
        Send(new { type = "focus" });
    }

    public void Clear() => Send(new { type = "clear" });

    public void ScrollToTop() { if (_ready) Send(new { type = "scroll", to = "top" }); }

    public void ScrollToBottom() { if (_ready) Send(new { type = "scroll", to = "bottom" }); }

    /// <summary>Tell the page a row is being dragged, so it offers to take the drop.</summary>
    public void SetAgentDragActive(bool active)
    {
        if (_ready) Send(new { type = "agentDrag", active });
    }

    /// <summary>
    /// Follow the app between light and dark: the page redraws in the new
    /// colours, answers colour queries with them, and tells a program that
    /// asked for colour-scheme reports.
    /// </summary>
    public void SetTheme(bool dark) => Send(new { type = "theme", dark });

    /// <summary>
    /// Draw at <paramref name="size"/>. xterm.js re-lays out the grid and the
    /// program is told the new size; nothing restarts.
    /// </summary>
    public void SetFontSize(double size)
    {
        if (size == _fontSize) return;
        _fontSize = size;
        if (_ready) Send(new { type = "font", size });
    }

    /// <summary>
    /// Read the child's directory, for shells that never report it with OSC 7
    /// or OSC 9;9. Once a shell has reported it, its reports win.
    /// </summary>
    public void PollDirectory()
    {
        if (DirectorySource == "osc" || ProcessId == 0) return;
        var dir = ProcessDirectory.TryGet(ProcessId);
        if (dir != null && !string.Equals(dir, Directory, StringComparison.OrdinalIgnoreCase))
        {
            Directory = dir;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ── The page's messages ──────────────────────────────────────────────

    /// <summary>
    /// One message from the page, as its JSON. The host checks it came from
    /// <see cref="Origin"/>; this checks its shape, and anything malformed,
    /// unknown or oversized is ignored rather than trusted.
    /// </summary>
    public void OnPageMessage(string json)
    {
        if (_disposed || string.IsNullOrEmpty(json) || json.Length > MaxMessageText * 2) return;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return; }
        using (doc)
        {
            var m = doc.RootElement;
            if (m.ValueKind != JsonValueKind.Object || Text(m, "type") is not { } type) return;
            switch (type)
            {
                case "ready":
                    _ready = true;
                    Send(new { type = "config", chords = TerminalKeyBindings.Chords(), fontSize = _fontSize });
                    FlushPending();
                    if (_focusWhenReady) { _focusWhenReady = false; RequestFocus(); }
                    break;
                case "key":
                    if (Text(m, "code") is not { } code) return;
                    var action = TerminalKeyBindings.Map(code, Flag(m, "ctrl"), Flag(m, "shift"), Flag(m, "alt"), out var index);
                    if (action != TerminalAction.None) KeyAction?.Invoke(this, action, index);
                    break;
                case "resize":
                    if (Number(m, "cols") is not { } cols || Number(m, "rows") is not { } rows) return;
                    _cols = (short)Math.Clamp(cols, 1, 2000);
                    _rows = (short)Math.Clamp(rows, 1, 1000);
                    if (!_started) _ = StartAsync();
                    else _session?.Resize(_cols, _rows);
                    break;
                case "input":
                    if (Text(m, "data") is { Length: > 0 and <= MaxMessageText } data) _session?.Write(data);
                    break;
                case "toggle":
                    ToggleRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case "modes":
                    AcceptsBracketedPaste = Flag(m, "bracketedPaste");
                    break;
                case "drop":
                    if (Text(m, "text") is { Length: > 0 and <= MaxMessageText } text) TextDropped?.Invoke(this, text);
                    break;
            }
        }
    }

    private static string? Text(JsonElement m, string name) =>
        m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Flag(JsonElement m, string name) =>
        m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int? Number(JsonElement m, string name) =>
        m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    // ── The process ──────────────────────────────────────────────────────

    /// <summary>Start once the page knows its size, so the program starts at the right width.</summary>
    private async Task StartAsync()
    {
        _started = true;
        if (_agent == null)
        {
            // Demo mode: fictional output, and never a process.
            Write((_launch.DemoTranscript ?? "Demo mode: no program runs here.\r\n").Replace("\r\n", "\n").Replace("\n", "\r\n"));
            return;
        }

        var workingDirectory = _launch.WorkingDirectory;
        if (_launch.Repo is { NeedsClone: true } repo)
        {
            Write($"\x1b[90mCloning {repo.CloneUrl} into {repo.Path}...\x1b[0m\r\n");
            var (ok, output) = await CloneAsync(repo);
            Write(output.Replace("\n", "\r\n"));
            if (!ok) { Write("\r\n\x1b[33mClone failed; starting in the repos folder instead.\x1b[0m\r\n"); workingDirectory = RepoLocator.DefaultRoot; }
        }
        if (workingDirectory != null && !System.IO.Directory.Exists(workingDirectory))
        {
            Write($"\x1b[33m{workingDirectory} does not exist; starting in the repos folder instead.\x1b[0m\r\n");
            workingDirectory = AgentSessionHost.WorkspaceDirectory();
        }
        // A session nothing pointed anywhere opens where the person is
        // working, never the bare home folder.
        workingDirectory ??= _agent.StartDirectory();
        if (Directory == null)
        {
            Directory = workingDirectory;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        try
        {
            // The agent is handed its brief, with where it opened at the top.
            var place = _agent.Whereabouts();
            var agent = _agent;
            var prepared = await Task.Run(() => agent.Prepare(_sessionId, _launch.Command, workingDirectory, place));
            if (_disposed) return;
            var env = TerminalEnvironment.Build(Environment.GetEnvironmentVariables(), agent.Context.Path,
                TerminalEnvironment.FindCliDirectory(AppContext.BaseDirectory), prepared.Environment);
            env["COLORFGBG"] = TerminalEnvironment.ColorFgBg(_isDark());
            _session = new PseudoConsoleSession();
            _session.Output += Write;
            _session.Exited += code => _post(() =>
            {
                Activity.Exit();
                Send(new { type = "exit", code });
                Exited?.Invoke(this, EventArgs.Empty);
                StateChanged?.Invoke(this, EventArgs.Empty);
            });
            _session.Start(prepared.Command.CommandLine, workingDirectory, env, _cols, _rows);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Terminal: could not start {Command}", _launch.Command.CommandLine);
            Write($"\x1b[31mCould not start {_launch.Command.CommandLine}: {ex.Message}\x1b[0m\r\n");
        }
    }

    private static async Task<(bool Ok, string Output)> CloneAsync(RepoLocation repo)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(repo.Path)!);
            var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, UseShellExecute = false };
            psi.ArgumentList.Add("clone");
            psi.ArgumentList.Add(repo.CloneUrl!);
            psi.ArgumentList.Add(repo.Path);
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode == 0, await stdout + await stderr);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Output can arrive before the page is ready; hold it until then. Each
    /// chunk is also read for titles, directories and bells, and marks the
    /// session active. Any thread.
    /// </summary>
    private void Write(string text)
    {
        List<TerminalSignal> signals;
        lock (_gate) signals = _scanner.Scan(text);
        _post(() =>
        {
            Activity.Output(DateTime.UtcNow);
            foreach (var signal in signals)
            {
                switch (signal)
                {
                    case TitleSignal t:
                        ProgramTitle = t.Title;
                        if (TerminalTitle.IsMeaningful(t.Title)) Title = t.Title.Trim();
                        break;
                    case DirectorySignal d: Directory = d.Path; DirectorySource = "osc"; break;
                    case BellSignal: Activity.Bell(IsShown); break;
                }
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
        });

        lock (_gate)
        {
            if (!_ready) { _pending.Append(text); return; }
        }
        _post(() => Send(new { type = "output", data = text }));
    }

    private void FlushPending()
    {
        string text;
        lock (_gate) { text = _pending.ToString(); _pending.Clear(); }
        if (text.Length > 0) Send(new { type = "output", data = text });
    }

    private void Send(object message)
    {
        if (_disposed || _send == null) return;
        _send(JsonSerializer.Serialize(message));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session?.Dispose();
        _agent?.Release(_sessionId);
        _send = null;
    }
}
