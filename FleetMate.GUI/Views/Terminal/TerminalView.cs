using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Services.Terminal;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Serilog;

namespace FleetMate.GUI.Views.Terminal;

/// <summary>
/// One terminal pane: xterm.js in a WebView2, drawing what its
/// <see cref="TerminalPaneController"/> (in FleetMate.Core, shared with the
/// WinUI app) runs. The page and its scripts are embedded in the app and
/// served from a private virtual host, so nothing loads from the network.
/// </summary>
public sealed class TerminalView : UserControl, IDisposable
{
    private readonly WebView2 _web = new() { DefaultBackgroundColor = System.Drawing.Color.Transparent };

    public TerminalView(TerminalPaneController controller)
    {
        Controller = controller;
        controller.FocusRequested += (_, _) => _web.Focus();
        Content = _web;
        Loaded += async (_, _) => await InitializeAsync();
    }

    public TerminalPaneController Controller { get; }

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
            core.AddWebResourceRequestedFilter($"{TerminalPaneController.Origin}/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnResourceRequested;
            core.WebMessageReceived += OnMessage;
            Controller.AttachPage(json => _web.CoreWebView2?.PostWebMessageAsJson(json));
            core.Navigate(TerminalPaneController.PageUrl(IsDark));
            ModernWpf.ThemeManager.Current.ActualApplicationThemeChanged += OnAppThemeChanged;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Terminal: WebView2 failed to start");
            Content = new TextBlock { Text = $"The terminal needs the WebView2 runtime: {ex.Message}", Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
        }
    }

    internal static bool IsDark => ModernWpf.ThemeManager.Current.ActualApplicationTheme != ModernWpf.ApplicationTheme.Light;

    private void OnAppThemeChanged(object? sender, object args) => Controller.SetTheme(IsDark);

    /// <summary>Serve terminal.html, xterm.js and its stylesheet from the app's embedded resources.</summary>
    private void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var name = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
        var stream = TerminalPaneController.AssetNames.Contains(name)
            ? Assembly.GetExecutingAssembly().GetManifestResourceStream($"FleetMate.GUI.Terminal.{name}")
            : null;
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
        if (!TerminalPaneController.IsOwnUri(e.Source)) return;
        Controller.OnPageMessage(e.WebMessageAsJson);
    }

    public void Dispose()
    {
        ModernWpf.ThemeManager.Current.ActualApplicationThemeChanged -= OnAppThemeChanged;
        Controller.Dispose();
        _web.Dispose();
    }
}
