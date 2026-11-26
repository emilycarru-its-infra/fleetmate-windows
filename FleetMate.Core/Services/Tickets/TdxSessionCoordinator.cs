using System.Net.NetworkInformation;
using FleetMate.Core.Config;
using FleetMate.Core.Models;
using FleetMate.Core.Models.Tickets;
using Serilog;

namespace FleetMate.Core.Services.Tickets;

/// <summary>
/// A window's TeamDynamix sign-in, free of any UI framework: the silent
/// sign-in at launch, reuse of the session saved by this app, the other
/// edition or the inbox helper, and the keeper that renews it before it
/// expires and after the network comes back. It never shows a window; the
/// sign-in chain it runs is the caller's (<see cref="TdxSignInChain"/> and, in
/// a window with WebView2, a hidden-browser phase after it).
///
/// Create it on the UI thread: its events are raised on the
/// <see cref="SynchronizationContext"/> current at construction, and its
/// keeper loop runs there. The rules — when to renew, back off, accept or
/// adopt a session — are <see cref="TdxSessionPolicy"/>'s; one process signs
/// in at a time through <see cref="TdxRenewalLock"/>.
/// </summary>
public sealed class TdxSessionCoordinator : IDisposable
{
    /// <summary>Runs the sign-in chain for a base URL; the step callback feeds the launch progress.</summary>
    public delegate Task<TdxSsoResult> SignInChain(string baseUrl, Action<TdxSignInStep>? step, CancellationToken ct);

    private static readonly TimeSpan KeeperInterval = TimeSpan.FromSeconds(15);

    private readonly TdxConfig? _config;
    private readonly TdxService? _service;
    private readonly AuthManager _auth;
    private readonly SignInChain _signIn;
    private readonly SynchronizationContext? _ui;
    private readonly TdxRenewalLock _lock;
    private readonly TdxSessionPolicy _policy;
    private readonly CancellationTokenSource _stop = new();

    private Task? _inFlight;
    private TdxSsoResult? _last;
    private TaskCompletionSource? _kick;
    private bool _kicked;
    private bool _started;
    private bool _lastSignedIn;

    /// <param name="signIn">The chain to run; <see cref="HttpOnly"/> when there is no browser phase.</param>
    /// <param name="store">This edition's saved session; the default file when null.</param>
    /// <param name="peers">Other sessions to adopt from; the other edition's when null.</param>
    public TdxSessionCoordinator(TdxConfig? config, TdxService? service, AuthManager auth, SignInChain signIn,
        TdxSessionCache? store = null, IReadOnlyList<TdxSessionCache>? peers = null,
        TdxRenewalLock? renewalLock = null, TdxSessionPolicy? policy = null)
    {
        _config = config;
        _service = service;
        _auth = auth;
        _signIn = signIn;
        _ui = SynchronizationContext.Current;
        _lock = renewalLock ?? new TdxRenewalLock();
        _policy = policy ?? new TdxSessionPolicy();
        Store = store ?? new TdxSessionCache();

        if (IsEnabled && _service is { } tdx)
        {
            tdx.SharedSession = Store;
            tdx.PeerSessions = peers ?? new[] { TdxSessionCache.OtherEdition() };
            tdx.Renewer = RenewOnDemandAsync;
            tdx.SharedSessionAdopted += OnSharedSessionAdopted;
            tdx.TokenRejected += OnTokenRejected;
        }
    }

    /// <summary>The HTTP phase alone, for a window that has no hidden browser.</summary>
    public static async Task<TdxSsoResult> HttpOnly(string baseUrl, Action<TdxSignInStep>? step, CancellationToken ct) =>
        (await TdxSignInChain.HttpPhaseAsync(baseUrl, step, ct)).Result;

    /// <summary>The saved TeamDynamix session, encrypted for this Windows user.</summary>
    public TdxSessionCache Store { get; }

    /// <summary>TeamDynamix is configured with single sign-on.</summary>
    public bool IsEnabled => _service != null && _config is { SsoEnabled: true } && !string.IsNullOrEmpty(_config.BaseUrl);

    public bool IsSignedIn => _service?.IsSsoAuthenticated ?? false;

    public string? SignedInName => _service?.AuthenticatedUserName;

    /// <summary>
    /// The Sign In banner shows: the token lapsed, renewals keep failing, or
    /// the session was refused as another account's.
    /// </summary>
    public bool NeedsAttention { get; private set; }

    /// <summary>Why, when the account was refused; null otherwise.</summary>
    public string? AttentionReason { get; private set; }

    /// <summary>The last silent attempt's failure, for the banner's detail.</summary>
    public string? LastError => _last is { Success: false } failed ? failed.Error : null;

    /// <summary>
    /// The Sign In banner shows: TeamDynamix single sign-on is configured and
    /// either the session needs attention or the silent sign-in finished
    /// without signing in. Never while the first attempt is still running.
    /// </summary>
    public bool ShowsSignIn => IsEnabled && !IsSigningIn && (NeedsAttention || !IsSignedIn) && _inFlight != null;

    /// <summary>A sign-in is running.</summary>
    public bool IsSigningIn => _inFlight is { IsCompleted: false };

    /// <summary>What the Sign In banner says.</summary>
    public string SignInMessage => AttentionReason
        ?? (IsSignedIn
            ? "Your TeamDynamix session is about to expire and signing in again in the background did not work. Tickets will not update until you sign in."
            : LastError is { } error
                ? $"Signing in as your Windows account did not work: {error}"
                : "Your TeamDynamix session has expired and signing in again in the background did not work. Tickets will not update until you sign in.");

    /// <summary>Where the launch sign-in has got to, or null when nothing is pending.</summary>
    public TdxSignInStep? LaunchStep { get; private set; }

    /// <summary>Signed in or out: a page showing TeamDynamix data reloads.</summary>
    public event Action? SignInChanged;

    /// <summary><see cref="NeedsAttention"/>, <see cref="AttentionReason"/> or <see cref="IsSignedIn"/> changed.</summary>
    public event Action? StatusChanged;

    public event Action? LaunchStepChanged;

    /// <summary>The sign-in in flight, or a completed task.</summary>
    public Task Settled => _inFlight ?? Task.CompletedTask;

    // MARK: - Sign-in

    /// <summary>
    /// Sign in silently; concurrent callers share one attempt.
    /// <paramref name="renew"/> signs in again while signed in, without reusing
    /// the saved session, as the keeper does two hours before expiry.
    /// </summary>
    public Task AttemptSilentAsync(bool renew = false)
    {
        if (_inFlight is { IsCompleted: false } running) return running;
        return _inFlight = RunSilentAsync(renew);
    }

    /// <summary>
    /// The Sign In button: forget a refusal and try the silent sign-in again.
    /// True when signed in afterwards.
    /// </summary>
    public async Task<bool> RetryAsync()
    {
        _last = null;
        try { await AttemptSilentAsync(); }
        catch (Exception ex) { Log.Warning(ex, "[tdx-sso] Silent SSO retry failed"); }
        RefreshStatus();
        return IsSignedIn;
    }

    private async Task RunSilentAsync(bool renew)
    {
        var report = !renew && !IsSignedIn && IsEnabled;
        Action<TdxSignInStep>? step = report ? s => SetLaunchStep(s) : null;
        var attempted = IsEnabled && (renew || !IsSignedIn);
        var wasSignedIn = IsSignedIn;
        var expiryBefore = _service?.CurrentSsoExpiry;
        using var signingIn = TdxService.BeginSignIn();
        step?.Invoke(TdxSignInStep.FindingAccount);
        try
        {
            await RunSilentCoreAsync(renew, step);
        }
        finally
        {
            if (report) SetLaunchStep(IsSignedIn ? TdxSignInStep.LoadingTickets : null);
            if (attempted)
            {
                var renewed = IsSignedIn && (!wasSignedIn || _service?.CurrentSsoExpiry > expiryBefore);
                if (renewed) _policy.RecordSuccess();
                else _policy.RecordFailure();
                if (!renewed)
                    Log.Information("[tdx-sso] Silent sign-in did not renew ({Failures} in a row); next try in {Delay}",
                        _policy.Failures, TdxSessionPolicy.RetryDelay(_policy.Failures));
                RefreshStatus();
            }
            // An attempt that changed nothing still settles the Sign In banner.
            RaiseStatusChanged();
        }
    }

    private void RaiseStatusChanged()
    {
        if (OnUi(RaiseStatusChanged)) StatusChanged?.Invoke();
    }

    private async Task RunSilentCoreAsync(bool renew, Action<TdxSignInStep>? step)
    {
        if (!IsEnabled || _service is not { } tdx || _config is not { } config)
        {
            Log.Debug("[tdx-sso] TDX SSO not configured or not enabled");
            return;
        }
        if (IsSignedIn && !renew)
        {
            Log.Debug("[tdx-sso] Already authenticated, skipping silent SSO");
            return;
        }

        if (!renew && await TryRestoreAsync(step)) return;

        if (!renew) _auth.Update(AuthSystemId.Tdx, AuthTokenState.Authenticating());
        Log.Information("[tdx-sso] Starting silent SSO for {BaseUrl}", config.BaseUrl);

        TdxSsoResult? result = null;
        var turn = await _lock.RenewOrAdoptAsync(
            renew: async () =>
            {
                result = await _signIn(config.BaseUrl, step, _stop.Token);
                _last = result;
                if (result is not { Success: true, Token: not null }) return false;
                // Saved with the lock still held, so the next process to take it sees this session.
                Accept(result);
                return true;
            },
            adopt: () => tdx.ReloadSharedSessionAsync(force: true),
            wait: TdxSessionPolicy.RenewalLockWait);
        if (turn is TdxRenewalTurn.Renewed) return;
        if (turn is TdxRenewalTurn.Adopted)
        {
            Log.Information("[tdx-sso] Took the session another process just saved");
            return;
        }
        if (turn is TdxRenewalTurn.OtherFailed || result == null)
        {
            result = TdxSsoResult.Failed("Another app was signing in to TeamDynamix and did not finish");
            _last = result;
        }

        if (result.IdentityRefused) Store.Clear();

        if (renew && IsSignedIn)
        {
            Log.Warning("[tdx-sso] Renewal failed; keeping the current sign-in: {Reason}", result.Error);
            return;
        }

        Log.Warning("[tdx-sso] Silent SSO failed; no sign-in window is shown: {Reason}", result.Error);
        _auth.Update(AuthSystemId.Tdx, AuthTokenState.Failed(result.Error ?? "Silent Windows SSO unavailable"));
    }

    /// <summary>
    /// Reuse the saved sign-in when it has more than an hour left and passes
    /// the identity check, then confirm it once by finding the signed-in person.
    /// </summary>
    private async Task<bool> TryRestoreAsync(Action<TdxSignInStep>? step)
    {
        if (_service is not { } tdx || Store.Load() is not { } session || !session.IsFresh(DateTime.UtcNow))
            return false;

        var expected = await TdxSsoIdentity.ResolveWindowsUpnAsync();
        var verified = TdxSsoIdentity.Verify(session.Token, expected);
        if (!session.BelongsTo(expected, _config?.BaseUrl) || !verified.Success)
        {
            Log.Information("[tdx-sso] Saved session is not this account's or tenant's; discarding it");
            Store.Clear();
            return false;
        }

        step?.Invoke(TdxSignInStep.SigningInToTeamDynamix);
        tdx.SetSsoToken(session.Token, session.ExpiryUtc, verified.UserEmail, session.UserName ?? verified.UserName);
        if (session.VerifiedMe is { } cachedMe) tdx.RememberMe(cachedMe);
        try
        {
            if (await tdx.ProbeMeAsync() is { } me) Store.SaveMe(session.Token, me);
        }
        catch (TdxRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            Log.Information("[tdx-sso] Saved session refused by TeamDynamix; signing in again");
            Store.Clear();
            tdx.SetSsoToken("", DateTime.MinValue);
            return false;
        }
        catch (TdxRequestException ex)
        {
            Log.Warning("[tdx-sso] Could not check the saved session; using it: {Reason}", ex.Message);
        }

        _auth.Update(AuthSystemId.Tdx, AuthTokenState.Valid(session.UserName, session.ExpiryUtc));
        Log.Information("[tdx-sso] Reused the saved session for {UserName}", session.UserName ?? "(unknown)");
        RaiseSignInChanged();
        return true;
    }

    private void Accept(TdxSsoResult result)
    {
        if (_service is not { } tdx) return;
        if (tdx.CurrentSsoToken != null && !_policy.ShouldAccept(result.Expiry, tdx.CurrentSsoExpiry))
        {
            Log.Information("[tdx-sso] Kept the session held; the new sign-in does not outlast it");
            return;
        }
        tdx.SetSsoToken(result.Token!, result.Expiry, result.UserEmail, result.UserName);
        Store.Save(new TdxSession
        {
            Token = result.Token!,
            ExpiryUtc = result.Expiry.ToUniversalTime(),
            UserEmail = result.UserEmail,
            UserName = result.UserName,
            VerifiedAddress = TdxSession.NormalizeAddress(result.UserEmail),
            BaseUrl = TdxSession.NormalizeBaseUrl(_config?.BaseUrl),
        });
        _ = SaveMeAsync(tdx, result.Token!);
        _auth.Update(AuthSystemId.Tdx, AuthTokenState.Valid(result.UserName, result.Expiry));
        Log.Information("[tdx-sso] Silent SSO successful — user={UserName}", result.UserName ?? "(unknown)");
        RaiseSignInChanged();
    }

    private async Task SaveMeAsync(TdxService tdx, string token)
    {
        try
        {
            if (await tdx.GetMeAsync() is { } me) Store.SaveMe(token, me);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[tdx-sso] Signed-in person not saved with the session");
        }
    }

    // MARK: - The keeper

    /// <summary>
    /// Keep the session renewed while the window is open: a check every 15
    /// seconds (sooner just before expiry), and one shortly after
    /// <see cref="Kick"/> or a network change. Call once, on the UI thread.
    /// </summary>
    public void Start()
    {
        if (_started || !IsEnabled) return;
        _started = true;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        _ = KeeperLoopAsync(_stop.Token);
    }

    /// <summary>Check the session soon: after resume from sleep, unlock, or a refused token.</summary>
    public void Kick(string why)
    {
        Log.Debug("[tdx-sso] Renewal check after {Why}", why);
        _kicked = true;
        _kick?.TrySetResult();
    }

    private async Task KeeperLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var kick = _kick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await Task.WhenAny(Task.Delay(NextInterval(), ct), kick.Task);
                // A burst of network events renews once.
                if (kick.Task.IsCompleted) await Task.Delay(TdxSessionPolicy.KickDebounce, ct);
            }
            catch (OperationCanceledException) { break; }
            await TickAsync();
        }
    }

    private TimeSpan NextInterval()
    {
        var interval = KeeperInterval;
        if (HeldExpiry is { } expiry)
        {
            var untilExpiry = expiry - DateTime.UtcNow + TimeSpan.FromSeconds(1);
            if (untilExpiry > TimeSpan.Zero && untilExpiry < interval) interval = untilExpiry;
        }
        return interval;
    }

    private DateTime? HeldExpiry => _service is { CurrentSsoToken: not null } tdx ? tdx.CurrentSsoExpiry : null;

    private async Task TickAsync()
    {
        if (_service is not { } tdx || !IsEnabled) return;
        try
        {
            var kicked = _kicked;
            _kicked = false;
            // Pick up another process's renewal before deciding to renew.
            await tdx.ReloadSharedSessionAsync(force: kicked);
            RefreshStatus();

            if (_inFlight is { IsCompleted: false }) return;
            // The wrong account is final until someone clicks Sign In.
            if (_last is { IdentityRefused: true }) return;
            if (!_policy.ShouldAttempt(HeldExpiry, kicked)) return;

            Log.Information("[tdx-sso] Renewal due (failures={Failures}, kicked={Kicked}, expires {Expiry:u})",
                _policy.Failures, kicked, HeldExpiry);
            await RenewSilentlyAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[tdx-sso] Renewal check failed");
        }
    }

    private async Task RenewSilentlyAsync()
    {
        if (_service is not { } tdx) return;
        if (await tdx.ReloadSharedSessionAsync(force: true)) return;
        _last = null;
        await AttemptSilentAsync(renew: IsSignedIn);
    }

    /// <summary>The service's own renewer: a request found the token lapsing or refused.</summary>
    private async Task<bool> RenewOnDemandAsync(bool urgent)
    {
        if (_service is not { } tdx) return false;
        try
        {
            await OnUiAsync(async () =>
            {
                if (_inFlight is { IsCompleted: false } running) await running;
                else if (_policy.ShouldAttempt(HeldExpiry, kicked: urgent)) await RenewSilentlyAsync();
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[tdx-sso] On-demand renewal failed");
        }
        return tdx.CurrentSsoToken != null;
    }

    /// <summary>Recompute <see cref="NeedsAttention"/> and mark a lapsed token expired.</summary>
    public void RefreshStatus()
    {
        if (!OnUi(RefreshStatus) || _service is not { } tdx) return;

        var token = tdx.CurrentSsoToken;
        var expiry = HeldExpiry;
        if (_auth.Systems.TryGetValue(AuthSystemId.Tdx, out var status))
        {
            var effective = _policy.EffectiveState(status.State, token != null, expiry);
            if (!ReferenceEquals(effective, status.State))
            {
                Log.Information("[tdx-sso] TeamDynamix token lapsed or was refused; marking it expired");
                _auth.Update(AuthSystemId.Tdx, effective);
            }
        }

        var refused = _last is { IdentityRefused: true };
        var needs = _policy.NeedsAttention(expiry, token != null, refused);
        var reason = refused ? _last?.Error : null;
        var signedIn = token != null;
        if (needs == NeedsAttention && reason == AttentionReason && signedIn == _lastSignedIn) return;

        NeedsAttention = needs;
        AttentionReason = reason;
        _lastSignedIn = signedIn;
        StatusChanged?.Invoke();
    }

    // MARK: - Events from the service and the system

    private void OnSharedSessionAdopted(TdxSession session)
    {
        if (!OnUi(() => OnSharedSessionAdopted(session))) return;
        var wasSignedIn = _lastSignedIn;
        _policy.RecordAdopted();
        _last = null;
        _auth.Update(AuthSystemId.Tdx, AuthTokenState.Valid(session.UserName, session.ExpiryUtc));
        RefreshStatus();
        if (!wasSignedIn) RaiseSignInChanged();
    }

    private void OnTokenRejected()
    {
        if (!OnUi(OnTokenRejected)) return;
        _policy.RecordRejected();
        RefreshStatus();
        // A workflow or task call is not re-sent, so renew for the next one.
        Kick("a refused token");
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) OnUi(() => Kick("network available"));
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => OnUi(() => Kick("network change"));

    private void SetLaunchStep(TdxSignInStep? step)
    {
        if (!OnUi(() => SetLaunchStep(step)) || LaunchStep == step) return;
        LaunchStep = step;
        LaunchStepChanged?.Invoke();
    }

    /// <summary>Clear the launch progress once the first ticket list arrives, or it fails.</summary>
    public void TicketsLoaded() => SetLaunchStep(null);

    private void RaiseSignInChanged()
    {
        if (OnUi(RaiseSignInChanged)) SignInChanged?.Invoke();
    }

    /// <summary>True when already on the UI thread; otherwise posts <paramref name="action"/> there and returns false.</summary>
    private bool OnUi(Action action)
    {
        if (_ui == null || SynchronizationContext.Current == _ui) return true;
        _ui.Post(_ => action(), null);
        return false;
    }

    private Task OnUiAsync(Func<Task> work)
    {
        if (_ui == null || SynchronizationContext.Current == _ui) return work();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(async _ =>
        {
            try { await work(); done.TrySetResult(); }
            catch (Exception ex) { done.TrySetException(ex); }
        }, null);
        return done.Task;
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (_started)
        {
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        }
        if (_service is { } tdx)
        {
            tdx.SharedSessionAdopted -= OnSharedSessionAdopted;
            tdx.TokenRejected -= OnTokenRejected;
            tdx.Renewer = null;
        }
        _stop.Dispose();
    }
}
