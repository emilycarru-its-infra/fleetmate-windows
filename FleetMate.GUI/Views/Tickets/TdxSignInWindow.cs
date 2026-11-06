using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Tickets;
using FleetMate.GUI.Views.Shared;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Serilog;

namespace FleetMate.GUI.Views.Tickets;

/// <summary>
/// The visible TeamDynamix sign-in: a short-lived last resort after every
/// silent path has failed, most often because Entra asked for multi-factor
/// authentication. Sign In opens it on demand, and the app may open it by
/// itself once the token is gone (see <see cref="TdxSessionPolicy.ShouldAutoOpenWindow"/>).
/// It walks the same chain in the same browser profile as
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
    private bool _jwtRequested;

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

        _status.Text = $"Sign in as {upn}. This window closes by itself when you are signed in.";
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
    /// <param name="activate">False opens it without taking focus, for an automatic open.</param>
    public static async Task<TdxSsoResult?> SignInAsync(string baseUrl, Window? owner, TimeSpan timeout,
        bool activate, CancellationToken ct)
    {
        var upn = await TdxSsoIdentity.ResolveWindowsUpnAsync();
        if (upn == null)
        {
            Log.Error("[tdx-sso] No Windows work-account address; the sign-in window is not opened");
            return TdxSsoResult.Refused(TdxSsoResult.UnknownExpectedAddressReason);
        }

        if (ct.IsCancellationRequested) return null;

        var window = new TdxSignInWindow(baseUrl, upn) { ShowActivated = activate };
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

        Log.Information("[tdx-sso] Opening the sign-in window for {Upn} ({How})", upn, activate ? "on request" : "automatically");
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
        // Entra's request carries the Windows account, so it is filled in.
        core.NavigationStarting += (_, e) =>
        {
            if (Result != null || EntraWebSignIn.WithLoginHint(e.Uri, _upn) is not { } hinted) return;
            e.Cancel = true;
            core.Navigate(hinted);
        };
        core.NavigationCompleted += async (_, e) => await OnNavigatedAsync(core, e);
        core.Navigate(TdxSsoService.BuildEntryUrl(_baseUrl));
    }

    private async Task OnNavigatedAsync(CoreWebView2 core, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (Result != null) return;
        var url = core.Source ?? "";
        Log.Debug("[tdx-sso] [window] {Status} {Url}", e.IsSuccess ? "ok" : e.WebErrorStatus.ToString(), url);
        if (!e.IsSuccess) return;

        try
        {
            if (EntraWebSignIn.IsEntraPage(url))
            {
                // Only the account tile and "Stay signed in" are answered for the
                // person; the second factor is theirs to give.
                await core.ExecuteScriptAsync(EntraWebSignIn.KmsiScript);
                await core.ExecuteScriptAsync(EntraWebSignIn.AccountScript(_upn));
                return;
            }

            if (url.Contains("/api/auth/loginsso", StringComparison.OrdinalIgnoreCase))
            {
                var body = await core.ExecuteScriptAsync("document.body ? document.body.innerText : ''");
                var token = (JsonSerializer.Deserialize<string>(body) ?? "").Trim().Trim('"');
                if (TdxSsoService.LooksLikeJwt(token))
                {
                    var result = TdxHeadlessSso.Checked(token, _upn);
                    if (!result.Success) _status.Text = result.Error ?? "TeamDynamix sign-in was refused.";
                    Finish(result);
                }
                return;
            }

            if (!_jwtRequested && TdxHeadlessSso.SignedInPaths.Any(p => url.Contains(p, StringComparison.OrdinalIgnoreCase)))
            {
                _jwtRequested = true;
                _status.Text = "Signed in. Finishing…";
                core.Navigate(TdxSsoService.BuildLoginSsoUrl(_baseUrl));
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[tdx-sso] Sign-in window page handling failed at {Url}", url);
        }
    }

    private void Finish(TdxSsoResult result)
    {
        Result = result;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        try { _web.Dispose(); } catch { /* already gone */ }
        base.OnClosed(e);
    }
}
