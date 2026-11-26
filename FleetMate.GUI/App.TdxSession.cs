using FleetMate.Core.Models;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using FleetMate.GUI.Views.Tickets;
using Serilog;

namespace FleetMate.GUI;

/// <summary>
/// Keeps the TeamDynamix sign-in alive, in the window and in the background
/// inbox helper alike. The sign-in, the saved session, the renewal keeper and
/// the sign-in window's lifecycle are <see cref="TdxSessionCoordinator"/>'s,
/// shared with the WinUI app; the rules are <see cref="TdxSessionPolicy"/>'s.
/// This file connects them to this window: the hidden WebView2 chain
/// (<see cref="TdxHeadlessSso"/>), the sign-in window, the ticket cache and
/// the events views already listen to.
/// </summary>
public partial class App
{
    private TdxSessionCoordinator? _tdxSession;
    private TdxService? _tdxSessionService;

    /// <summary>The TeamDynamix sign-in in flight, or a completed task when there is none.</summary>
    public Task TdxSignInSettled => _tdxSession?.Settled ?? Task.CompletedTask;

    /// <summary>
    /// Sign in to TeamDynamix as the Windows user with nothing on screen: HTTP
    /// Negotiate first, then a never-shown WebView2. Runs on startup and inside
    /// every Sign In button; on its own it never shows anything. Concurrent
    /// callers share one attempt.
    /// </summary>
    public Task AttemptSilentTdxSsoAsync() => _tdxSession?.AttemptSilentAsync() ?? Task.CompletedTask;

    /// <summary>
    /// The Sign In buttons' action: retry the silent sign-in, and when that
    /// cannot finish (Entra wants multi-factor authentication) open the sign-in
    /// window. True when signed in afterwards.
    /// </summary>
    public Task<bool> RetryTdxSsoAsync() => _tdxSession?.RetryAsync() ?? Task.FromResult(false);

    /// <summary>
    /// Where the launch (or retry) sign-in has got to, for the progress over an
    /// empty ticket list. Null when nothing is pending.
    /// </summary>
    public TdxSignInStep? TdxLaunchStep => _tdxSession?.LaunchStep;

    /// <summary>Raised on the UI thread when <see cref="TdxLaunchStep"/> changes.</summary>
    public event Action? TdxLaunchStepChanged;

    /// <summary>The first ticket list arrived, or loading it failed: the launch progress ends.</summary>
    internal void SetTdxLaunchStep(TdxSignInStep? step)
    {
        if (step == null) _tdxSession?.TicketsLoaded();
    }

    /// <summary>True when the Sign In banner should show: the token lapsed or renewal keeps failing.</summary>
    public bool TdxNeedsAttention => _tdxSession?.NeedsAttention ?? false;

    /// <summary>Why, when the account was refused; null otherwise.</summary>
    public string? TdxAttentionReason => _tdxSession?.AttentionReason;

    /// <summary>Raised on the UI thread when attention, its reason or the token changes.</summary>
    public event Action? TdxSessionStatusChanged;

    internal void RefreshTdxSessionStatus() => _tdxSession?.RefreshStatus();

    /// <summary>
    /// Build the coordinator for the current TeamDynamix service and start its
    /// keeper. Called at launch and after Settings reloads the services; a
    /// coordinator for a service that has gone is disposed first.
    /// </summary>
    private void StartTdxSessionKeeper()
    {
        if (_tdxSession != null && ReferenceEquals(_tdxSessionService, TdxService)) return;
        StopTdxSessionKeeper();
        if (TdxService == null || Config.Tdx is not { SsoEnabled: true }) return;

        // The coordinator raises its events on the context it is built on, and
        // views handle them without marshalling: make sure that is this dispatcher.
        if (SynchronizationContext.Current is not System.Windows.Threading.DispatcherSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(Dispatcher));

        var session = new TdxSessionCoordinator(Config.Tdx, TdxService, AuthManager,
            (baseUrl, step, ct) => TdxHeadlessSso.SignInAsync(baseUrl, ct, step))
        {
            // The inbox helper renews the session but has no window to report
            // to or to open a sign-in window over.
            ReportsStatus = !_isInboxAgent,
            InteractiveSignIn = _isInboxAgent ? null : OpenTdxSignInWindowAsync,
            IdentityChanged = () => TicketWorkspaceIdentityChanged,
            HasTickets = () => IsTicketsCacheValid,
        };
        session.SignInChanged += () =>
        {
            _ticketsCacheTime = null;
            NotifyTdxSignInChanged();
        };
        session.StatusChanged += () => TdxSessionStatusChanged?.Invoke();
        session.LaunchStepChanged += () => TdxLaunchStepChanged?.Invoke();
        _tdxSession = session;
        _tdxSessionService = TdxService;
        session.Start();
    }

    private void StopTdxSessionKeeper()
    {
        _tdxSession?.Dispose();
        _tdxSession = null;
        _tdxSessionService = null;
    }

    private Task<TdxSsoResult?> OpenTdxSignInWindowAsync(CancellationToken ct)
    {
        if (Config.Tdx is not { SsoEnabled: true } tdx || string.IsNullOrEmpty(tdx.BaseUrl))
            return Task.FromResult<TdxSsoResult?>(null);
        var owner = Current.Windows.OfType<Views.Shared.MainWindow>().FirstOrDefault();
        Log.Information("[tdx-sso] Sign In needs the sign-in window");
        return TdxSignInWindow.SignInAsync(tdx.BaseUrl, owner, TdxSessionPolicy.WindowTimeout, ct);
    }
}
