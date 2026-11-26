using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Tickets;
using FleetMate.GUI.Views.Shared;
using FleetMate.Core.Services.SignIn;
using FleetMate.SignIn;
using Microsoft.Web.WebView2.Wpf;
using Serilog;

namespace FleetMate.GUI.Views.Tickets;

/// <summary>
/// The visible TeamDynamix sign-in: a short-lived last resort after every
/// silent path has failed, most often because Entra asked for multi-factor
/// authentication. Only a Sign In click opens it, as on FleetMate for Mac;
/// renewal never does. It walks the same chain in the same browser profile as
/// <see cref="TdxHeadlessSso"/>, with the Windows account filled in: the
/// account tile is answered and Entra's request carries <c>login_hint</c>,
/// so single sign-on can finish it, and otherwise the person answers Entra's
/// pages, passkeys included. Once TeamDynamix is reached it asks loginsso
/// for the JWT and closes. It is never modal, closes as soon as a token
/// arrives by any route (the caller cancels it), and times out on its own.
/// The Entra cookies stay in the shared profile and the caller saves the
/// session, so the next launch signs in silently again. The token passes the
/// same identity check as a silent sign-in.
/// </summary>
internal sealed class TdxSignInWindow : Window
{
    private readonly string _baseUrl;
    private readonly string _upn;
    private readonly WebView2 _web = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(12, 8, 12, 8), TextWrapping = TextWrapping.Wrap };
    private TdxWindowSignIn? _flow;

    /// <summary>The finished sign-in; null when the window was closed first.</summary>
    public TdxSsoResult? Result { get; private set; }

    private TdxSignInWindow(string baseUrl, string upn)
    {
        _baseUrl = baseUrl;
        _upn = upn;
        Title = "Sign in to TeamDynamix";
        Width = 520;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = true;

        _status.Text = TdxWindowSignIn.Prompt(upn);
        var root = new DockPanel();
        DockPanel.SetDock(_status, Dock.Top);
        root.Children.Add(_status);
        root.Children.Add(_web);
        Content = root;

        Loaded += async (_, _) => await StartAsync();
    }

    /// <summary>
    /// Show the window over <paramref name="owner"/>, without blocking the UI,
    /// and wait for it to close. Null when the person closed it or
    /// <paramref name="ct"/> was cancelled (a token arrived another way); a
    /// failure when it timed out or the browser could not start.
    /// </summary>
    public static async Task<TdxSsoResult?> SignInAsync(string baseUrl, Window? owner, TimeSpan timeout,
        CancellationToken ct)
    {
        var upn = await TdxSsoIdentity.ResolveWindowsUpnAsync();
        if (upn == null)
        {
            Log.Error("[tdx-sso] No Windows work-account address; the sign-in window is not opened");
            return TdxSsoResult.Refused(TdxSsoResult.UnknownExpectedAddressReason);
        }

        if (ct.IsCancellationRequested) return null;

        var window = new TdxSignInWindow(baseUrl, upn);
        if (owner is { IsVisible: true }) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var closed = new TaskCompletionSource<TdxSsoResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult(window.Result);

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = timeout };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (window.Result != null) return;
            Log.Warning("[tdx-sso] The sign-in window timed out after {Timeout}; closing it", timeout);
            window.Finish(TdxSsoResult.Failed("The sign-in window timed out"));
        };
        using var cancel = ct.Register(() => window.Dispatcher.BeginInvoke(() =>
        {
            if (window.Result != null || !window.IsVisible) return;
            Log.Information("[tdx-sso] A TeamDynamix token arrived; closing the sign-in window");
            window.Close();
        }));

        Log.Information("[tdx-sso] Opening the sign-in window for {Upn}", upn);
        timer.Start();
        window.Show();
        var result = await closed.Task;
        timer.Stop();
        Log.Information("[tdx-sso] Sign-in window closed: {Outcome}",
            result is { Success: true } ? "signed in" : result?.Error ?? "closed before signing in");
        return result;
    }

    private async Task StartAsync()
    {
        try
        {
            await _web.EnsureCoreWebView2Async(await WebViewEnvironments.SignIn());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[tdx-sso] The sign-in window's browser could not start");
            Finish(TdxSsoResult.Failed($"The sign-in browser could not start: {ex.Message}"));
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        // A sign-in page that opens a window stays in this one.
        core.NewWindowRequested += (_, e) => { e.Handled = true; core.Navigate(e.Uri); };
        // The page handling is shared with the WinUI app.
        _flow = new TdxWindowSignIn(new CoreWebView2SignInBrowser(core), _baseUrl, _upn);
        _flow.StatusChanged += status => _status.Text = status;
        _flow.Finished += result =>
        {
            Result = result;
            Close();
        };
        _flow.Start();
    }

    private void Finish(TdxSsoResult result)
    {
        if (_flow != null)
        {
            _flow.Finish(result);
            return;
        }
        Result = result;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        try { _web.Dispose(); } catch { /* already gone */ }
        base.OnClosed(e);
    }
}
