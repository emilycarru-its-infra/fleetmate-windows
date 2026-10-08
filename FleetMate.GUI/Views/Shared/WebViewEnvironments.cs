using System.IO;
using Microsoft.Web.WebView2.Core;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// The WebView2 environments FleetMate's views share. Each needs its own
/// user-data folder under the user's local app data: left to the default,
/// WebView2 puts it beside the executable, which a normal user cannot write
/// to, and the view fails to start with access denied. A folder can be used
/// with only one set of options per process, so the sign-in windows, which
/// pass the OS account through, keep a folder apart from plain content views.
/// </summary>
internal static class WebViewEnvironments
{
    private static readonly Lazy<Task<CoreWebView2Environment>> ContentEnvironment = new(() =>
        CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: Folder("WebView2-Content")));

    private static readonly Lazy<Task<CoreWebView2Environment>> SignInEnvironment = new(() =>
        CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: Folder("WebView2"),
            options: new CoreWebView2EnvironmentOptions { AllowSingleSignOnUsingOSPrimaryAccount = true }));

    /// <summary>For views that only render content: Markdown, descriptions, skills.</summary>
    public static Task<CoreWebView2Environment> Content() => ContentEnvironment.Value;

    /// <summary>For the sign-in windows, which reuse the Windows account.</summary>
    public static Task<CoreWebView2Environment> SignIn() => SignInEnvironment.Value;

    private static string Folder(string name)
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FleetMate", name);
        Directory.CreateDirectory(folder);
        return folder;
    }
}
