using FleetMate.Core.Config;
using FleetMate.Core.Models;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Search;
using FleetMate.Core.Services.Tickets;
using Serilog;

namespace FleetMate.WinUI.Shell;

/// <summary>
/// The services a page reaches for, built once at launch from the desktop
/// configuration (the config file, then HKLM and HKCU policy for this
/// edition). Pages read them through <see cref="App.Services"/>:
///
/// <code>
/// var tdx = App.Services.Tdx;          // null when TeamDynamix is not configured
/// App.Services.TdxSession.SignInChanged += Reload;
/// </code>
///
/// Phase 1 builds TeamDynamix only, the one service the Tickets page and
/// TicketsMate need. The other services (Graph, Snipe-IT, Azure DevOps,
/// ReportMate, SSH) are added here as their pages are ported, following
/// FleetMate.GUI's <c>App.InitializeServices</c>.
/// Create on the UI thread.
/// </summary>
public sealed partial class ShellServices : IDisposable
{
    private static readonly List<Action<ShellServices>> DemoFixtures = new();

    /// <summary>
    /// Register a page's fictional data for demo mode. The action runs once,
    /// when the services are built in demo mode, and fills the caches the page
    /// reads; it never runs against live services. Register from
    /// <see cref="App"/>'s constructor, beside <c>ShellPages.Register</c>:
    ///
    /// <code>
    /// ShellServices.RegisterDemoFixtures(s => s.TicketCache.Update(DemoTickets.Tickets.ToList()));
    /// </code>
    ///
    /// Fixtures are public: every name, address, id and record in them is made
    /// up, and addresses use example.* domains.
    /// </summary>
    public static void RegisterDemoFixtures(Action<ShellServices> fill) => DemoFixtures.Add(fill);

    /// <param name="config">The desktop configuration. Ignored in demo mode, which reads none.</param>
    /// <param name="demoMode">
    /// Serve registered fixtures; build no live service and sign in to nothing.
    /// Null reads this process's <c>--demo</c>, <c>--capture</c> and <c>FLEETMATE_DEMO</c>.
    /// </param>
    public ShellServices(FleetMateConfig config, bool? demoMode = null)
    {
        DemoMode = demoMode ?? Core.Config.DemoMode.IsRequested();
        TicketCache = new TicketBoardCache(() => Tdx, () => Config?.Tdx, () => TdxSession?.Settled ?? Task.CompletedTask,
            () => TimeSpan.FromMinutes(Config?.CacheMinutes ?? 5));
        if (DemoMode)
        {
            // Nothing configured, so nothing can sign in or call out, and the
            // Authentication popover has no account to show.
            Config = new FleetMateConfig();
            Auth = new AuthManager(Config);
            TdxSession = new TdxSessionCoordinator(null, null, Auth, TdxSessionCoordinator.HttpOnly);
            foreach (var fill in DemoFixtures) fill(this);
            Log.Information("Demo mode: fictional fixture data, no live services");
            return;
        }

        Config = config;
        Auth = new AuthManager(config);
        if (config.Tdx is { } tdx && !string.IsNullOrEmpty(tdx.BaseUrl))
        {
            Tdx = new TdxService(tdx);
            Log.Information("TdxService initialized");
        }
        // The browser-free chain: a session another FleetMate process saved is
        // adopted first, then HTTP Negotiate. The hidden-browser phase the WPF
        // app runs after it is not in the WinUI shell yet.
        TdxSession = new TdxSessionCoordinator(config.Tdx, Tdx, Auth, TdxSessionCoordinator.HttpOnly);
        InitializeInventory(config);
        InitializeDevelopment(config);
        InitializeIdentity(config);
        TicketCache.Updated += TdxSession.TicketsLoaded;
        TdxSession.SignInChanged += OnTdxSignInChanged;
        InitializeDevices(config);
    }

    /// <summary>
    /// Demo mode is on (<c>--demo</c>, <c>--capture</c> or <c>FLEETMATE_DEMO=1</c>):
    /// every service is absent, caches hold registered fixtures, and nothing
    /// signed in is shown. Pages that would call a service read their cache instead.
    /// </summary>
    public bool DemoMode { get; }

    /// <summary>
    /// A live service exists or someone is signed in. A capture refuses to
    /// write screenshots while this is true.
    /// </summary>
    public bool HasLiveSession =>
        Tdx != null || Graph != null || TdxSession.IsSignedIn
        || Auth.Systems.Values.Any(s => s.State.Kind is AuthStateKind.Valid or AuthStateKind.ServicePrincipal);

    public FleetMateConfig Config { get; }

    /// <summary>Sign-in state for every configured system, as the Authentication popover shows it.</summary>
    public AuthManager Auth { get; }

    public TdxService? Tdx { get; }

    /// <summary>The TeamDynamix sign-in: silent at launch, renewed while the window is open.</summary>
    public TdxSessionCoordinator TdxSession { get; }

    /// <summary>
    /// The loaded Tickets board, shared with FleetMate.GUI's code: each
    /// signed-in identity's snapshot, the coalesced fresh read and the edits
    /// made in place. In demo mode it holds the registered fixtures. Its
    /// events arrive on the UI thread.
    /// </summary>
    public TicketBoardCache TicketCache { get; }

    /// <summary>Tickets loaded for the board, for search.</summary>
    public IReadOnlyList<TdxTicket> CachedTickets => TicketCache.Tickets;

    /// <summary>A snapshot of the loaded data for <see cref="GlobalSearch.Search"/>; no API calls.</summary>
    public SearchSources BuildSearchSources() => new()
    {
        Assets = CachedAssets.ToList(),
        Tickets = CachedTickets.ToList(),
        PullRequests = DevelopmentPullRequests?.PullRequests.ToList() ?? new(),
        Notifications = Inbox.Notifications.ToList(),
        Commits = DevelopmentCommits?.ToList() ?? new(),
        Runs = DevelopmentRuns?.ToList() ?? new(),
        Devices = CachedDevices.ToList(),
    };

    /// <summary>
    /// A ticket a <c>fleetmate:</c> link asked for, waiting for the Tickets
    /// page. Set by the shell; the Tickets page opens and clears it.
    /// </summary>
    public int? PendingTicketId { get; set; }

    /// <summary>
    /// The latest app-level failure (an unobserved task exception), shown in
    /// Recent Activity; null when there is none.
    /// </summary>
    public string? AppError { get; private set; }

    public event Action? AppErrorChanged;

    public void ReportAppError(string? message)
    {
        AppError = message;
        AppErrorChanged?.Invoke();
    }

    /// <summary>Start the launch sign-in and the keeper that renews it.</summary>
    public void Start()
    {
        if (DemoMode) return;
        TdxSession.Start();
        _ = StartSignInAsync();
        StartInventory();
        StartDevelopment();
        StartDevices();
    }

    private async Task StartSignInAsync()
    {
        try
        {
            await TdxSession.AttemptSilentAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[tdx-sso] Silent SSO sequence failed");
        }
        if (Tdx == null || !TdxSession.IsSignedIn) return;
        // The board loads at launch, as FleetMate.GUI's preload does, so the
        // Tickets tab opens on it; the page shares this read.
        try
        {
            var tickets = await TicketCache.LoadAsync();
            Log.Information("Preloaded {Count} tickets", tickets.Count);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warning(ex, "Failed to preload tickets");
            TdxSession.TicketsLoaded();
        }
    }

    /// <summary>
    /// Another account's tickets never stay on screen: a change of identity
    /// drops the board, and a fresh sign-in makes the next read go to
    /// TeamDynamix.
    /// </summary>
    private void OnTdxSignInChanged()
    {
        if (TicketCache.IdentityChanged) TicketCache.Reset();
        else if (TdxSession.IsSignedIn) TicketCache.Invalidate();
    }

    public void Dispose()
    {
        DisposeInventory();
        DisposeDevelopment();
        DisposeIdentity();
        TdxSession.SignInChanged -= OnTdxSignInChanged;
        TicketCache.Dispose();
        DisposeDevices();
        TdxSession.Dispose();
        Tdx?.Dispose();
    }
}
