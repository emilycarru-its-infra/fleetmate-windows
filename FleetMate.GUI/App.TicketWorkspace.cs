using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;

namespace FleetMate.GUI;

public partial class App
{
    private TicketWorkspace? _ticketWorkspace;
    private sealed class TicketWorkspace : IDisposable
    {
        public required TdxService Service;
        public required string Account;
        public required Guid Person;
        public required TicketDateRangePreset Preset;
        public required TicketSnapshot Snapshot;
        public required TicketBoardLoader Loader;
        public required Task<List<TdxTicket>>? Restore;
        public long LastAppliedGeneration;
        public bool IsIdentityCurrent(App app) => ReferenceEquals(Service, app.TdxService)
            && string.Equals(Account, Service.AuthenticatedUserId, StringComparison.OrdinalIgnoreCase)
            && Service.IsSsoAuthenticated;
        public bool IsCurrent(App app) => ReferenceEquals(this, app._ticketWorkspace) && IsIdentityCurrent(app) && Preset == app.TicketDatePreset;
        public void Dispose() => Loader.Dispose();
    }

    public DateTime? LastTicketsRefreshUtc => _ticketsCacheTime?.ToUniversalTime();

    /// <summary>Restore only the confirmed identity's snapshot, then coalesce and cache a strict fresh board read.</summary>
    public async Task<List<TdxTicket>> LoadBoardTicketsAsync(CancellationToken ct = default)
    {
        await TdxSignInSettled.WaitAsync(ct);
        var service = TdxService;
        var account = service?.AuthenticatedUserId;
        var config = Config.Tdx;
        if (service?.IsSsoAuthenticated != true || string.IsNullOrWhiteSpace(account) || config == null)
            throw new InvalidOperationException("Sign in to TeamDynamix to refresh tickets.");
        var person = await service.GetMeAsync().WaitAsync(ct);
        if (person?.Uid is not { } uid || uid == Guid.Empty)
            throw new InvalidOperationException("The signed-in ticket person could not be resolved.");
        if (!ReferenceEquals(service, TdxService) || !string.Equals(account, service.AuthenticatedUserId, StringComparison.OrdinalIgnoreCase))
            throw new OperationCanceledException("The ticket account changed.");

        var workspace = await Dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(service, TdxService) || !string.Equals(account, service.AuthenticatedUserId, StringComparison.OrdinalIgnoreCase))
                throw new OperationCanceledException("The ticket account changed.");
            if (_ticketWorkspace is { } current && current.IsCurrent(this) && current.Person == uid) return current;
            ResetTicketWorkspace();
            var preset = TicketDatePreset;
            var snapshot = TicketSnapshot.ForScope(service.InboxServiceUrl, uid, preset);
            return _ticketWorkspace = new TicketWorkspace
            {
                Service = service, Account = account, Person = uid, Preset = preset, Snapshot = snapshot,
                Restore = Task.Run(() => snapshot.Load(TimeSpan.FromMinutes(15))),
                Loader = new TicketBoardLoader(() => TdxSignInSettled,
                    (range, token) => TicketBoardQuery.LoadAsync(service, config, range, ct: token, personUid: uid)),
            };
        });
        var fetched = workspace.Loader.LoadRequest(workspace.Preset, ct);
        _ = RestoreTicketSnapshotAsync(workspace, ct);
        var tickets = await fetched.Task;
        ct.ThrowIfCancellationRequested();
        var accepted = await Dispatcher.InvokeAsync(() =>
        {
            if (!workspace.IsCurrent(this)) return false;
            if (fetched.Generation < workspace.LastAppliedGeneration) return true;
            workspace.LastAppliedGeneration = fetched.Generation;
            UpdateTicketsCache(tickets);
            return true;
        });
        if (!accepted) throw new OperationCanceledException("The ticket workspace changed.");
        return tickets;
    }

    private async Task RestoreTicketSnapshotAsync(TicketWorkspace workspace, CancellationToken ct)
    {
        var restore = workspace.Restore;
        if (restore == null) return;
        try
        {
            var cached = await restore.WaitAsync(ct).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                workspace.Restore = null;
                if (workspace.IsCurrent(this) && _ticketsCacheTime == null && CachedTickets.Count == 0 && cached.Count > 0)
                {
                    CachedTickets = cached;
                    NotifyCacheChanged("Tickets");
                }
            });
        }
        catch (OperationCanceledException) { }
    }

    private void ResetTicketWorkspace()
    {
        _ticketWorkspace?.Dispose();
        _ticketWorkspace = null;
        CachedTickets = new();
        _ticketsCacheTime = null;
        NotifyCacheChanged("Tickets");
    }

    private bool TicketWorkspaceIdentityChanged => _ticketWorkspace is { } workspace && !workspace.IsIdentityCurrent(this);
}
