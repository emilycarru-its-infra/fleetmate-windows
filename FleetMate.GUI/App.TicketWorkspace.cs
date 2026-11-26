using System.Windows.Threading;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;

namespace FleetMate.GUI;

public partial class App
{
    private TicketBoardCache? _ticketCache;

    /// <summary>
    /// The loaded Tickets board and its per-identity workspace, shared with
    /// the WinUI shell through <see cref="TicketBoardCache"/>.
    /// </summary>
    public TicketBoardCache TicketCache => _ticketCache ??= CreateTicketCache();

    private TicketBoardCache CreateTicketCache()
    {
        var cache = new TicketBoardCache(() => TdxService, () => Config?.Tdx, () => TdxSignInSettled,
            () => CacheDuration, new DispatcherSynchronizationContext(Dispatcher));
        cache.Changed += () => NotifyCacheChanged("Tickets");
        cache.Updated += () => SetTdxLaunchStep(null);
        return cache;
    }

    public DateTime? LastTicketsRefreshUtc => TicketCache.LastRefreshUtc;

    /// <summary>Restore only the confirmed identity's snapshot, then coalesce and cache a strict fresh board read.</summary>
    public Task<List<TdxTicket>> LoadBoardTicketsAsync(CancellationToken ct = default) => TicketCache.LoadAsync(ct);

    private void ResetTicketWorkspace() => TicketCache.Reset();

    private bool TicketWorkspaceIdentityChanged => TicketCache.IdentityChanged;
}
