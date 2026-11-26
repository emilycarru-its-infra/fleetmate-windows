using FleetMate.Core.Models.Tickets;

namespace FleetMate.Core.Services.Tickets;

/// <summary>
/// The loaded Tickets board, shared by every view that shows it: the list,
/// the board, the widgets and search. Each confirmed TeamDynamix identity
/// (service, account, person and date window) has its own workspace: its
/// saved snapshot shows while the strict fresh read runs, concurrent reads
/// share one fetch, and a read that finishes after the identity changed is
/// dropped rather than shown as another account's tickets.
///
/// Changes to <see cref="Tickets"/> and both events happen on the UI
/// context given at construction, as the pages that read them expect.
/// </summary>
public sealed class TicketBoardCache : IDisposable
{
    private readonly Func<TdxService?> _service;
    private readonly Func<TdxConfig?> _config;
    private readonly Func<Task> _signInSettled;
    private readonly Func<TimeSpan> _lifetime;
    private readonly SynchronizationContext? _ui;
    private readonly string? _snapshotDirectory;
    private Workspace? _workspace;

    /// <param name="service">The TeamDynamix service now in use; it can be replaced when settings reload.</param>
    /// <param name="signInSettled">The sign-in in flight, so a read waits for its credential.</param>
    /// <param name="lifetime">How long a loaded board counts as fresh.</param>
    /// <param name="ui">The UI context; the current one when null.</param>
    /// <param name="snapshotDirectory">Where snapshots are saved; the edition's folder when null.</param>
    public TicketBoardCache(Func<TdxService?> service, Func<TdxConfig?> config, Func<Task> signInSettled,
        Func<TimeSpan> lifetime, SynchronizationContext? ui = null, string? snapshotDirectory = null)
    {
        _service = service;
        _config = config;
        _signInSettled = signInSettled;
        _lifetime = lifetime;
        _ui = ui ?? SynchronizationContext.Current;
        _snapshotDirectory = snapshotDirectory;
    }

    /// <summary>The loaded tickets. Views update a ticket in place after an edit.</summary>
    public List<TdxTicket> Tickets { get; set; } = new();

    /// <summary>The created-date window the board reads.</summary>
    public TicketDateRangePreset Preset { get; set; } = TicketDateRangePreset.CurrentTerm;

    /// <summary>When the last fresh read landed, in local time; null after a reset or invalidation.</summary>
    public DateTime? LoadedAt { get; private set; }

    public DateTime? LastRefreshUtc => LoadedAt?.ToUniversalTime();

    /// <summary>A fresh read landed within the cache lifetime.</summary>
    public bool IsValid => LoadedAt is { } loaded && DateTime.Now - loaded < _lifetime();

    /// <summary>The tickets were replaced: a fresh read, a restored snapshot or a reset.</summary>
    public event Action? Changed;

    /// <summary>A fresh read landed; the launch progress can clear.</summary>
    public event Action? Updated;

    /// <summary>The next read goes to TeamDynamix even though tickets are held.</summary>
    public void Invalidate() => LoadedAt = null;

    /// <summary>Forget the tickets and their freshness, as Refresh does before it reads again.</summary>
    public void Clear()
    {
        Tickets.Clear();
        LoadedAt = null;
    }

    /// <summary>Put an edited ticket in place of the one held; false when it is not held.</summary>
    public bool Replace(TdxTicket updated)
    {
        var index = Tickets.FindIndex(t => t.Id == updated.Id);
        if (index < 0) return false;
        Tickets[index] = updated;
        return true;
    }

    /// <summary>Record a fresh read and save it as this identity's snapshot.</summary>
    public void Update(List<TdxTicket> tickets)
    {
        Tickets = tickets;
        LoadedAt = DateTime.Now;
        Changed?.Invoke();
        Updated?.Invoke();
        // Only the confirmed service, person and window write their snapshot.
        if (_workspace is { } workspace && workspace.IsCurrent(this))
            _ = workspace.Snapshot.SaveInBackground(tickets);
    }

    /// <summary>The signed-in account or service no longer matches the workspace the tickets came from.</summary>
    public bool IdentityChanged => _workspace is { } workspace && !workspace.IsIdentityCurrent(this);

    /// <summary>Drop the workspace and its tickets, as after a change of account or settings.</summary>
    public void Reset()
    {
        _workspace?.Dispose();
        _workspace = null;
        Tickets = new();
        LoadedAt = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Read the board for the signed-in person. The identity's snapshot fills
    /// an empty cache while the read runs; the read then replaces it, unless
    /// a newer read already landed or the identity changed meanwhile.
    /// </summary>
    /// <exception cref="InvalidOperationException">TeamDynamix is not signed in, or the person cannot be found.</exception>
    /// <exception cref="OperationCanceledException">The account or workspace changed while the read ran.</exception>
    public async Task<List<TdxTicket>> LoadAsync(CancellationToken ct = default)
    {
        await _signInSettled().WaitAsync(ct);
        var service = _service();
        var account = service?.AuthenticatedUserId;
        var config = _config();
        if (service?.IsSsoAuthenticated != true || string.IsNullOrWhiteSpace(account) || config == null)
            throw new InvalidOperationException("Sign in to TeamDynamix to refresh tickets.");
        var person = await service.GetMeAsync().WaitAsync(ct);
        if (person?.Uid is not { } uid || uid == Guid.Empty)
            throw new InvalidOperationException("The signed-in ticket person could not be resolved.");
        if (!SameAccount(service, account))
            throw new OperationCanceledException("The ticket account changed.");

        var workspace = await OnUiAsync(() =>
        {
            if (!SameAccount(service, account)) throw new OperationCanceledException("The ticket account changed.");
            if (_workspace is { } current && current.IsCurrent(this) && current.Person == uid) return current;
            Reset();
            var preset = Preset;
            var snapshot = TicketSnapshot.ForScope(service.InboxServiceUrl, uid, preset, _snapshotDirectory);
            return _workspace = new Workspace
            {
                Service = service,
                Account = account,
                Person = uid,
                Preset = preset,
                Snapshot = snapshot,
                Restore = Task.Run(() => snapshot.Load(TimeSpan.FromMinutes(15))),
                Loader = new TicketBoardLoader(_signInSettled,
                    (range, token) => TicketBoardQuery.LoadAsync(service, config, range, ct: token, personUid: uid)),
            };
        });
        var fetched = workspace.Loader.LoadRequest(workspace.Preset, ct);
        _ = RestoreSnapshotAsync(workspace, ct);
        var tickets = await fetched.Task;
        ct.ThrowIfCancellationRequested();
        var accepted = await OnUiAsync(() =>
        {
            if (!workspace.IsCurrent(this)) return false;
            if (fetched.Generation < workspace.LastAppliedGeneration) return true;
            workspace.LastAppliedGeneration = fetched.Generation;
            Update(tickets);
            return true;
        });
        if (!accepted) throw new OperationCanceledException("The ticket workspace changed.");
        return tickets;
    }

    private bool SameAccount(TdxService service, string account) =>
        ReferenceEquals(service, _service()) && string.Equals(account, service.AuthenticatedUserId, StringComparison.OrdinalIgnoreCase);

    private async Task RestoreSnapshotAsync(Workspace workspace, CancellationToken ct)
    {
        if (workspace.Restore is not { } restore) return;
        try
        {
            var cached = await restore.WaitAsync(ct).ConfigureAwait(false);
            await OnUiAsync(() =>
            {
                workspace.Restore = null;
                if (workspace.IsCurrent(this) && LoadedAt == null && Tickets.Count == 0 && cached.Count > 0)
                {
                    Tickets = cached;
                    Changed?.Invoke();
                }
                return true;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    private Task<T> OnUiAsync<T>(Func<T> work)
    {
        if (_ui == null || SynchronizationContext.Current == _ui)
        {
            try { return Task.FromResult(work()); }
            catch (Exception ex) { return Task.FromException<T>(ex); }
        }
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(_ =>
        {
            try { done.TrySetResult(work()); }
            catch (Exception ex) { done.TrySetException(ex); }
        }, null);
        return done.Task;
    }

    public void Dispose()
    {
        _workspace?.Dispose();
        _workspace = null;
    }

    private sealed class Workspace : IDisposable
    {
        public required TdxService Service;
        public required string Account;
        public required Guid Person;
        public required TicketDateRangePreset Preset;
        public required TicketSnapshot Snapshot;
        public required TicketBoardLoader Loader;
        public required Task<List<TdxTicket>>? Restore;
        public long LastAppliedGeneration;

        public bool IsIdentityCurrent(TicketBoardCache cache) => ReferenceEquals(Service, cache._service())
            && string.Equals(Account, Service.AuthenticatedUserId, StringComparison.OrdinalIgnoreCase)
            && Service.IsSsoAuthenticated;

        public bool IsCurrent(TicketBoardCache cache) =>
            ReferenceEquals(this, cache._workspace) && IsIdentityCurrent(cache) && Preset == cache.Preset;

        public void Dispose() => Loader.Dispose();
    }
}
