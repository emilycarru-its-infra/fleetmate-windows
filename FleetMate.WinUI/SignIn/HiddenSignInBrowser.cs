using FleetMate.Core.Services;
using FleetMate.Core.Services.SignIn;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using WinRT.Interop;

namespace FleetMate.SignIn;

/// <summary>
/// A WebView2 that is never on screen, for headless web sign-in in the WinUI
/// app. Its parent is a WinUI window that is created but never activated: it
/// has a handle, and no taskbar button, Alt+Tab entry or pixels. WebAuthn is
/// refused in every page, so no page can raise the system passkey dialog.
/// Create and use it on the UI thread.
/// </summary>
internal sealed class HiddenSignInBrowser : CoreWebView2SignInBrowser, ISignInHost
{
    private readonly Window _host;
    private readonly CoreWebView2Controller _controller;

    private HiddenSignInBrowser(Window host, CoreWebView2Controller controller) : base(controller.CoreWebView2)
    {
        _host = host;
        _controller = controller;
    }

    public static async Task<HiddenSignInBrowser> CreateAsync()
    {
        // Never activated, so never shown; the handle is all the browser needs.
        var host = new Window { Title = "" };
        try
        {
            var hwnd = WindowNative.GetWindowHandle(host);
            var environment = await SignInEnvironment.GetAsync();
            var controller = await environment.CreateCoreWebView2ControllerAsync(
                CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)hwnd));
            // Laid out at a desktop size so Entra serves its normal pages; the
            // parent is hidden, so nothing reaches the screen.
            controller.Bounds = new Windows.Foundation.Rect(0, 0, 1024, 768);
            controller.IsVisible = true;
            var core = controller.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            // A page that tries to open a window gets nothing, so no popup can surface.
            core.NewWindowRequested += (_, e) => e.Handled = true;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(EntraWebSignIn.WebAuthnBlockScript);
            return new HiddenSignInBrowser(host, controller);
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

/// <summary>
/// The WebView2 environment the WinUI app's sign-ins share. Its user-data
/// folder is the WinUI app's own, beside the WPF app's: one folder takes one
/// set of options per browser process, and the two apps run side by side.
/// </summary>
internal static class SignInEnvironment
{
    private static Task<CoreWebView2Environment>? _environment;

    public static Task<CoreWebView2Environment> GetAsync() => _environment ??= CreateAsync();

    private static async Task<CoreWebView2Environment> CreateAsync()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FleetMate", "WebView2-WinUI");
        Directory.CreateDirectory(folder);
        var options = new CoreWebView2EnvironmentOptions
        {
            // Entra pages sign in with the Windows account's primary refresh
            // token, the same SSO the broker uses.
            AllowSingleSignOnUsingOSPrimaryAccount = true,
            // The sign-in browser is never on screen; without these Chromium
            // throttles its timers and Entra's pages answer too slowly.
            AdditionalBrowserArguments =
                "--disable-features=CalculateNativeWinOcclusion " +
                "--disable-background-timer-throttling " +
                "--disable-backgrounding-occluded-windows " +
                "--disable-renderer-backgrounding",
        };
        return await CoreWebView2Environment.CreateWithOptionsAsync("", folder, options);
    }
}
