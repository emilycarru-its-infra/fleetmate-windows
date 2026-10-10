using FleetMate.Core.Services;
using Microsoft.Web.WebView2.Core;
using Serilog;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// What every hidden sign-in does on an Entra page, so the API tokens, Azure
/// DevOps and TeamDynamix take the same path through it: accept "Stay signed
/// in", pick the Windows account's tile by exact address, move off a passkey
/// prompt to another method, and give up on the passkey page early rather
/// than waiting out the whole attempt. Call on the UI thread.
/// </summary>
internal static class EntraPageDriver
{
    /// <summary>
    /// Handle one loaded Entra page. <paramref name="fail"/> is called with the
    /// reason when the browser is still on the passkey page after
    /// <see cref="EntraWebSignIn.PasskeyGiveUp"/>.
    /// </summary>
    public static async Task HandleAsync(CoreWebView2 core, string url, string? upn, bool accountAnswered, Task done, Action<string> fail)
    {
        await core.ExecuteScriptAsync(EntraWebSignIn.KmsiScript);
        if (!accountAnswered && upn != null)
            await core.ExecuteScriptAsync(EntraWebSignIn.AccountScript(upn));
        ScheduleMethodFallback(core, url, done);
        if (EntraWebSignIn.IsPasskeyPage(url))
            WatchPasskey(core, done, fail);
    }

    /// <summary>
    /// If an Entra page is still showing a few seconds after it loaded — a
    /// passkey prompt the hidden browser cannot answer — offer Entra another
    /// method. The script runs inside the page; nothing is shown.
    /// </summary>
    public static void ScheduleMethodFallback(CoreWebView2 core, string url, Task done)
    {
        _ = Task.Delay(3000).ContinueWith(async _ =>
        {
            if (done.IsCompleted) return;
            try
            {
                if (!string.Equals(core.Source, url, StringComparison.Ordinal)) return;
                await core.ExecuteScriptAsync(EntraWebSignIn.MethodFallbackScript);
            }
            catch { /* the browser has gone */ }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static void WatchPasskey(CoreWebView2 core, Task done, Action<string> fail)
    {
        _ = Task.Delay(EntraWebSignIn.PasskeyGiveUp).ContinueWith(_ =>
        {
            if (done.IsCompleted) return;
            try
            {
                var now = core.Source;
                if (!EntraWebSignIn.IsPasskeyPage(now)) return;
                var reason = EntraWebSignIn.PasskeyReason(now);
                Log.Warning("[entra-web] {Reason}; giving up after {Seconds:0}s", reason, EntraWebSignIn.PasskeyGiveUp.TotalSeconds);
                fail(reason);
            }
            catch { /* the browser has gone */ }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
