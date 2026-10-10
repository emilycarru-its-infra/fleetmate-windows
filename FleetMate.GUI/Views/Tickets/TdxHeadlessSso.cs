using System.Linq;
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
/// fall back to. A JWT from either phase that cannot be confirmed as the
/// Windows account's — another account's, or one with no email or UPN — fails
/// the sign-in (<see cref="TdxSsoIdentity"/>), and nothing further is tried,
/// since the next phase would reach the same account. When the Windows
/// account's own address cannot be found, no phase is tried at all.
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
        var upn = await TdxSsoIdentity.ResolveWindowsUpnAsync(ct);
        if (upn == null)
        {
            Log.Error("[tdx-sso] No Windows work-account address from the logon, the sign-in broker or the registry; the session's account cannot be checked, sign-in refused");
            return TdxSsoResult.Refused(TdxSsoResult.UnknownExpectedAddressReason);
        }
        Log.Information("[tdx-sso] Signing in as the Windows account {Upn}", upn);

        Log.Information("[tdx-sso] Phase 1: silent HTTP SSO (Negotiate/Kerberos)");
        var http = await Task.Run(() => new TdxSsoService(baseUrl).TrySilentSsoAsync(ct, upn), ct);
        if (http is { Success: true, Token: not null } or { IdentityRefused: true }) return http;

        Log.Information("[tdx-sso] Phase 2: hidden WebView2 SSO");
        return await HiddenBrowserAsync(baseUrl, upn, ct);
    }

    private static async Task<TdxSsoResult> HiddenBrowserAsync(string baseUrl, string upn, CancellationToken ct)
    {
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
                        // The same handling as the API and Azure DevOps sign-ins,
                        // including giving up on Entra's passkey page early.
                        await EntraPageDriver.HandleAsync(core, url, upn, accountAnswered, tcs.Task,
                            reason => tcs.TrySetResult(TdxSsoResult.Failed($"Silent SSO stopped: {reason}")));
                        return;
                    }

                    if (url.Contains("/api/auth/loginsso", StringComparison.OrdinalIgnoreCase))
                    {
                        var body = await core.ExecuteScriptAsync("document.body ? document.body.innerText : ''");
                        var token = (JsonSerializer.Deserialize<string>(body) ?? "").Trim().Trim('"');
                        if (TdxSsoService.LooksLikeJwt(token))
                            tcs.TrySetResult(Checked(token, upn));
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

    /// <summary>
    /// The sign-in result for a JWT, refused when it cannot be confirmed as the
    /// Windows account's. A refused token is dropped here and never
    /// reaches the caller.
    /// </summary>
    private static TdxSsoResult Checked(string token, string expectedUpn)
    {
        var result = TdxSsoIdentity.Verify(token, expectedUpn);
        if (!result.Success)
        {
            Log.Error("[tdx-sso] {Reason}; token discarded, sign-in refused", result.Error);
            return result;
        }
        Log.Information("[tdx-sso] JWT acquired in the hidden browser for {User}", result.UserName ?? result.UserEmail ?? "(unknown)");
        return result;
    }
}
