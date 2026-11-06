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
/// The visible TeamDynamix sign-in, opened by Sign In when the hidden one
/// could not finish, most often because Entra asked for multi-factor
/// authentication. It walks the same chain in the same browser profile as
/// <see cref="TdxHeadlessSso"/>, but the person answers Entra's pages,
/// passkeys included. Once TeamDynamix is reached it asks loginsso for the
/// JWT and closes. The Entra cookies stay in the shared profile and the
/// caller saves the session, so the next launch signs in silently again.
/// The token passes the same identity check as a silent sign-in.
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
    /// Show the window over <paramref name="owner"/> and wait for it to close.
    /// Null when the person closed it, or the browser could not start.
    /// </summary>
    public static async Task<TdxSsoResult?> SignInAsync(string baseUrl, Window? owner)
    {
        var upn = await TdxSsoIdentity.ResolveWindowsUpnAsync();
        if (upn == null)
        {
            Log.Error("[tdx-sso] No Windows work-account address; the sign-in window is not opened");
            return TdxSsoResult.Refused(TdxSsoResult.UnknownExpectedAddressReason);
        }

        var window = new TdxSignInWindow(baseUrl, upn);
        if (owner is { IsVisible: true }) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Log.Information("[tdx-sso] Opening the sign-in window for {Upn}", upn);
        window.ShowDialog();
        Log.Information("[tdx-sso] Sign-in window closed: {Outcome}",
            window.Result is { Success: true } ? "signed in" : window.Result?.Error ?? "closed before signing in");
        return window.Result;
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
