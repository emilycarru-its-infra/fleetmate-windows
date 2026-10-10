using System.Collections.Concurrent;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Serilog;

namespace FleetMate.Core.Services;

/// <summary>
/// Mints short-lived Entra access tokens for a target API audience off the
/// operator's own Windows sign-in, using WAM (the Web Account Manager broker).
///
/// This is the SSO model applied to arbitrary resource APIs (ReportMate,
/// Snipe-IT, …): the token carries the operator's delegated identity and role
/// assignment, validated server-side, so no shared secret ever leaves this
/// machine.
///
/// Why the broker rather than a CLI shell-out: on an Entra-joined or hybrid-joined
/// Windows device WAM can redeem the device-bound Primary Refresh Token directly,
/// so the common case is a genuinely silent token with no prompt, no browser and
/// no dependency on Azure CLI being installed or signed in. The silent path is
/// the only path: FleetMate never prompts, so a failure surfaces as signed out.
///
/// NOTE: this is deliberately NOT the <see cref="ElevationSession"/> path.
/// Elevation runs as a domain managed identity for privileged Graph/Intune work;
/// resource tokens for ReportMate/Snipe must carry the *operator's* identity and
/// role assignment, which is exactly what the broker returns.
/// </summary>
public sealed class EntraTokenSource
{
    /// <summary>
    /// The Azure CLI's well-known public client ID. Used when no dedicated
    /// FleetMate app registration is configured.
    ///
    /// This is the same client the previous `az account get-access-token` path
    /// authenticated as, so tenants that already consented to FleetMate's access
    /// keep working unchanged. It is a public client and supports the broker.
    /// Override with `entra_client_id` once a dedicated registration exists.
    /// </summary>
    public const string AzureCliClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

    /// <summary>
    /// Process-wide default, configured once at startup. Services resolve this
    /// lazily so a config reload is picked up without rebuilding them.
    /// </summary>
    public static EntraTokenSource? Shared { get; private set; }

    /// <summary>Build the shared instance from config. Safe to call again on reload.</summary>
    public static EntraTokenSource Configure(string? tenantId, string? clientId = null)
    {
        Shared = new EntraTokenSource(tenantId, clientId);
        return Shared;
    }

    private readonly IPublicClientApplication _app;
    // One gate per scope, so a slow broker call for one resource never holds
    // up another. A single shared gate let a stalled Azure DevOps request
    // starve the ReportMate dashboard for as long as the stall lasted.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Expiry)> _cache = new();

    /// <summary>
    /// The longest one silent acquisition may take. The broker can stall (a
    /// locked desktop, a hung network call); past this the caller gets an
    /// <see cref="EntraTokenException"/> instead of waiting indefinitely.
    /// </summary>
    internal TimeSpan AcquireTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Test seam: replaces the broker call with a stand-in.</summary>
    internal Func<string, CancellationToken, Task<(string Token, DateTimeOffset ExpiresOn)>>? AcquireOverride { get; init; }

    public EntraTokenSource(string? tenantId, string? clientId = null)
    {
        var authority = string.IsNullOrWhiteSpace(tenantId)
            ? "https://login.microsoftonline.com/organizations"
            : $"https://login.microsoftonline.com/{tenantId}";

        _app = PublicClientApplicationBuilder
            .Create(string.IsNullOrWhiteSpace(clientId) ? AzureCliClientId : clientId)
            .WithAuthority(authority)
            // Windows-only broker. On a device with a PRT this redeems it without UI.
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows))
            .WithDefaultRedirectUri()
            .Build();
    }

    /// <summary>
    /// A delegated access token for <paramref name="audience"/> — an app/client
    /// ID GUID, an <c>api://…</c> identifier URI, or an already-qualified scope.
    /// Cached until shortly before expiry.
    /// </summary>
    /// <exception cref="EntraTokenException">
    /// Thrown when no token can be obtained silently, or the broker times out.
    /// </exception>
    public async Task<string> GetTokenAsync(string audience, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(audience))
            throw new ArgumentException("An audience is required", nameof(audience));

        var scope = ToScope(audience);

        if (_cache.TryGetValue(scope, out var cached) && DateTimeOffset.UtcNow < cached.Expiry)
            return cached.Token;

        var gate = _gates.GetOrAdd(scope, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(scope, out var hit) && DateTimeOffset.UtcNow < hit.Expiry)
                return hit.Token;

            // The broker does not always honour cancellation, so the wait is
            // bounded here rather than trusted to the call itself.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(AcquireTimeout);
            (string Token, DateTimeOffset ExpiresOn) result;
            try
            {
                var acquire = AcquireOverride?.Invoke(scope, timeout.Token) ?? AcquireAsync(scope, timeout.Token);
                result = await acquire.WaitAsync(AcquireTimeout, ct);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                throw new EntraTokenException(scope, $"the sign-in broker did not answer within {AcquireTimeout.TotalSeconds:0} seconds");
            }

            // Refresh a little early so a token never expires mid-flight.
            _cache[scope] = (result.Token, result.ExpiresOn.AddMinutes(-5));
            return result.Token;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Drop cached tokens — used on sign-out and after a 401.</summary>
    public void Invalidate() => _cache.Clear();

    /// <summary>
    /// The address of the work account Windows is signed in with, as the broker
    /// (WAM) reports it from the device's primary refresh token. A headless web
    /// sign-in uses it to pick that exact account in Entra's account picker.
    /// Silent only; null when there is no such account or the broker stalls.
    /// </summary>
    public async Task<string?> GetOperatingSystemAccountUpnAsync(CancellationToken ct = default)
    {
        try
        {
            var acquire = _app
                .AcquireTokenSilent(new[] { "https://graph.microsoft.com/.default" }, PublicClientApplication.OperatingSystemAccount)
                .ExecuteAsync(ct);
            var result = await acquire.WaitAsync(AcquireTimeout, ct);
            var upn = result.Account?.Username;
            return string.IsNullOrWhiteSpace(upn) || !upn.Contains('@') ? null : upn.Trim();
        }
        catch (Exception ex) when (ex is MsalException or TimeoutException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            Log.Debug(ex, "[entra] No operating-system account from the broker");
            return null;
        }
    }

    private async Task<(string Token, DateTimeOffset ExpiresOn)> AcquireAsync(string scope, CancellationToken ct)
    {
        var result = await AcquireResultAsync(scope, ct);
        return (result.AccessToken, result.ExpiresOn);
    }

    private async Task<AuthenticationResult> AcquireResultAsync(string scope, CancellationToken ct)
    {
        var scopes = new[] { scope };

        // 1. The signed-in Windows account, redeemed straight from the device PRT.
        //    This is the path that makes FleetMate silent on a managed device.
        try
        {
            return await _app
                .AcquireTokenSilent(scopes, PublicClientApplication.OperatingSystemAccount)
                .ExecuteAsync(ct);
        }
        catch (MsalUiRequiredException)
        {
            // No PRT, or the resource needs consent this account hasn't given.
            // Fall through — a cached MSAL account may still serve.
        }
        catch (MsalServiceException ex)
        {
            Log.Debug(ex, "[entra] Broker declined the OS account for {Scope}", scope);
        }

        // 2. Any account MSAL has already seen this session.
        foreach (var account in await _app.GetAccountsAsync())
        {
            try
            {
                return await _app.AcquireTokenSilent(scopes, account).ExecuteAsync(ct);
            }
            catch (MsalUiRequiredException)
            {
                // Try the next account.
            }
        }

        // FleetMate never opens a sign-in window: when neither silent path
        // works, the system is reported as signed out and the operator fixes
        // the sign-in outside the app (az login, or the Windows account).
        throw new EntraTokenException(
            scope,
            "no silent credential — sign in to Windows with a work account, or run az login");
    }

    /// <summary>
    /// Turn an audience into a scope. Callers configure audiences (a GUID or an
    /// <c>api://…</c> URI); Entra wants a scope, and for a resource-wide
    /// delegated token that is <c>{audience}/.default</c>.
    /// </summary>
    internal static string ToScope(string audience)
    {
        var trimmed = audience.Trim();

        // Already a resource-wide scope.
        if (trimmed.EndsWith("/.default", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        // Already a *named* permission, e.g. "https://graph.microsoft.com/User.Read".
        // This branch used to be missing despite the comment claiming otherwise, so a
        // caller asking for one delegated permission silently got
        // ".../User.Read/.default" — not a scope Entra recognises, which surfaces as a
        // token-acquisition failure rather than anything pointing at the real cause.
        // A named permission is the last path segment containing a dot with no
        // trailing slash; a bare audience (a GUID, "api://…", "https://graph.microsoft.com")
        // never looks like that.
        var lastSlash = trimmed.LastIndexOf('/');
        if (lastSlash > 0 && lastSlash < trimmed.Length - 1)
        {
            var lastSegment = trimmed[(lastSlash + 1)..];
            // Exclude host-only audiences: "graph.microsoft.com" has dots but follows "//".
            var isHostSegment = lastSlash >= 1 && trimmed[lastSlash - 1] == '/';
            if (!isHostSegment && lastSegment.Contains('.') && !lastSegment.Contains(' '))
                return trimmed;
        }

        return $"{trimmed.TrimEnd('/')}/.default";
    }
}

/// <summary>Raised when no Entra token can be obtained for a resource.</summary>
public sealed class EntraTokenException : Exception
{
    public string Audience { get; }

    public EntraTokenException(string audience, string detail, Exception? inner = null)
        : base($"Could not acquire an Entra token for {audience} — {detail}", inner)
    {
        Audience = audience;
    }
}
