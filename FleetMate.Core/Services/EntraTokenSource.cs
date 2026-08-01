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
/// no dependency on Azure CLI being installed or signed in. That makes the silent
/// path the *primary* path here, not a best-effort attempt.
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
    /// Supplies the window handle the broker parents its (rare) interactive
    /// prompts to. The GUI sets this to its main window; the CLI leaves it null
    /// and never prompts. Living here keeps Core free of a WPF dependency.
    /// </summary>
    public static Func<IntPtr>? ParentWindowProvider { get; set; }

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
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (string Token, DateTimeOffset Expiry)> _cache = new();

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
            .WithParentActivityOrWindow(() => ParentWindowProvider?.Invoke() ?? IntPtr.Zero)
            .WithDefaultRedirectUri()
            .Build();
    }

    /// <summary>
    /// A delegated access token for <paramref name="audience"/> — an app/client
    /// ID GUID, an <c>api://…</c> identifier URI, or an already-qualified scope.
    /// Cached until shortly before expiry.
    /// </summary>
    /// <exception cref="EntraTokenException">
    /// Thrown when no token can be obtained without prompting.
    /// </exception>
    public async Task<string> GetTokenAsync(string audience, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(audience))
            throw new ArgumentException("An audience is required", nameof(audience));

        var scope = ToScope(audience);

        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(scope, out var hit) && DateTimeOffset.UtcNow < hit.Expiry)
                return hit.Token;

            var result = await AcquireAsync(scope, ct);

            // Refresh a little early so a token never expires mid-flight.
            var expiry = result.ExpiresOn.AddMinutes(-5);
            _cache[scope] = (result.AccessToken, expiry);
            return result.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drop cached tokens — used on sign-out and after a 401.</summary>
    public void Invalidate()
    {
        _gate.Wait();
        try { _cache.Clear(); }
        finally { _gate.Release(); }
    }

    private async Task<AuthenticationResult> AcquireAsync(string scope, CancellationToken ct)
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

        // 3. Interactive, but only where there is a window to parent it to.
        //    Headless callers (the CLI, scheduled runs) must fail loudly rather
        //    than block forever on a prompt nobody can answer.
        var parent = ParentWindowProvider?.Invoke() ?? IntPtr.Zero;
        if (parent == IntPtr.Zero)
        {
            throw new EntraTokenException(
                scope,
                "no cached credential and no window to prompt in — sign in to FleetMate, " +
                "or run on an Entra-joined device where the broker can use the device credential");
        }

        try
        {
            Log.Information("[entra] Silent acquisition failed for {Scope}; prompting via the broker", scope);
            return await _app
                .AcquireTokenInteractive(scopes)
                .WithAccount(PublicClientApplication.OperatingSystemAccount)
                .ExecuteAsync(ct);
        }
        catch (MsalException ex)
        {
            throw new EntraTokenException(scope, ex.Message, ex);
        }
    }

    /// <summary>
    /// Turn an audience into a scope. Callers configure audiences (a GUID or an
    /// <c>api://…</c> URI); Entra wants a scope, and for a resource-wide
    /// delegated token that is <c>{audience}/.default</c>.
    /// </summary>
    internal static string ToScope(string audience)
    {
        var trimmed = audience.Trim();

        // Already a scope — either .default or a named permission.
        if (trimmed.EndsWith("/.default", StringComparison.OrdinalIgnoreCase))
            return trimmed;

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
