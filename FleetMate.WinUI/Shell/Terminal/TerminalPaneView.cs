using FleetMate.Core.Services.Terminal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Serilog;

namespace FleetMate.WinUI.Shell.Terminal;

/// <summary>
/// One terminal pane: xterm.js in a WebView2, drawing what its
/// <see cref="TerminalPaneController"/> (FleetMate.Core, shared with
/// FleetMate.GUI) runs. The page and its scripts are files beside the app,
/// mapped to a private virtual host, so nothing loads from the network.
///
/// Hardened: navigation anywhere but the page is cancelled, new windows are
/// refused, messages are read only from the page's own origin, the only
/// permission granted is clipboard read for Ctrl+V, and DevTools, context
/// menus, zoom, autofill, host objects and the browser's own keys are off.
/// </summary>
public sealed class TerminalPaneView : UserControl, IDisposable
{
    private readonly WebView2 _web = new() { DefaultBackgroundColor = Microsoft.UI.Colors.Transparent };
    private bool _disposed;

    public TerminalPaneView(TerminalPaneController controller)
    {
        Controller = controller;
        controller.FocusRequested += (_, _) => _web.Focus(FocusState.Programmatic);
        AutomationProperties.SetName(_web, "Agent Terminal");
        AutomationProperties.SetAutomationId(_web, "TerminalWebView");
        Content = _web;
        Loaded += async (_, _) => await InitializeAsync();
    }

    public TerminalPaneController Controller { get; }

    /// <summary>
    /// One WebView2 environment for every terminal, with its data in the
    /// user's profile: unpackaged, the default folder beside the exe is not
    /// writable by a normal user. Kept apart from FleetMate.GUI's folder, so
    /// the two apps never contend for one browser process's options.
    /// </summary>
    private static readonly Lazy<Task<CoreWebView2Environment>> SharedEnvironment = new(() =>
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FleetMate", "WebView2-Terminal-WinUI");
        Directory.CreateDirectory(folder);
        return CoreWebView2Environment.CreateWithOptionsAsync(null, folder, new CoreWebView2EnvironmentOptions()).AsTask();
    });

    private static string AssetFolder => Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal");

    private async Task InitializeAsync()
    {
        if (_web.CoreWebView2 != null || _disposed) return;
        try
        {
            await _web.EnsureCoreWebView2Async(await SharedEnvironment.Value);
            if (_disposed) return;
            var core = _web.CoreWebView2!;
            var settings = core.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.IsPinchZoomEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.AreHostObjectsAllowed = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsSwipeNavigationEnabled = false;
            settings.AreDefaultScriptDialogsEnabled = false;
            settings.IsBuiltInErrorPageEnabled = false;
            // Reload, find, print and the rest belong to a browser, not a terminal.
            settings.AreBrowserAcceleratorKeysEnabled = false;
#if DEBUG
            settings.AreDevToolsEnabled = true;
#else
            settings.AreDevToolsEnabled = false;
#endif
            core.SetVirtualHostNameToFolderMapping(TerminalPaneController.Host, AssetFolder, CoreWebView2HostResourceAccessKind.Deny);
            core.NavigationStarting += (_, e) => { if (!TerminalPaneController.IsOwnUri(e.Uri)) e.Cancel = true; };
            core.FrameNavigationStarting += (_, e) => e.Cancel = true;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State =
                TerminalPaneController.AllowsPermission(e.Uri, e.PermissionKind.ToString())
                    ? CoreWebView2PermissionState.Allow
                    : CoreWebView2PermissionState.Deny;
            core.WebMessageReceived += (_, e) =>
            {
                if (TerminalPaneController.IsOwnUri(e.Source)) Controller.OnPageMessage(e.WebMessageAsJson);
            };
            Controller.AttachPage(json =>
            {
                if (!_disposed) _web.CoreWebView2?.PostWebMessageAsJson(json);
            });
            core.Navigate(TerminalPaneController.PageUrl(ActualTheme == ElementTheme.Dark));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Terminal: WebView2 failed to start");
            Content = new TextBlock
            {
                Text = $"The terminal needs the WebView2 runtime: {ex.Message}",
                Margin = new Thickness(12),
                TextWrapping = TextWrapping.Wrap,
            };
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Controller.Dispose();
        _web.Close();
    }
}
