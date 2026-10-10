using System.Linq;
using System.Security.Principal;
using System.Text.Json;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Tickets;
using FleetMate.GUI.Views.Shared;
using Microsoft.Web.WebView2.Core;
using Serilog;

namespace FleetMate.GUI.Views.Tickets;

/// <summary>
/// TeamDynamix sign-in as the Windows user, with nothing on screen.
///
///   1. HTTP with the Windows credentials (Negotiate/Kerberos) follows the
///      Shibboleth → Entra chain and asks <c>/api/auth/loginsso</c> for a JWT.
///   2. Otherwise a never-shown WebView2 walks the same chain. Its profile
///      signs in to Entra with the device's primary refresh token; Entra's
///      account picker is answered with the tile whose address is exactly the
///      Windows account's, "Stay signed in" is accepted, and once TeamDynamix is
///      reached the browser session asks loginsso for the JWT.
///
/// There is no third, visible step. When neither finishes within
/// <see cref="EntraWebSignIn.HeadlessTimeout"/> the result is a failure that
/// names where the chain stopped, and TeamDynamix is reported as not signed in.
/// It is the user's own identity or nothing: there is no shared account to
/// fall back to.
/// </summary>
internal static class TdxHeadlessSso
{
    /// <summary>Paths that mean the browser has reached TeamDynamix signed in.</summary>
    private static readonly string[] SignedInPaths =
    {
        "/SBTDClient/",
        "/TDClient/",
        "/TDNext/",
        "/TDWorkManagement",
        "/Home/Desktop",
    };

    /// <summary>Run both phases. Call on the UI thread.</summary>
    public static async Task<TdxSsoResult> SignInAsync(string baseUrl, CancellationToken ct = default)
    {
        Log.Information("[tdx-sso] Phase 1: silent HTTP SSO (Negotiate/Kerberos)");
        var http = await Task.Run(() => new TdxSsoService(baseUrl).TrySilentSsoAsync(ct), ct);
        if (http is { Success: true, Token: not null }) return http;

        Log.Information("[tdx-sso] Phase 2: hidden WebView2 SSO");
        return await HiddenBrowserAsync(baseUrl, ct);
    }

    private static async Task<TdxSsoResult> HiddenBrowserAsync(string baseUrl, CancellationToken ct)
    {
        var upn = await ResolveUpnAsync(ct);
        if (upn == null)
            Log.Warning("[tdx-sso] No Windows work-account address found; Entra's account picker cannot be answered");
        else
            Log.Information("[tdx-sso] Signing in as the Windows account {Upn}", upn);

        var tcs = new TaskCompletionSource<TdxSsoResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? lastUrl = null;
        var accountAnswered = false;
        var jwtRequested = false;

        HiddenWebView browser;
        try
        {
            browser = await HiddenWebView.CreateAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[tdx-sso] Could not start the hidden browser");
            return TdxSsoResult.Failed($"Silent SSO could not start the browser: {ex.Message}");
        }

        using (browser)
        {
            var core = browser.Core;

            core.WebMessageReceived += (_, e) =>
            {
                try
                {
                    using var message = JsonDocument.Parse(e.WebMessageAsJson);
                    var root = message.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return;
                    if (root.TryGetProperty("debug", out var debug))
                        Log.Debug("[tdx-sso] [page] {Line}", debug.GetString());
                    if (root.TryGetProperty("account", out JsonElement _))
                        accountAnswered = true;
                }
                catch (JsonException) { /* not ours */ }
            };

            core.NavigationCompleted += async (_, e) =>
            {
                if (tcs.Task.IsCompleted) return;
                var url = core.Source ?? "";
                lastUrl = url;
                Log.Debug("[tdx-sso] [nav] {Status} {Url}", e.IsSuccess ? "ok" : e.WebErrorStatus.ToString(), url);
                if (!e.IsSuccess) return;

                try
                {
                    if (EntraWebSignIn.IsEntraPage(url))
                    {
                        await core.ExecuteScriptAsync(EntraWebSignIn.KmsiScript);
                        if (!accountAnswered && upn != null)
                            await core.ExecuteScriptAsync(EntraWebSignIn.AccountScript(upn));
                        ScheduleMethodFallback(core, url, tcs.Task);
                        return;
                    }

                    if (url.Contains("/api/auth/loginsso", StringComparison.OrdinalIgnoreCase))
                    {
                        var body = await core.ExecuteScriptAsync("document.body ? document.body.innerText : ''");
                        var token = (JsonSerializer.Deserialize<string>(body) ?? "").Trim().Trim('"');
                        if (TdxSsoService.LooksLikeJwt(token))
                            tcs.TrySetResult(Succeeded(token, upn));
                        else
                            Log.Information("[tdx-sso] loginsso answered without a JWT");
                        return;
                    }

                    if (!jwtRequested && SignedInPaths.Any(p => url.Contains(p, StringComparison.OrdinalIgnoreCase)))
                    {
                        jwtRequested = true;
                        Log.Information("[tdx-sso] TeamDynamix reached; requesting the JWT in the browser session");
                        core.Navigate(TdxSsoService.BuildLoginSsoUrl(baseUrl));
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "[tdx-sso] Page handling failed at {Url}", url);
                }
            };

            core.Navigate(TdxSsoService.BuildEntryUrl(baseUrl));

            var finished = await Task.WhenAny(tcs.Task, Task.Delay(EntraWebSignIn.HeadlessTimeout, ct));
            if (finished == tcs.Task) return tcs.Task.Result;
        }

        var stop = Uri.TryCreate(lastUrl, UriKind.Absolute, out var stoppedAt)
            ? $"{stoppedAt.Host}{stoppedAt.AbsolutePath}"
            : "the start of the sign-in chain";
        var failed = TdxSsoResult.Failed(ct.IsCancellationRequested
            ? "Silent SSO was cancelled"
            : $"Silent SSO stopped at {stop} after {EntraWebSignIn.HeadlessTimeout.TotalSeconds:0}s");
        Log.Warning("[tdx-sso] {Reason}", failed.Error);
        return failed;
    }

    private static TdxSsoResult Succeeded(string token, string? expectedUpn)
    {
        var (name, email) = TdxSsoService.ExtractUserInfoFromJwt(token);
        if (expectedUpn != null && email != null && !email.Equals(expectedUpn, StringComparison.OrdinalIgnoreCase))
            Log.Warning("[tdx-sso] Signed in as {Email}, not the Windows account {Upn}", email, expectedUpn);
        Log.Information("[tdx-sso] JWT acquired in the hidden browser for {User}", name ?? email ?? "(unknown)");
        return new TdxSsoResult
        {
            Success = true,
            Token = token,
            UserName = name,
            UserEmail = email,
            Expiry = TdxSsoService.ReadExpiry(token),
        };
    }

    /// <summary>
    /// If an Entra page is still showing a few seconds after it loaded — a
    /// passkey prompt the hidden browser cannot answer — offer Entra another
    /// method. The script runs inside the page; nothing is shown.
    /// </summary>
    private static void ScheduleMethodFallback(CoreWebView2 core, string url, Task done)
    {
        _ = Task.Delay(3000).ContinueWith(async _ =>
        {
            if (done.IsCompleted) return;
            try
            {
                if (!string.Equals(core.Source, url, StringComparison.Ordinal)) return;
                await core.ExecuteScriptAsync(MethodFallbackScript);
            }
            catch { /* the browser has gone */ }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// The Windows account's address: the broker's operating-system account
    /// first (the identity behind the device's primary refresh token), then the
    /// UPN claim on the Windows logon token.
    /// </summary>
    private static async Task<string?> ResolveUpnAsync(CancellationToken ct)
    {
        if (EntraTokenSource.Shared is { } broker)
        {
            var upn = await broker.GetOperatingSystemAccountUpnAsync(ct);
            if (upn != null) return upn.ToLowerInvariant();
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var claim = identity.Claims.FirstOrDefault(c =>
                c.Type == "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn");
            if (claim?.Value.Contains('@') == true) return claim.Value.ToLowerInvariant();
            if (identity.Name?.Contains('@') == true) return identity.Name.ToLowerInvariant();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[tdx-sso] Could not read the Windows identity");
        }
        return null;
    }

    /// <summary>
    /// Moves Entra off a passkey page: "Sign in another way", then a non-FIDO
    /// method such as an Authenticator push. Never "Try again", which loops on
    /// the failing passkey.
    /// </summary>
    private const string MethodFallbackScript = """
        (function() {
            if (window.__fleetmateFidoFallback) return;
            window.__fleetmateFidoFallback = true;
            var acted = false;
            function post(m) { try { window.chrome.webview.postMessage({ debug: m }); } catch (e) {} }
            function tryFallback() {
                if (acted) return true;
                var elems = document.querySelectorAll('a, button, [role="link"], [role="button"], input[type="submit"], span[tabindex], div[tabindex], li[tabindex]');
                for (var i = 0; i < elems.length; i++) {
                    var text = (elems[i].textContent || elems[i].value || '').trim().toLowerCase();
                    if (text.indexOf('sign in another way') !== -1 ||
                        text.indexOf('other ways to sign in') !== -1 ||
                        text.indexOf('use another method') !== -1 ||
                        text.indexOf('use a different method') !== -1 ||
                        text.indexOf('try another way') !== -1 ||
                        text.indexOf("i can't use") !== -1) {
                        post('[FIDO] Clicking: ' + text);
                        elems[i].click();
                        acted = true;
                        return true;
                    }
                }
                var bodyText = (document.body && document.body.innerText) || '';
                var isFidoPage = bodyText.indexOf('passkey') !== -1 ||
                                 bodyText.indexOf('security key') !== -1 ||
                                 bodyText.indexOf('FIDO') !== -1;
                var isErrorPage = bodyText.indexOf("couldn’t sign you in") !== -1 ||
                                  bodyText.indexOf("couldn't sign you in") !== -1 ||
                                  bodyText.indexOf('Something went wrong') !== -1;
                if (!isFidoPage && !isErrorPage) return false;
                var tiles = document.querySelectorAll('[data-value]');
                var preferred = ['PhoneAppNotification', 'PhoneAppOTP', 'OneWaySMS', 'TwoWayVoiceMobile'];
                for (var p = 0; p < preferred.length; p++) {
                    for (var t = 0; t < tiles.length; t++) {
                        if ((tiles[t].getAttribute('data-value') || '') === preferred[p]) {
                            post('[FIDO] Selecting method: ' + preferred[p]);
                            tiles[t].click();
                            acted = true;
                            return true;
                        }
                    }
                }
                post('[FIDO] Page: ' + location.host + location.pathname + ' Body: ' + bodyText.slice(0, 160).replace(/\s+/g, ' '));
                return false;
            }
            if (document.body) {
                var observer = new MutationObserver(function() { tryFallback(); });
                observer.observe(document.body, { childList: true, subtree: true });
                setTimeout(function() { observer.disconnect(); }, 45000);
            }
            [0, 500, 1000, 2000, 4000, 8000, 13000, 20000].forEach(function(d) { setTimeout(tryFallback, d); });
        })();
        """;
}
