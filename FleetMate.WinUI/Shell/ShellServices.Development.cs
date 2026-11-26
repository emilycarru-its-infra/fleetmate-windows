using FleetMate.Core.Config;
using FleetMate.Core.Development;
using FleetMate.Core.Knowledge;
using FleetMate.Core.Links;
using FleetMate.Core.Models;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Services.Terminal;
using Microsoft.UI.Dispatching;
using Serilog;

namespace FleetMate.WinUI.Shell;

/// <summary>
/// The Development tab's services, following FleetMate.GUI's App: Azure DevOps
/// and its silent sign-in, the Pulls, Commits and Pipelines caches, pending
/// approvals, the GitHub inbox (polled from launch so the Inbox button is live),
/// the one refresh worker, and the shared Skills catalog. The refresh worker
/// and the inbox are the same FleetMate.Core classes the WPF app runs.
///
/// <code>
/// App.Services.CacheChanged += key => { if (key == "PullRequests") Render(); };
/// await App.Services.DevelopmentSync.RefreshNowAsync(DevelopmentRefreshArea.PullRequests);
/// </code>
/// </summary>
public sealed partial class ShellServices : IDevelopmentHost
{
    private DispatcherQueue? _ui;
    private readonly HashSet<string> _loadingCaches = new(StringComparer.Ordinal);
    private Task? _devOpsSsoInFlight;
    private DevelopmentDataSync? _developmentSync;

    /// <summary>Azure DevOps: null when no organization is configured.</summary>
    public AzureDevOpsService? DevOpsService { get; private set; }

    /// <summary>Azure DevOps sign-in: null when no client and tenant are configured.</summary>
    public DevOpsSsoService? DevOpsSsoService { get; private set; }

    public bool IsDevOpsSignedIn => DevOpsService?.IsSsoAuthenticated ?? false;

    /// <summary>Raised on the UI thread when the silent Azure DevOps sign-in succeeds.</summary>
    public event Action? DevOpsSignedIn;

    /// <summary>Raised on the UI thread whenever <see cref="DevOpsSignInStatus"/> may have changed.</summary>
    public event Action? DevOpsSignInStatusChanged;

    /// <summary>Where Azure DevOps sign-in stands, for views that hold a request until it finishes.</summary>
    public SignInStatus DevOpsSignInStatus =>
        DevOpsService == null || DevOpsSsoService == null ? SignInStatus.NotConfigured
        : IsDevOpsSignedIn ? SignInStatus.SignedIn
        : _devOpsSsoInFlight is { IsCompleted: true } ? SignInStatus.Failed
        : SignInStatus.SigningIn;

    /// <summary>What to do with an Azure DevOps request that failed, given whether it started signed in.</summary>
    public SignInRetry DevOpsRetry(bool startedSignedIn) => SignInDecisions.AfterFailure(startedSignedIn, DevOpsSignInStatus);

    /// <summary>
    /// Act on a Retry for an Azure DevOps request: reload at once when sign-in
    /// isn't the problem (true), otherwise start the silent sign-in and leave the
    /// caller to reload from <see cref="DevOpsSignedIn"/> (false).
    /// </summary>
    public async Task<bool> RetryDevOpsRequestAsync(Func<Task> reload)
    {
        if (SignInDecisions.OnRetry(DevOpsSignInStatus) == SignInRetryTap.Reload)
        {
            await reload();
            return true;
        }
        _ = AttemptSilentDevOpsSsoAsync();
        return false;
    }

    // MARK: - Demo mode

    /// <summary>
    /// Fictional data only, nothing fetched: for screenshots of a public
    /// repository's app. Set by <c>--demo</c>, implied by <c>--capture</c>, or
    /// <c>FLEETMATE_DEMO=1</c>.
    /// </summary>
    public bool DemoMode { get; } = IsDemoRequested();

    private static bool IsDemoRequested() =>
        Environment.GetEnvironmentVariable("FLEETMATE_DEMO") == "1"
        || Environment.GetCommandLineArgs().Any(a =>
            a.Equals("--demo", StringComparison.OrdinalIgnoreCase) || a.Equals("--capture", StringComparison.OrdinalIgnoreCase));

    /// <summary>Fill every Development cache with <see cref="DevelopmentDemoData"/>.</summary>
    private void SeedDevelopmentDemo()
    {
        var now = DateTime.UtcNow;
        _developmentPullRequests = DevelopmentDemoData.PullRequests(now);
        _developmentCommits = DevelopmentDemoData.Commits(now);
        _developmentRuns = DevelopmentDemoData.Runs(now);
        Inbox.Seed(DevelopmentDemoData.Notifications(now));
        Skills?.Seed(DevelopmentDemoData.Skills());
    }

    // MARK: - Caches

    /// <summary>Raised on the UI thread when a Development cache changes or starts or stops loading.</summary>
    public event Action<string>? CacheChanged;

    public PullRequestQueue? DevelopmentPullRequests
    {
        get => _developmentPullRequests;
        set { _developmentPullRequests = value; NotifyCacheChanged("PullRequests"); }
    }
    private PullRequestQueue? _developmentPullRequests;

    public List<RepositoryCommits>? DevelopmentCommits
    {
        get => _developmentCommits;
        set { _developmentCommits = value; NotifyCacheChanged("Commits"); }
    }
    private List<RepositoryCommits>? _developmentCommits;

    public List<PipelineRun>? DevelopmentRuns
    {
        get => _developmentRuns;
        set { _developmentRuns = value; NotifyCacheChanged("Runs"); }
    }
    private List<PipelineRun>? _developmentRuns;

    public PipelineApprovalTracker PipelineApprovals { get; } = new();

    /// <summary>GitHub notifications, polled from launch.</summary>
    public DevelopmentInbox Inbox { get; private set; } = null!;

    /// <summary>The shared agent skills, hooks and standards, plus this PC's own.</summary>
    public SkillsStore? Skills { get; private set; }

    /// <summary>The one Development refresh worker, created on first use.</summary>
    public DevelopmentDataSync DevelopmentSync => _developmentSync ??= new DevelopmentDataSync(this, foreground: true);

    public bool IsCacheLoading(string key)
    {
        lock (_loadingCaches) return _loadingCaches.Contains(key);
    }

    public IDisposable TrackCacheLoad(string key)
    {
        lock (_loadingCaches) _loadingCaches.Add(key);
        NotifyCacheChanged(key);
        return new LoadScope(this, key);
    }

    private sealed class LoadScope(ShellServices services, string key) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            lock (services._loadingCaches) services._loadingCaches.Remove(key);
            services.NotifyCacheChanged(key);
        }
    }

    private void NotifyCacheChanged(string key) => PostToUi(() => CacheChanged?.Invoke(key));

    Task<bool> IDevelopmentHost.RunOnUiAsync(Func<Task<bool>> work)
    {
        // Demo mode keeps its fictional data: a refresh changes nothing.
        if (DemoMode) return Task.FromResult(true);
        if (_ui == null || _ui.HasThreadAccess) return work();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_ui.TryEnqueue(async () =>
            {
                try { done.TrySetResult(await work()); }
                catch (OperationCanceledException ex) { done.TrySetCanceled(ex.CancellationToken); }
                catch (Exception ex) { done.TrySetException(ex); }
            }))
            done.TrySetCanceled();
        return done.Task;
    }

    /// <summary>Run <paramref name="action"/> on the UI thread: at once when already there.</summary>
    public void PostToUi(Action action)
    {
        if (_ui == null || _ui.HasThreadAccess) action();
        else _ui.TryEnqueue(() => action());
    }

    // MARK: - Links

    /// <summary>A pull request, commit or pipeline link waiting for the Development tab.</summary>
    public FleetMateLink? PendingDevelopmentLink { get; set; }

    /// <summary>Raised after a Development link is queued, for a Development tab that is already showing.</summary>
    public event Action? DevelopmentLinkRequested;

    /// <summary>Queue <paramref name="link"/> for Development; the shell then opens the tab.</summary>
    public void RequestDevelopmentLink(FleetMateLink link)
    {
        PendingDevelopmentLink = link;
        DevelopmentLinkRequested?.Invoke();
    }

    /// <summary>
    /// The FLEETMATE_CONTEXT file terminal sessions read. Null until the
    /// Terminal tab is ported and sets it; Development reports its segment and
    /// selection here (see <c>DevelopmentContext</c>), so nothing else changes then.
    /// </summary>
    public AppContextFile? AgentContext { get; set; }

    // MARK: - Lifetime

    /// <summary>Build Development's services from the configuration. Called from the constructor, on the UI thread.</summary>
    private void InitializeDevelopment(FleetMateConfig config)
    {
        _ui = DispatcherQueue.GetForCurrentThread();
        Inbox = new DevelopmentInbox(() => new GitHubNotificationService(config.GitHubProviderOrDefault()));

        if (config.AzureDevOps is { } devOps && !string.IsNullOrWhiteSpace(devOps.Organization))
        {
            try
            {
                DevOpsService = new AzureDevOpsService(devOps);
                Log.Information("AzureDevOpsService initialized");
                if (!string.IsNullOrEmpty(devOps.ClientId) && !string.IsNullOrEmpty(devOps.TenantId))
                    DevOpsSsoService = new DevOpsSsoService(devOps.ClientId, devOps.TenantId);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Azure DevOps could not be set up");
            }
        }
        // The broker is the first, silent phase of the Azure DevOps sign-in.
        if (EntraTokenSource.Shared == null && config.Graph?.TenantId is { Length: > 0 } tenant)
        {
            try { EntraTokenSource.Configure(tenant, config.EntraClientId); }
            catch (Exception ex) { Log.Debug(ex, "Entra broker unavailable"); }
        }

        Func<Task<string?>> token = async () => DevOpsSsoService is { } sso ? await sso.GetValidTokenAsync() : null;
        Skills = new SkillsStore(config, token);
    }

    private void StartDevelopment()
    {
        if (DemoMode)
        {
            SeedDevelopmentDemo();
            return;
        }
        Inbox.Start();
        Skills?.Start();
        _ = AttemptSilentDevOpsSsoAsync();
    }

    private void DisposeDevelopment()
    {
        _developmentSync?.Dispose();
        Inbox.Dispose();
        Skills?.Stop();
    }

    /// <summary>
    /// Sign in to Azure DevOps with nothing on screen (<see cref="DevOpsSignInChain"/>).
    /// The hidden-browser phase FleetMate.GUI runs last is not in the WinUI shell
    /// yet. Concurrent callers share one attempt.
    /// </summary>
    public Task AttemptSilentDevOpsSsoAsync()
    {
        if (_devOpsSsoInFlight is { IsCompleted: false } running) return running;
        return _devOpsSsoInFlight = TrackSilentDevOpsSsoAsync();
    }

    private async Task TrackSilentDevOpsSsoAsync()
    {
        PostToUi(() => DevOpsSignInStatusChanged?.Invoke());
        try
        {
            if (DevOpsSsoService is not { } sso || DevOpsService is not { } devOps || devOps.IsSsoAuthenticated) return;
            Auth.Update(AuthSystemId.DevOps, AuthTokenState.Authenticating());
            var result = await DevOpsSignInChain.RunAsync(sso, hiddenBrowser: null);
            if (result is { Success: true, Token: not null })
            {
                devOps.SetSsoToken(result.Token, result.Expiry, result.UserName);
                Auth.Update(AuthSystemId.DevOps, AuthTokenState.Valid(result.UserName, result.Expiry));
                PostToUi(() => DevOpsSignedIn?.Invoke());
            }
            else
            {
                Log.Warning("[devops-sso] Silent SSO failed; no sign-in window is shown: {Reason}", result.Error);
                Auth.Update(AuthSystemId.DevOps, AuthTokenState.Failed(result.Error ?? "Silent Windows SSO unavailable"));
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[devops-sso] Silent SSO sequence failed");
        }
        finally
        {
            PostToUi(() => DevOpsSignInStatusChanged?.Invoke());
        }
    }
}
