using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Services.Terminal;
using FleetMate.Core.Shared;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Serilog;

namespace FleetMate.GUI.Views.Terminal;

/// <summary>What one terminal pane runs, and where.</summary>
public sealed record TerminalLaunch(string Title, TerminalCommand Command, string? WorkingDirectory, RepoLocation? Repo = null)
{
    /// <summary>Take keyboard focus once ready. A session opened by the person does; one auto-started at launch does not.</summary>
    public bool TakeFocus { get; init; } = true;
}

/// <summary>
/// One terminal pane: xterm.js in a WebView2, wired to a process behind a
/// Windows pseudo console. The page and its scripts are embedded in the app
/// and served from a private virtual host, so nothing loads from the network.
/// </summary>
public sealed class TerminalView : UserControl, IDisposable
{
    private const string Host = "terminal.fleetmate.invalid";
    private readonly WebView2 _web = new() { DefaultBackgroundColor = System.Drawing.Color.Transparent };
    private readonly TerminalLaunch _launch;
    private readonly StringBuilder _pending = new();
    private readonly object _gate = new();
    private PseudoConsoleSession? _session;
    private bool _ready;
    private bool _started;
    private short _cols = 120, _rows = 30;
    private readonly TerminalSignalScanner _scanner = new();
    private bool _focusWhenReady;
    private double _fontSize = TerminalFontSize.Base;
    /// <summary>Names this session's own brief, removed when the pane closes.</summary>
    private readonly string _sessionId = Guid.NewGuid().ToString("N");

    public event EventHandler? ToggleRequested;
    public event EventHandler? Exited;
    /// <summary>The title, directory or activity changed; the session list redraws the row.</summary>
    public event EventHandler? StateChanged;
    /// <summary>A panel key binding was pressed inside this terminal.</summary>
    public event Action<TerminalView, TerminalAction, int>? KeyAction;

    /// <summary>The command's label, until the program sets a title with OSC 0 or 2.</summary>
    public string Title { get; private set; }
    public string? Directory { get; private set; }
    /// <summary>"osc" once the shell has reported its directory itself; "poll" while it is read from the process.</summary>
    public string DirectorySource { get; private set; } = "poll";
    public SessionActivity Activity { get; } = new();
    /// <summary>Whether this pane is on screen now; a bell from a hidden pane asks for attention.</summary>
    public bool IsShown { get; set; }
    public int ProcessId => _session?.ProcessId ?? 0;

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
    internal static string Paste(string text) => "\x1b[200~" + AgentContextSanitizer.PastePayload(text) + "\x1b[201~";

    public TerminalView(TerminalLaunch launch)
    {
        _launch = launch;
        Title = launch.Title;
        Directory = launch.WorkingDirectory;
        _focusWhenReady = launch.TakeFocus;
        Content = _web;
        Loaded += async (_, _) => await InitializeAsync();
    }

    public void FocusTerminal()
    {
        if (!_ready) { _focusWhenReady = true; return; }
        _web.Focus();
        Post(new { type = "focus" });
    }

    public void Clear() => Post(new { type = "clear" });

    /// <summary>
    /// Draw at <paramref name="size"/>. xterm.js re-lays out the grid and the
    /// program is told the new size; nothing restarts.
    /// </summary>
    public void SetFontSize(double size)
    {
        if (size == _fontSize) return;
        _fontSize = size;
        if (_ready) Post(new { type = "font", size });
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

    /// <summary>
    /// One WebView2 environment for every terminal, with its data in the
    /// user's profile. Left to its default, WebView2 puts the folder beside
    /// the host executable, which a normal user cannot write to, and the
    /// terminal fails with access denied. It is kept apart from the sign-in
    /// window's folder because that one is created with different options.
    /// </summary>
    private static readonly Lazy<Task<CoreWebView2Environment>> SharedEnvironment = new(() =>
    {
        var folder = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FleetMate", "WebView2-Terminal");
        System.IO.Directory.CreateDirectory(folder);
        return CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: folder);
    });

    private async Task InitializeAsync()
    {
        if (_web.CoreWebView2 != null) return;
        try
        {
            await _web.EnsureCoreWebView2Async(await SharedEnvironment.Value);
            var core = _web.CoreWebView2!;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.AddWebResourceRequestedFilter($"https://{Host}/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnResourceRequested;
            core.WebMessageReceived += OnMessage;
            var theme = ModernWpf.ThemeManager.Current.ActualApplicationTheme == ModernWpf.ApplicationTheme.Light ? "light" : "dark";
            core.Navigate($"https://{Host}/terminal.html?theme={theme}");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Terminal: WebView2 failed to start");
            Content = new TextBlock { Text = $"The terminal needs the WebView2 runtime: {ex.Message}", Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
        }
    }

    /// <summary>Serve terminal.html, xterm.js and its stylesheet from the app's embedded resources.</summary>
    private void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var name = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"FleetMate.GUI.Terminal.{name}");
        if (stream == null)
        {
            e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
            return;
        }
        var type = name.EndsWith(".js") ? "text/javascript" : name.EndsWith(".css") ? "text/css" : "text/html";
        e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {type}; charset=utf-8");
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var m = doc.RootElement;
        switch (m.GetProperty("type").GetString())
        {
            case "ready":
                _ready = true;
                Post(new { type = "config", chords = TerminalKeyBindings.Chords(), fontSize = _fontSize });
                FlushPending();
                if (_focusWhenReady) FocusTerminal();
                break;
            case "key":
                var action = TerminalKeyBindings.Map(m.GetProperty("code").GetString() ?? "",
                    m.GetProperty("ctrl").GetBoolean(), m.GetProperty("shift").GetBoolean(),
                    m.GetProperty("alt").GetBoolean(), out var index);
                if (action != TerminalAction.None) KeyAction?.Invoke(this, action, index);
                break;
            case "resize":
                _cols = (short)Math.Max(1, m.GetProperty("cols").GetInt32());
                _rows = (short)Math.Max(1, m.GetProperty("rows").GetInt32());
                if (!_started) _ = StartAsync();
                else _session?.Resize(_cols, _rows);
                break;
            case "input":
                _session?.Write(m.GetProperty("data").GetString() ?? "");
                break;
            case "toggle":
                ToggleRequested?.Invoke(this, EventArgs.Empty);
                break;
            case "modes":
                AcceptsBracketedPaste = m.GetProperty("bracketedPaste").GetBoolean();
                break;
        }
    }

    /// <summary>Start once the page knows its size, so the program starts at the right width.</summary>
    private async Task StartAsync()
    {
        _started = true;
        var workingDirectory = _launch.WorkingDirectory;
        if (_launch.Repo is { NeedsClone: true } repo)
        {
            Write($"\x1b[90mCloning {repo.CloneUrl} into {repo.Path}...\x1b[0m\r\n");
            var (ok, output) = await CloneAsync(repo);
            Write(output.Replace("\n", "\r\n"));
            if (!ok) { Write("\r\n\x1b[33mClone failed; starting in the repos folder instead.\x1b[0m\r\n"); workingDirectory = RepoLocator.DefaultRoot; }
        }
        var app = (App)Application.Current;
        if (workingDirectory != null && !System.IO.Directory.Exists(workingDirectory))
        {
            Write($"\x1b[33m{workingDirectory} does not exist; starting in the repos folder instead.\x1b[0m\r\n");
            workingDirectory = AgentSessions.WorkspaceDirectory();
        }
        // A session nothing pointed anywhere opens where the person is
        // working, never the bare home folder.
        workingDirectory ??= app.Agent.StartDirectory();
        if (Directory == null)
        {
            Directory = workingDirectory;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        try
        {
            // The agent is handed its brief, with where it opened at the top.
            var place = app.Agent.Whereabouts();
            var prepared = await Task.Run(() => app.Agent.Prepare(_sessionId, _launch.Command, workingDirectory, place));
            var env = TerminalEnvironment.Build(Environment.GetEnvironmentVariables(), app.Context.Path,
                TerminalEnvironment.FindCliDirectory(AppContext.BaseDirectory), prepared.Environment);
            _session = new PseudoConsoleSession();
            _session.Output += Write;
            _session.Exited += code =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    Activity.Exit();
                    Post(new { type = "exit", code });
                    Exited?.Invoke(this, EventArgs.Empty);
                    StateChanged?.Invoke(this, EventArgs.Empty);
                });
            };
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
    /// session active.
    /// </summary>
    private void Write(string text)
    {
        List<TerminalSignal> signals;
        lock (_gate) signals = _scanner.Scan(text);
        Dispatcher.BeginInvoke(() =>
        {
            Activity.Output(DateTime.UtcNow);
            foreach (var signal in signals)
            {
                switch (signal)
                {
                    case TitleSignal t when TerminalTitle.IsMeaningful(t.Title): Title = t.Title.Trim(); break;
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
        Dispatcher.BeginInvoke(() => Post(new { type = "output", data = text }));
    }

    private void FlushPending()
    {
        string text;
        lock (_gate) { text = _pending.ToString(); _pending.Clear(); }
        if (text.Length > 0) Post(new { type = "output", data = text });
    }

    private void Post(object message)
    {
        if (_web.CoreWebView2 == null) return;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    public void Dispose()
    {
        _session?.Dispose();
        (Application.Current as App)?.Agent.Release(_sessionId);
        _web.Dispose();
    }
}
