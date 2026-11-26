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
public sealed class ShellServices : IDisposable
{
    public ShellServices(FleetMateConfig config)
    {
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
    }

    public FleetMateConfig Config { get; }

    /// <summary>Sign-in state for every configured system, as the Authentication popover shows it.</summary>
    public AuthManager Auth { get; }

    public TdxService? Tdx { get; }

    /// <summary>The TeamDynamix sign-in: silent at launch, renewed while the window is open.</summary>
    public TdxSessionCoordinator TdxSession { get; }

    /// <summary>Tickets loaded by the Tickets page, for search. Empty until the page loads live data.</summary>
    public IReadOnlyList<TdxTicket> CachedTickets { get; set; } = Array.Empty<TdxTicket>();

    /// <summary>A snapshot of the loaded data for <see cref="GlobalSearch.Search"/>; no API calls.</summary>
    public SearchSources BuildSearchSources() => new()
    {
        Tickets = CachedTickets.ToList(),
    };

    /// <summary>
    /// A ticket a <c>fleetmate:</c> link asked for, waiting for the Tickets
    /// page. Set by the shell; the live Tickets page (phase 2) opens and clears it.
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
        TdxSession.Start();
        _ = StartSignInAsync();
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
    }

    public void Dispose()
    {
        TdxSession.Dispose();
        Tdx?.Dispose();
    }
}
