using FleetMate.Core.Services.SignIn;
using Microsoft.Web.WebView2.Core;

namespace FleetMate.SignIn;

/// <summary>
/// A WebView2 as an <see cref="ISignInBrowser"/>. This one file is compiled
/// into both apps (FleetMate.GUI links it): the WPF app's
/// <c>Microsoft.Web.WebView2.Core</c> and the Windows App SDK's projection of
/// it share their type names and members, so the same source adapts either.
/// </summary>
public class CoreWebView2SignInBrowser : ISignInBrowser
{
    public CoreWebView2SignInBrowser(CoreWebView2 core)
    {
        Core = core;
        core.NavigationStarting += (_, e) =>
        {
            if (NavigationStarting is not { } handler) return;
            var starting = new SignInNavigationStarting(e.Uri);
            handler(starting);
            if (starting.Cancel) e.Cancel = true;
        };
        core.NavigationCompleted += (_, e) => NavigationCompleted?.Invoke(e.IsSuccess, e.WebErrorStatus.ToString());
        core.WebMessageReceived += (_, e) => WebMessageReceived?.Invoke(e.WebMessageAsJson);
    }

    public CoreWebView2 Core { get; }

    public string Source => Core.Source ?? "";

    public async Task<string> ExecuteScriptAsync(string script) => await Core.ExecuteScriptAsync(script);

    public void Navigate(string uri) => Core.Navigate(uri);

    public event Action<SignInNavigationStarting>? NavigationStarting;

    public event Action<bool, string>? NavigationCompleted;

    public event Action<string>? WebMessageReceived;
}
