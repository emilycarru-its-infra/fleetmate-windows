using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Services.Terminal;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Serilog;

namespace FleetMate.GUI.Views.Terminal;

/// <summary>What one terminal pane runs, and where.</summary>
public sealed record TerminalLaunch(string Title, TerminalCommand Command, string? WorkingDirectory, RepoLocation? Repo = null);

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

    public event EventHandler? ToggleRequested;
    public event EventHandler? Exited;

    public string Title => _launch.Title;

    public TerminalView(TerminalLaunch launch)
    {
        _launch = launch;
        Content = _web;
        Loaded += async (_, _) => await InitializeAsync();
    }

    public void FocusTerminal()
    {
        _web.Focus();
        Post(new { type = "focus" });
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
                FlushPending();
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
        if (workingDirectory != null && !Directory.Exists(workingDirectory))
        {
            Write($"\x1b[33m{workingDirectory} does not exist; starting in your home folder.\x1b[0m\r\n");
            workingDirectory = null;
        }

        try
        {
            var app = (App)Application.Current;
            var env = TerminalEnvironment.Build(Environment.GetEnvironmentVariables(), app.Context.Path,
                TerminalEnvironment.FindCliDirectory(AppContext.BaseDirectory));
            _session = new PseudoConsoleSession();
            _session.Output += Write;
            _session.Exited += code =>
            {
                Dispatcher.BeginInvoke(() => { Post(new { type = "exit", code }); Exited?.Invoke(this, EventArgs.Empty); });
            };
            _session.Start(_launch.Command.CommandLine,
                workingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), env, _cols, _rows);
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
            Directory.CreateDirectory(Path.GetDirectoryName(repo.Path)!);
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

    /// <summary>Output can arrive before the page is ready; hold it until then.</summary>
    private void Write(string text)
    {
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
        _web.Dispose();
    }
}
