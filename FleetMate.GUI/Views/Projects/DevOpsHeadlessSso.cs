using System.Text.Json;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Projects;
using FleetMate.GUI.Views.Shared;
using Serilog;

namespace FleetMate.GUI.Views.Projects;

/// <summary>
/// Azure DevOps sign-in through a never-shown WebView2: the OAuth2
/// authorization-code + PKCE flow, carried by the browser profile's Entra
/// session (the device's primary refresh token), with the account picker
/// answered by exact address. The redirect carrying the code is caught before
/// it loads and exchanged for tokens. Nothing is shown; when the flow cannot
/// finish on its own the result is a failure.
/// </summary>
internal static class DevOpsHeadlessSso
{
    /// <summary>Run the flow. Call on the UI thread.</summary>
    public static async Task<DevOpsSsoResult> SignInAsync(DevOpsSsoService sso, string? upn, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<DevOpsSsoResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? lastUrl = null;
        var accountAnswered = false;

        HiddenWebView browser;
        try
        {
            browser = await HiddenWebView.CreateAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[devops-sso] Could not start the hidden browser");
            return DevOpsSsoResult.Failed($"Silent SSO could not start the browser: {ex.Message}");
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
                        Log.Debug("[devops-sso] [page] {Line}", debug.GetString());
                    if (root.TryGetProperty("account", out JsonElement _))
                        accountAnswered = true;
                }
                catch (JsonException) { /* not ours */ }
            };

            core.NavigationStarting += async (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || !DevOpsSsoService.IsRedirectUri(uri)) return;
                e.Cancel = true;

                var code = DevOpsSsoService.ExtractCode(uri);
                if (string.IsNullOrEmpty(code))
                {
                    tcs.TrySetResult(DevOpsSsoResult.Failed(DevOpsSsoService.ExtractError(uri) ?? "No authorization code returned"));
                    return;
                }

                try
                {
                    tcs.TrySetResult(await sso.ExchangeCodeAsync(code));
                }
                catch (Exception ex)
                {
                    tcs.TrySetResult(DevOpsSsoResult.Failed($"Token exchange failed: {ex.Message}"));
                }
            };

            core.NavigationCompleted += async (_, e) =>
            {
                if (tcs.Task.IsCompleted) return;
                var url = core.Source ?? "";
                lastUrl = url;
                if (!e.IsSuccess || !EntraWebSignIn.IsEntraPage(url)) return;
                try
                {
                    await core.ExecuteScriptAsync(EntraWebSignIn.KmsiScript);
                    if (!accountAnswered && upn != null)
                        await core.ExecuteScriptAsync(EntraWebSignIn.AccountScript(upn));
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "[devops-sso] Page handling failed at {Url}", url);
                }
            };

            core.Navigate(sso.BuildAuthorizeUrl(loginHint: upn).AbsoluteUri);

            var finished = await Task.WhenAny(tcs.Task, Task.Delay(EntraWebSignIn.HeadlessTimeout, ct));
            if (finished == tcs.Task) return tcs.Task.Result;
        }

        var stop = Uri.TryCreate(lastUrl, UriKind.Absolute, out var stoppedAt)
            ? $"{stoppedAt.Host}{stoppedAt.AbsolutePath}"
            : "the authorize page";
        return DevOpsSsoResult.Failed($"Silent SSO stopped at {stop} after {EntraWebSignIn.HeadlessTimeout.TotalSeconds:0}s");
    }
}
