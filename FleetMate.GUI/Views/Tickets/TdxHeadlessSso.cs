using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Tickets;
using FleetMate.GUI.Views.Shared;
using FleetMate.Core.Services.SignIn;
using Serilog;

namespace FleetMate.GUI.Views.Tickets;

/// <summary>
/// TeamDynamix sign-in as the Windows user, with nothing on screen: the WPF
/// app's hidden host for <see cref="TdxBrowserSignIn"/>, which holds the
/// chain itself and is shared with the WinUI app.
/// </summary>
internal static class TdxHeadlessSso
{
    /// <summary>Run both phases (see <see cref="TdxBrowserSignIn"/>). Call on the UI thread.</summary>
    /// <param name="step">Told as each step starts, for the launch progress bar.</param>
    public static Task<TdxSsoResult> SignInAsync(string baseUrl, CancellationToken ct = default,
        Action<TdxSignInStep>? step = null) =>
        TdxBrowserSignIn.SignInAsync(baseUrl, async () => await HiddenWebView.CreateAsync(), step, ct);
}
