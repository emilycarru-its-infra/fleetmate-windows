using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Services.Tickets;
using Serilog;

namespace FleetMate.Core.Development;

/// <summary>
/// The silent Azure DevOps sign-in, in order: the broker redeeming the
/// device's primary refresh token, then az CLI and its token cache (and a
/// refresh token from this session), then the caller's hidden browser when it
/// has one. Nothing is ever shown. FleetMate.GUI passes its hidden WebView2;
/// FleetMate.WinUI has none yet and stops after the second phase.
/// </summary>
public static class DevOpsSignInChain
{
    public static async Task<DevOpsSsoResult> RunAsync(DevOpsSsoService sso, Func<Task<DevOpsSsoResult>>? hiddenBrowser)
    {
        // Phase 1: the broker, redeeming the device's primary refresh token.
        if (EntraTokenSource.Shared is { } broker)
        {
            try
            {
                var token = await broker.GetTokenAsync(DevOpsSsoService.AdoResourceId);
                var (name, email) = DevOpsSsoService.ExtractUserInfoFromJwt(token);
                Log.Information("[devops-sso] Phase 1: broker token acquired — user={UserName}", name ?? email ?? "(unknown)");
                return DevOpsSsoResult.Succeeded(token, TdxSsoService.ReadExpiry(token), userName: name, userEmail: email);
            }
            catch (Exception ex)
            {
                Log.Information("[devops-sso] Phase 1: broker had no token: {Reason}", ex.Message);
            }
        }

        // Phase 2: az CLI, then its MSAL cache, then a refresh token from this session.
        try
        {
            var result = await sso.RefreshAccessTokenAsync();
            if (result.Success && !string.IsNullOrEmpty(result.Token))
            {
                Log.Information("[devops-sso] Phase 2: token acquired — user={UserName}", result.UserName ?? "(unknown)");
                return result;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[devops-sso] Phase 2: token acquisition failed");
        }

        if (hiddenBrowser == null)
            return DevOpsSsoResult.Failed("Silent Windows SSO unavailable");

        // Phase 3: the hidden browser.
        Log.Information("[devops-sso] Phase 3: hidden browser SSO");
        var headless = await hiddenBrowser();
        if (headless is { Success: true, Token: not null })
            Log.Information("[devops-sso] Phase 3: token acquired — user={UserName}", headless.UserName ?? "(unknown)");
        return headless;
    }
}
