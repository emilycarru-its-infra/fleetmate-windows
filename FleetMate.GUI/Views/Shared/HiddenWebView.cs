using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// A WebView2 that is never on screen, for headless web sign-in. Its host is a
/// WPF window whose handle is created but which is never shown: no taskbar
/// button, no Alt+Tab entry, nothing to see or click. The browser is attached
/// to that handle through a <see cref="CoreWebView2Controller"/>, which needs a
/// parent window but not a visible one.
///
/// Create and use it on the UI thread.
/// </summary>
internal sealed class HiddenWebView : IDisposable
{
    private readonly Window _host;
    private readonly CoreWebView2Controller _controller;

    public CoreWebView2 Core => _controller.CoreWebView2;

    private HiddenWebView(Window host, CoreWebView2Controller controller)
    {
        _host = host;
        _controller = controller;
    }

    public static async Task<HiddenWebView> CreateAsync()
    {
        var host = new Window
        {
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Left = -32000,
            Top = -32000,
            Width = 1024,
            Height = 768,
        };
        try
        {
            // A handle without Show(): the window exists but never appears.
            var hwnd = new WindowInteropHelper(host).EnsureHandle();
            var environment = await WebViewEnvironments.SignIn();
            var controller = await environment.CreateCoreWebView2ControllerAsync(hwnd);
            // Laid out at a desktop size so Entra serves its normal pages; the
            // parent is hidden, so nothing reaches the screen.
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 1024, 768);
            controller.IsVisible = true;
            controller.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            controller.CoreWebView2.Settings.AreDevToolsEnabled = false;
            controller.CoreWebView2.Settings.IsStatusBarEnabled = false;
            // A page that tries to open a window gets nothing, so no popup can surface.
            controller.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            return new HiddenWebView(host, controller);
        }
        catch
        {
            host.Close();
            throw;
        }
    }

    public void Dispose()
    {
        try { _controller.Close(); } catch { /* already gone */ }
        try { _host.Close(); } catch { /* already gone */ }
    }
}
