namespace FleetMate.Core.Services.SignIn;

/// <summary>
/// The parts of a WebView2 the web sign-ins drive, with no UI framework
/// behind it. The WPF and WinUI apps each adapt their own WebView2 to it
/// (CoreWebView2SignInBrowser), so the page handling that walks Entra and
/// TeamDynamix is written once, here. Use it on the UI thread that owns the browser.
/// </summary>
public interface ISignInBrowser
{
    /// <summary>The address the browser is showing.</summary>
    string Source { get; }

    /// <summary>Run <paramref name="script"/> in the page; the result is its JSON value.</summary>
    Task<string> ExecuteScriptAsync(string script);

    void Navigate(string uri);

    /// <summary>A navigation is about to start; set <see cref="SignInNavigationStarting.Cancel"/> to stop it.</summary>
    event Action<SignInNavigationStarting>? NavigationStarting;

    /// <summary>A navigation finished: whether it succeeded, and the browser's status name.</summary>
    event Action<bool, string>? NavigationCompleted;

    /// <summary>A page posted a message; the argument is its JSON.</summary>
    event Action<string>? WebMessageReceived;
}

/// <summary>A browser that can be closed: a hidden sign-in host.</summary>
public interface ISignInHost : ISignInBrowser, IDisposable;

/// <summary>A navigation about to start.</summary>
public sealed class SignInNavigationStarting(string uri)
{
    public string Uri { get; } = uri;

    public bool Cancel { get; set; }
}
