using System.IO;
using Microsoft.Web.WebView2.Core;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// The WebView2 environments FleetMate's views share. Each needs its own
/// user-data folder under the user's local app data: left to the default,
/// WebView2 puts it beside the executable, which a normal user cannot write
/// to, and the view fails to start with access denied. A folder can be used
/// with only one set of options per process, so the hidden sign-in browser,
/// which passes the OS account through, keeps a folder apart from plain
/// content views.
/// </summary>
internal static class WebViewEnvironments
{
    private static readonly Lazy<Task<CoreWebView2Environment>> ContentEnvironment = new(() =>
        CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: Folder("WebView2-Content")));

    private static readonly Lazy<Task<CoreWebView2Environment>> SignInEnvironment = new(() =>
        CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: Folder("WebView2"),
            options: new CoreWebView2EnvironmentOptions
            {
                // Entra pages in this profile sign in with the Windows account's
                // primary refresh token, the same SSO the broker (WAM) uses.
                AllowSingleSignOnUsingOSPrimaryAccount = true,
                // The sign-in browser is never on screen. Without these, Chromium
                // treats it as occluded and throttles its timers, and the scripts
                // that answer Entra's pages run too slowly to finish in time.
                AdditionalBrowserArguments =
                    "--disable-features=CalculateNativeWinOcclusion " +
                    "--disable-background-timer-throttling " +
                    "--disable-backgrounding-occluded-windows " +
                    "--disable-renderer-backgrounding",
            }));

    /// <summary>For views that only render content: Markdown, descriptions, skills.</summary>
    public static Task<CoreWebView2Environment> Content() => ContentEnvironment.Value;

    /// <summary>For the hidden sign-in browser, which reuses the Windows account.</summary>
    public static Task<CoreWebView2Environment> SignIn() => SignInEnvironment.Value;

    private static string Folder(string name)
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FleetMate", name);
        Directory.CreateDirectory(folder);
        return folder;
    }
}
