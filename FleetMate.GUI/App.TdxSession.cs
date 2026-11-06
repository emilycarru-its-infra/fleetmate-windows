using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Threading;
using FleetMate.Core.Models;
using FleetMate.Core.Services.Tickets;
using FleetMate.GUI.Views.Tickets;
using Microsoft.Win32;
using Serilog;

namespace FleetMate.GUI;

/// <summary>
/// Keeps the window's TeamDynamix sign-in alive. The rules (when to renew,
/// the backoff, adopting a saved session, opening the sign-in window, what
/// to show) are <see cref="TdxSessionPolicy"/>'s; this is the wiring.
///
/// Every 15 seconds, and at the moment the token expires, the window picks
/// up a session another process saved, brings the Authentication panel and
/// Tickets in line with the token actually held, and renews when the policy
/// says so. Renewal is always silent first: the saved session, then HTTP
/// Negotiate with the device sign-in, then the hidden WebView2. Waking from
/// sleep, unlocking the session and the network changing retry straight
/// away. Only when all of that has failed with the token gone or nearly gone
/// does the sign-in window open by itself, and never more than once per
/// cool-down.
/// </summary>
public partial class App
{
    private static readonly TimeSpan TdxKeeperInterval = TimeSpan.FromSeconds(15);

    private TdxSessionPolicy _tdxPolicy = new();
    private DispatcherTimer? _tdxKeeperTimer;
    private DispatcherTimer? _tdxKickTimer;
    private bool _tdxKicked;
    private bool _tdxTickRunning;
    private bool _tdxSystemHooks;
    private bool _tdxLastSignedIn;
    private volatile bool _tdxSessionLocked;
    private TdxService? _tdxWiredService;
    private CancellationTokenSource? _tdxWindowCts;
    private Task<bool>? _tdxWindow;

    /// <summary>
    /// True when silent renewal keeps failing with no usable token left, or
    /// the sign-in was refused for being the wrong account: Tickets shows its
    /// sign-in banner.
    /// </summary>
    public bool TdxNeedsAttention { get; private set; }

    /// <summary>Why sign-in needs attention, when there is more to say than that it expired.</summary>
    public string? TdxAttentionReason { get; private set; }

    /// <summary>
    /// Raised on the UI thread when the token held lapses or returns, or
    /// <see cref="TdxNeedsAttention"/> changes. Tickets redraws its Sign In
    /// button, banner and empty state from it.
    /// </summary>
    public event Action? TdxSessionStatusChanged;

    /// <summary>
    /// Start keeping the sign-in alive in this window, or re-wire it after
    /// the services were rebuilt. Safe to call more than once.
    /// </summary>
    private void StartTdxSessionKeeper()
    {
        if (_isInboxAgent)
        {
            // The helper renews on its own schedule, but still takes a newer
            // session the window or the other edition saved.
            if (TdxService is { } helperTdx && Config.Tdx is { SsoEnabled: true })
            {
                helperTdx.SharedSession = TdxSessionStore;
                helperTdx.PeerSessions = new[] { TdxSessionCache.OtherEdition() };
            }
            return;
        }
        WireTdxService();
        if (TdxService == null || Config.Tdx is not { SsoEnabled: true }) return;

        if (!_tdxSystemHooks)
        {
            _tdxSystemHooks = true;
            SystemEvents.PowerModeChanged += OnTdxPowerModeChanged;
            SystemEvents.SessionSwitch += OnTdxSessionSwitch;
            NetworkChange.NetworkAvailabilityChanged += OnTdxNetworkAvailabilityChanged;
            NetworkChange.NetworkAddressChanged += OnTdxNetworkAddressChanged;
        }

        if (_tdxKeeperTimer == null)
        {
            _tdxKeeperTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TdxKeeperInterval };
            _tdxKeeperTimer.Tick += async (_, _) => await TdxKeeperTickAsync();
            _tdxKeeperTimer.Start();
        }
        if (_tdxKickTimer == null)
        {
            _tdxKickTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TdxSessionPolicy.KickDebounce };
            _tdxKickTimer.Tick += async (_, _) =>
            {
                _tdxKickTimer.Stop();
                await TdxKeeperTickAsync();
            };
        }
    }

    /// <summary>Unhook everything the keeper hooked. Called on exit.</summary>
    private void StopTdxSessionKeeper()
    {
        if (_tdxSystemHooks)
        {
            _tdxSystemHooks = false;
            SystemEvents.PowerModeChanged -= OnTdxPowerModeChanged;
            SystemEvents.SessionSwitch -= OnTdxSessionSwitch;
            NetworkChange.NetworkAvailabilityChanged -= OnTdxNetworkAvailabilityChanged;
            NetworkChange.NetworkAddressChanged -= OnTdxNetworkAddressChanged;
        }
        _tdxKeeperTimer?.Stop();
        _tdxKickTimer?.Stop();
        _tdxWindowCts?.Cancel();
        UnwireTdxService();
    }

    /// <summary>Point the current TeamDynamix service at the saved sessions and this window's sign-in chain.</summary>
    private void WireTdxService()
    {
        if (ReferenceEquals(_tdxWiredService, TdxService)) return;
        UnwireTdxService();
        _tdxPolicy = new TdxSessionPolicy();
        _tdxLastSignedIn = false;
        if (TdxService is not { } tdx || Config.Tdx is not { SsoEnabled: true }) return;

        tdx.SharedSession = TdxSessionStore;
        tdx.PeerSessions = new[] { TdxSessionCache.OtherEdition() };
        tdx.Renewer = urgent => Dispatcher.InvokeAsync(() => RenewTdxOnDemandAsync(urgent)).Task.Unwrap();
        tdx.SharedSessionAdopted += OnTdxSharedSessionAdopted;
        tdx.TokenRejected += OnTdxTokenRejected;
        _tdxWiredService = tdx;
    }

    private void UnwireTdxService()
    {
        if (_tdxWiredService is not { } tdx) return;
        tdx.SharedSessionAdopted -= OnTdxSharedSessionAdopted;
        tdx.TokenRejected -= OnTdxTokenRejected;
        tdx.Renewer = null;
        _tdxWiredService = null;
    }

    /// <summary>The expiry of the token held, or null when there is none.</summary>
    private DateTime? HeldTdxExpiry =>
        TdxService is { CurrentSsoToken: not null } tdx ? tdx.CurrentSsoExpiry : null;

    // MARK: - The loop

    private async Task TdxKeeperTickAsync()
    {
        if (_tdxTickRunning || TdxService is not { } tdx || Config.Tdx is not { SsoEnabled: true }) return;
        _tdxTickRunning = true;
        try
        {
            var kicked = _tdxKicked;
            _tdxKicked = false;

            // Pick up another process's renewal before deciding to renew.
            await tdx.ReloadSharedSessionAsync(force: kicked);
            RefreshTdxSessionStatus();

            if (_tdxSsoInFlight is { IsCompleted: false } || _tdxRetry is { IsCompleted: false }) return;
            // The wrong account is final until someone clicks Sign In.
            if (_lastSilentTdxResult is { IdentityRefused: true }) return;
            if (!_tdxPolicy.ShouldAttempt(HeldTdxExpiry, kicked)) return;

            Log.Information("[tdx-sso] Renewal due (failures={Failures}, kicked={Kicked}, expires {Expiry:u})",
                _tdxPolicy.Failures, kicked, HeldTdxExpiry);
            await RenewTdxSilentlyAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[tdx-sso] Renewal check failed");
        }
        finally
        {
            _tdxTickRunning = false;
            ScheduleTdxKeeperTick();
        }
    }

    /// <summary>The next check runs in 15 seconds, or just after the token expires if that is sooner.</summary>
    private void ScheduleTdxKeeperTick()
    {
        if (_tdxKeeperTimer == null) return;
        var interval = TdxKeeperInterval;
        if (HeldTdxExpiry is { } expiry)
        {
            var untilExpiry = expiry - DateTime.UtcNow + TimeSpan.FromSeconds(1);
            if (untilExpiry > TimeSpan.Zero && untilExpiry < interval) interval = untilExpiry;
        }
        if (_tdxKeeperTimer.Interval != interval) _tdxKeeperTimer.Interval = interval;
    }

    /// <summary>
    /// One silent renewal, in order: a fresher saved session, then the chain
    /// (HTTP Negotiate, then the hidden WebView2). A failure while the token is
    /// still valid keeps it. When everything failed and the token is gone or
    /// nearly gone, the sign-in window may open by itself.
    /// </summary>
    private async Task RenewTdxSilentlyAsync()
    {
        if (TdxService is not { } tdx) return;
        if (await tdx.ReloadSharedSessionAsync(force: true)) return;

        _lastSilentTdxResult = null;
        await AttemptSilentTdxSsoAsync(renew: IsTdxSsoAuthenticated);
        if (_tdxPolicy.IsUsable(HeldTdxExpiry) && _tdxPolicy.Failures == 0) return;

        var silentFailed = _lastSilentTdxResult is { Success: false } last && TdxSsoResult.ShouldOfferWindow(last);
        // Nobody can answer a window on a locked session, and with no network
        // it would only show an error page; the next kick tries again.
        if (_tdxSessionLocked || !NetworkInterface.GetIsNetworkAvailable()) return;
        if (_tdxPolicy.ShouldAutoOpenWindow(HeldTdxExpiry, silentFailed,
                identityRefused: _lastSilentTdxResult is { IdentityRefused: true },
                windowOpen: _tdxWindow is { IsCompleted: false }))
        {
            Log.Information("[tdx-sso] Every silent sign-in failed and the token is gone; opening the sign-in window");
            _ = OpenTdxSignInWindowAsync(automatic: true);
        }
    }

    /// <summary>
    /// <see cref="TdxService.Renewer"/>: a request found no usable token, or got
    /// a 401 (<paramref name="urgent"/>). Joins the attempt running, or runs one
    /// when the policy allows; reports whether a usable token is held.
    /// </summary>
    private async Task<bool> RenewTdxOnDemandAsync(bool urgent)
    {
        if (TdxService is not { } tdx) return false;
        try
        {
            if (_tdxSsoInFlight is { IsCompleted: false } running) await running;
            else if (_tdxPolicy.ShouldAttempt(HeldTdxExpiry, kicked: urgent)) await RenewTdxSilentlyAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[tdx-sso] On-demand renewal failed");
        }
        return tdx.CurrentSsoToken != null;
    }

    /// <summary>
    /// Bring the Authentication panel and Tickets in line with the token held:
    /// a probe's Valid does not outlive the token it checked, nor a 401.
    /// </summary>
    internal void RefreshTdxSessionStatus()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshTdxSessionStatus);
            return;
        }
        if (TdxService is not { } tdx || _isInboxAgent) return;

        var token = tdx.CurrentSsoToken;
        var expiry = HeldTdxExpiry;
        if (AuthManager != null && AuthManager.Systems.TryGetValue(AuthSystemId.Tdx, out var status))
        {
            var effective = _tdxPolicy.EffectiveState(status.State, token != null, expiry);
            if (!ReferenceEquals(effective, status.State))
            {
                Log.Information("[tdx-sso] TeamDynamix token lapsed or was refused; marking it expired");
                AuthManager.Update(AuthSystemId.Tdx, effective);
            }
        }

        var refused = _lastSilentTdxResult is { IdentityRefused: true };
        var needs = _tdxPolicy.NeedsAttention(expiry, token != null, refused);
        var reason = refused ? _lastSilentTdxResult?.Error : null;
        var signedIn = token != null;
        if (needs == TdxNeedsAttention && reason == TdxAttentionReason && signedIn == _tdxLastSignedIn) return;

        TdxNeedsAttention = needs;
        TdxAttentionReason = reason;
        _tdxLastSignedIn = signedIn;
        TdxSessionStatusChanged?.Invoke();
    }

    // MARK: - Events from the service

    /// <summary>Another process saved a newer session and the service took it.</summary>
    private void OnTdxSharedSessionAdopted(TdxSession session)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnTdxSharedSessionAdopted(session));
            return;
        }
        var wasSignedIn = _tdxLastSignedIn;
        _tdxPolicy.RecordAdopted();
        _lastSilentTdxResult = null;
        AuthManager.Update(AuthSystemId.Tdx, AuthTokenState.Valid(session.UserName, session.ExpiryUtc));
        // A token arrived; the sign-in window has nothing left to do.
        _tdxWindowCts?.Cancel();
        RefreshTdxSessionStatus();
        if (!wasSignedIn)
        {
            _ticketsCacheTime = null;
            NotifyTdxSignInChanged();
        }
    }

    /// <summary>TeamDynamix refused the token held.</summary>
    private void OnTdxTokenRejected()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnTdxTokenRejected);
            return;
        }
        _tdxPolicy.RecordRejected();
        RefreshTdxSessionStatus();
        // A request that may be re-sent recovers on its own; a workflow or
        // task call is not re-sent, so renew for the next one.
        KickTdxRenewal("a refused token");
    }

    // MARK: - System events

    private void OnTdxPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) KickTdxRenewal("resume from sleep");
    }

    private void OnTdxSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock) _tdxSessionLocked = true;
        else if (e.Reason is SessionSwitchReason.SessionUnlock) _tdxSessionLocked = false;
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon
            or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
            KickTdxRenewal("session unlock");
    }

    private void OnTdxNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) KickTdxRenewal("network available");
    }

    private void OnTdxNetworkAddressChanged(object? sender, EventArgs e) => KickTdxRenewal("network change");

    /// <summary>Check again shortly; events arriving together are one check.</summary>
    private void KickTdxRenewal(string why)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => KickTdxRenewal(why));
            return;
        }
        if (_tdxKickTimer == null) return;
        Log.Debug("[tdx-sso] Renewal check after {Why}", why);
        _tdxKicked = true;
        _tdxKickTimer.Stop();
        _tdxKickTimer.Start();
    }

    // MARK: - The sign-in window

    /// <summary>
    /// Show the sign-in window, or the one already open. It never blocks the
    /// UI or silent renewal, closes itself when a token arrives by any route,
    /// and times out on its own. <paramref name="automatic"/> opens it without
    /// taking focus.
    /// </summary>
    private Task<bool> OpenTdxSignInWindowAsync(bool automatic)
    {
        if (_tdxWindow is { IsCompleted: false } open) return open;
        return _tdxWindow = RunTdxSignInWindowAsync(automatic);
    }

    private async Task<bool> RunTdxSignInWindowAsync(bool automatic)
    {
        if (Config.Tdx is not { SsoEnabled: true } tdx || string.IsNullOrEmpty(tdx.BaseUrl)) return false;

        using var cts = new CancellationTokenSource();
        _tdxWindowCts = cts;
        TdxSsoResult? result;
        try
        {
            var owner = Current.Windows.OfType<Views.Shared.MainWindow>().FirstOrDefault();
            result = await TdxSignInWindow.SignInAsync(tdx.BaseUrl, owner, TdxSessionPolicy.WindowTimeout,
                activate: !automatic, cts.Token);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[tdx-sso] The sign-in window failed");
            result = TdxSsoResult.Failed(ex.Message);
        }
        finally
        {
            _tdxWindowCts = null;
        }

        if (result is { Success: true, Token: not null })
        {
            HandleSilentSsoSuccess(result);
            _tdxPolicy.RecordWindowClosed(signedIn: true);
            RefreshTdxSessionStatus();
            return true;
        }

        // A silent renewal or another process may have signed in meanwhile;
        // a late failure from the window must not overwrite that.
        var signedIn = IsTdxSsoAuthenticated;
        _tdxPolicy.RecordWindowClosed(signedIn);
        if (!signedIn && result != null)
        {
            if (result.IdentityRefused)
            {
                TdxSessionStore.Clear();
                _lastSilentTdxResult = result;
            }
            AuthManager.Update(AuthSystemId.Tdx, AuthTokenState.Failed(result.Error ?? "TeamDynamix sign-in did not finish"));
        }
        RefreshTdxSessionStatus();
        return signedIn;
    }
}
