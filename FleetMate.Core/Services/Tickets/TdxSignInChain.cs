using FleetMate.Core.Models.Tickets;
using Serilog;

namespace FleetMate.Core.Services.Tickets;

/// <summary>
/// The first, browser-free phase of the TeamDynamix sign-in as the Windows
/// user, shared by every window: find the Windows work account, then HTTP
/// with the Windows credentials (Negotiate/Kerberos) follows the Shibboleth →
/// Entra chain to <c>/api/auth/loginsso</c> for a JWT that must be that
/// account's. A window adds its hidden-browser phase after this one when the
/// result neither succeeded nor was refused.
/// </summary>
public static class TdxSignInChain
{
    /// <summary>
    /// Run the account lookup and the HTTP phase. <c>Upn</c> is null when the
    /// Windows account's address could not be found; the result is then a
    /// refusal and no further phase may run, since nothing could check it.
    /// </summary>
    /// <param name="step">Told as each step starts, for the launch progress bar.</param>
    public static async Task<(string? Upn, TdxSsoResult Result)> HttpPhaseAsync(string baseUrl,
        Action<TdxSignInStep>? step = null, CancellationToken ct = default)
    {
        step?.Invoke(TdxSignInStep.FindingAccount);
        var upn = await TdxSsoIdentity.ResolveWindowsUpnAsync(ct);
        if (upn == null)
        {
            Log.Error("[tdx-sso] No Windows work-account address from the logon, the sign-in broker or the registry; the session's account cannot be checked, sign-in refused");
            return (null, TdxSsoResult.Refused(TdxSsoResult.UnknownExpectedAddressReason));
        }
        Log.Information("[tdx-sso] Signing in as the Windows account {Upn}", upn);
        // The account came from the logon or the Microsoft sign-in broker;
        // the next step reaches Microsoft sign-in for the TeamDynamix session.
        step?.Invoke(TdxSignInStep.ConnectingToMicrosoft);

        Log.Information("[tdx-sso] Phase 1: silent HTTP SSO (Negotiate/Kerberos)");
        step?.Invoke(TdxSignInStep.SigningInToTeamDynamix);
        var http = await Task.Run(() => new TdxSsoService(baseUrl).TrySilentSsoAsync(ct, upn), ct);
        return (upn, http);
    }

    /// <summary>True when the HTTP phase settled the sign-in, either way, and no browser phase should follow.</summary>
    public static bool IsFinal(TdxSsoResult result) =>
        result is { Success: true, Token: not null } or { IdentityRefused: true };
}
