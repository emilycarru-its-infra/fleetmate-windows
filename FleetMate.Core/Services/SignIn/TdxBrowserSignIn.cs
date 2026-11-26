using System.Text.Json;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using Serilog;

namespace FleetMate.Core.Services.SignIn;

/// <summary>
/// TeamDynamix sign-in in a browser, as the Windows user, shared by the WPF
/// and WinUI apps through <see cref="ISignInBrowser"/>.
///
/// <see cref="SignInAsync"/> is the whole silent chain: the HTTP phase
/// (<see cref="TdxSignInChain"/>), then a never-shown browser that walks the
/// same Shibboleth → Entra chain with the device's primary refresh token.
/// Entra's account picker is answered with the tile whose address is exactly
/// the Windows account's, "Stay signed in" is accepted, and once TeamDynamix
/// is reached the browser session asks loginsso for the JWT. Entra's passkey
/// page means it wants multi-factor authentication, which a hidden browser
/// cannot give: the attempt stops there with
/// <see cref="TdxSsoResult.NeedsInteraction"/>, and Sign In opens a visible
/// window driven by <see cref="TdxWindowSignIn"/>.
///
/// A JWT that cannot be confirmed as the Windows account's fails the sign-in
/// (<see cref="TdxSsoIdentity"/>). It is the user's own identity or nothing.
/// </summary>
public static class TdxBrowserSignIn
{
    /// <summary>Paths that mean the browser has reached TeamDynamix signed in.</summary>
    public static readonly string[] SignedInPaths =
    {
        "/SBTDClient/",
        "/TDClient/",
        "/TDNext/",
        "/TDWorkManagement",
        "/Home/Desktop",
    };

    public const string MfaReason =
        "Microsoft sign-in asked for multi-factor authentication. Click Sign In to finish it in a sign-in window.";

    /// <summary>
    /// The silent chain: HTTP, then the hidden browser <paramref name="createHost"/>
    /// makes. Call on the UI thread that hosts the browser.
    /// </summary>
    /// <param name="step">Told as each step starts, for the launch progress bar.</param>
    public static async Task<TdxSsoResult> SignInAsync(string baseUrl, Func<Task<ISignInHost>> createHost,
        Action<TdxSignInStep>? step = null, CancellationToken ct = default)
    {
        var (upn, http) = await TdxSignInChain.HttpPhaseAsync(baseUrl, step, ct);
        if (upn == null || TdxSignInChain.IsFinal(http)) return http;

        Log.Information("[tdx-sso] Phase 2: hidden WebView2 SSO");
        step?.Invoke(TdxSignInStep.FinishingSignIn);
        ISignInHost host;
        try
        {
            host = await createHost();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[tdx-sso] Could not start the hidden browser");
            return TdxSsoResult.Failed($"Silent SSO could not start the browser: {ex.Message}");
        }
        using (host)
        {
            return await HeadlessAsync(host, baseUrl, upn, ct);
        }
    }

    /// <summary>Walk the chain in a hidden browser until a JWT, a refusal, or the timeout.</summary>
    public static async Task<TdxSsoResult> HeadlessAsync(ISignInBrowser core, string baseUrl, string upn, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<TdxSsoResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? lastUrl = null;
        var accountAnswered = false;
        var jwtRequested = false;

        void OnMessage(string json)
        {
            try
            {
                using var message = JsonDocument.Parse(json);
                var root = message.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;
                if (root.TryGetProperty("debug", out var debug))
                    Log.Debug("[tdx-sso] [page] {Line}", debug.GetString());
                if (root.TryGetProperty("account", out JsonElement _))
                    accountAnswered = true;
            }
            catch (JsonException) { /* not ours */ }
        }

        async void OnNavigated(bool success, string status)
        {
            if (tcs.Task.IsCompleted) return;
            var url = core.Source ?? "";
            lastUrl = url;
            Log.Debug("[tdx-sso] [nav] {Status} {Url}", success ? "ok" : status, url);
            if (!success) return;

            try
            {
                if (EntraWebSignIn.IsPasskeyPage(url))
                {
                    Log.Information("[tdx-sso] Microsoft sign-in asked for multi-factor authentication; a visible sign-in is needed");
                    tcs.TrySetResult(TdxSsoResult.NeedsSignIn(MfaReason));
                    return;
                }

                if (EntraWebSignIn.IsEntraPage(url))
                {
                    await EntraPageDriver.HandleAsync(core, url, upn, accountAnswered, tcs.Task,
                        reason => tcs.TrySetResult(TdxSsoResult.Failed($"Silent SSO stopped: {reason}")));
                    return;
                }

                if (url.Contains("/api/auth/loginsso", StringComparison.OrdinalIgnoreCase))
                {
                    if (await ReadJwtAsync(core) is { } token) tcs.TrySetResult(Checked(token, upn));
                    else Log.Information("[tdx-sso] loginsso answered without a JWT");
                    return;
                }

                if (!jwtRequested && IsSignedIn(url))
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
        }

        core.WebMessageReceived += OnMessage;
        core.NavigationCompleted += OnNavigated;
        try
        {
            core.Navigate(TdxSsoService.BuildEntryUrl(baseUrl));
            var finished = await Task.WhenAny(tcs.Task, Task.Delay(EntraWebSignIn.HeadlessTimeout, ct));
            if (finished == tcs.Task) return tcs.Task.Result;
        }
        catch (OperationCanceledException) { /* reported below */ }
        finally
        {
            core.WebMessageReceived -= OnMessage;
            core.NavigationCompleted -= OnNavigated;
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

    /// <summary>True when <paramref name="url"/> is TeamDynamix itself, signed in.</summary>
    public static bool IsSignedIn(string url) =>
        SignedInPaths.Any(p => url.Contains(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>The JWT loginsso printed into the page, or null.</summary>
    internal static async Task<string?> ReadJwtAsync(ISignInBrowser core)
    {
        var body = await core.ExecuteScriptAsync("document.body ? document.body.innerText : ''");
        var token = (JsonSerializer.Deserialize<string>(body) ?? "").Trim().Trim('"');
        return TdxSsoService.LooksLikeJwt(token) ? token : null;
    }

    /// <summary>
    /// The sign-in result for a JWT, refused when it cannot be confirmed as the
    /// Windows account's. A refused token is dropped here and never reaches the caller.
    /// </summary>
    public static TdxSsoResult Checked(string token, string expectedUpn)
    {
        var result = TdxSsoIdentity.Verify(token, expectedUpn);
        if (!result.Success)
        {
            Log.Error("[tdx-sso] {Reason}; token discarded, sign-in refused", result.Error);
            return result;
        }
        Log.Information("[tdx-sso] JWT acquired for {User}", result.UserName ?? result.UserEmail ?? "(unknown)");
        return result;
    }
}

/// <summary>
/// The visible TeamDynamix sign-in, for when Entra wants a second factor: the
/// page handling behind the window each app shows. Only the account tile and
/// "Stay signed in" are answered for the person; the second factor is theirs
/// to give. <see cref="Finished"/> fires once, with the checked result.
/// </summary>
public sealed class TdxWindowSignIn
{
    private readonly ISignInBrowser _core;
    private readonly string _baseUrl;
    private readonly string _upn;
    private bool _jwtRequested;

    public TdxWindowSignIn(ISignInBrowser core, string baseUrl, string upn)
    {
        _core = core;
        _baseUrl = baseUrl;
        _upn = upn;
    }

    /// <summary>The line the window shows above the browser.</summary>
    public string Status { get; private set; } = "";

    public TdxSsoResult? Result { get; private set; }

    public event Action<string>? StatusChanged;

    public event Action<TdxSsoResult>? Finished;

    /// <summary>The window's opening line.</summary>
    public static string Prompt(string upn) =>
        $"Sign in as {upn}. This window closes by itself when you are signed in.";

    public void Start()
    {
        SetStatus(Prompt(_upn));
        // Entra's request carries the Windows account, so it is filled in.
        _core.NavigationStarting += e =>
        {
            if (Result != null || EntraWebSignIn.WithLoginHint(e.Uri, _upn) is not { } hinted) return;
            e.Cancel = true;
            _core.Navigate(hinted);
        };
        _core.NavigationCompleted += async (success, status) => await OnNavigatedAsync(success, status);
        _core.Navigate(TdxSsoService.BuildEntryUrl(_baseUrl));
    }

    private async Task OnNavigatedAsync(bool success, string status)
    {
        if (Result != null) return;
        var url = _core.Source ?? "";
        Log.Debug("[tdx-sso] [window] {Status} {Url}", success ? "ok" : status, url);
        if (!success) return;

        try
        {
            if (EntraWebSignIn.IsEntraPage(url))
            {
                await _core.ExecuteScriptAsync(EntraWebSignIn.KmsiScript);
                await _core.ExecuteScriptAsync(EntraWebSignIn.AccountScript(_upn));
                return;
            }

            if (url.Contains("/api/auth/loginsso", StringComparison.OrdinalIgnoreCase))
            {
                if (await TdxBrowserSignIn.ReadJwtAsync(_core) is { } token)
                {
                    var result = TdxBrowserSignIn.Checked(token, _upn);
                    if (!result.Success) SetStatus(result.Error ?? "TeamDynamix sign-in was refused.");
                    Finish(result);
                }
                return;
            }

            if (!_jwtRequested && TdxBrowserSignIn.IsSignedIn(url))
            {
                _jwtRequested = true;
                SetStatus("Signed in. Finishing…");
                _core.Navigate(TdxSsoService.BuildLoginSsoUrl(_baseUrl));
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[tdx-sso] Sign-in window page handling failed at {Url}", url);
        }
    }

    /// <summary>End the sign-in with <paramref name="result"/>, as a timeout or cancel does.</summary>
    public void Finish(TdxSsoResult result)
    {
        if (Result != null) return;
        Result = result;
        Finished?.Invoke(result);
    }

    private void SetStatus(string status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }
}
