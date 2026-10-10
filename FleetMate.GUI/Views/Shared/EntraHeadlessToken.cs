using System.Text.Json;
using System.Windows;
using FleetMate.Core.Services;
using Serilog;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// The hidden-browser path of <see cref="EntraTokenSource"/>: an authorization
/// code + PKCE sign-in for one API scope (Snipe-IT, ReportMate, …) in a WebView2
/// that is never shown, carried by the browser profile's Entra session and the
/// device's primary refresh token, with the account picker answered by exact
/// address and Entra's pages handled as the TeamDynamix sign-in handles them
/// (<see cref="EntraPageDriver"/>). It is what the TeamDynamix and Azure DevOps sign-ins already do, and
/// it is how the APIs still get a token when the broker will not hand one over,
/// as it can refuse in a disconnected remote session. Nothing is ever shown;
/// a flow that cannot finish on its own fails.
/// </summary>
internal static class EntraHeadlessToken
{
    // One hidden browser at a time: several scopes failing together would
    // otherwise each start their own, all racing through the same profile.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Register with the token source. Call once, at startup.</summary>
    public static void Register(Application app)
    {
        EntraTokenSource.HeadlessSignIn = async (request, ct) =>
        {
            await Gate.WaitAsync(ct);
            try
            {
                return await app.Dispatcher.InvokeAsync(() => AcquireAsync(request, ct)).Task.Unwrap();
            }
            finally
            {
                Gate.Release();
            }
        };
    }

    /// <summary>Run the flow. Call on the UI thread.</summary>
    private static async Task<(string Token, DateTimeOffset ExpiresOn)> AcquireAsync(EntraHeadlessRequest request, CancellationToken ct)
    {
        var flow = new EntraAuthCodeFlow(request);
        var tcs = new TaskCompletionSource<(string, DateTimeOffset)>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? lastUrl = null;
        var accountAnswered = false;

        using var browser = await HiddenWebView.CreateAsync();
        var core = browser.Core;

        core.WebMessageReceived += (_, e) =>
        {
            try
            {
                using var message = JsonDocument.Parse(e.WebMessageAsJson);
                var root = message.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;
                if (root.TryGetProperty("debug", out var debug))
                    Log.Debug("[entra-web] [page] {Line}", debug.GetString());
                if (root.TryGetProperty("account", out JsonElement _))
                    accountAnswered = true;
            }
            catch (JsonException) { /* not ours */ }
        };

        core.NavigationStarting += async (_, e) =>
        {
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || !EntraAuthCodeFlow.IsRedirect(uri)) return;
            e.Cancel = true;
            try
            {
                tcs.TrySetResult(await flow.RedeemAsync(uri, ct));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
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
                await EntraPageDriver.HandleAsync(core, url, request.LoginHint, accountAnswered, tcs.Task,
                    reason => tcs.TrySetException(new InvalidOperationException(reason)));
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[entra-web] Page handling failed at {Url}", url);
            }
        };

        Log.Information("[entra-web] Hidden sign-in for {Scope}", request.Scope);
        core.Navigate(flow.AuthorizeUrl.AbsoluteUri);

        var finished = await Task.WhenAny(tcs.Task, Task.Delay(EntraWebSignIn.HeadlessTimeout, ct));
        if (finished == tcs.Task) return await tcs.Task;

        var stop = Uri.TryCreate(lastUrl, UriKind.Absolute, out var stoppedAt)
            ? $"{stoppedAt.Host}{stoppedAt.AbsolutePath}"
            : "the authorize page";
        throw new TimeoutException($"stopped at {stop} after {EntraWebSignIn.HeadlessTimeout.TotalSeconds:0}s");
    }
}
