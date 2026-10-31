using System.Text.Json;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Tickets;
using Serilog;

namespace FleetMate.Core.Services.Tickets;

/// <summary>The two TDX reads an inbox pass makes. Tests answer in place of TeamDynamix.</summary>
public interface ITicketInboxApi
{
    Task<List<TdxTicket>> SearchTicketsOrThrowAsync(TicketSearchRequest search, CancellationToken ct = default);
    Task<List<TdxFeedEntry>> GetTicketFeedOrThrowAsync(int ticketId, CancellationToken ct = default);
}

/// <summary>
/// Saves one person's inbox under the app's local data folder
/// (%LOCALAPPDATA%\FleetMate or %LOCALAPPDATA%\TicketsMate), in <c>Inbox</c>:
/// <c>&lt;uid&gt;.json</c> for the items and scan point, written by whichever
/// process scans; <c>&lt;uid&gt;.read.json</c> for read marks, written only by
/// the app window; and <c>&lt;uid&gt;.lock</c>, held while a pass runs.
/// </summary>
public sealed class TicketInboxStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    private readonly string _directory;

    public TicketInboxStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(AppEdition.Current.LocalAppDataDirectory, "Inbox");
    }

    public string PathFor(Guid uid) => Path.Combine(_directory, $"{uid}.json");
    public string ReadPathFor(Guid uid) => Path.Combine(_directory, $"{uid}.read.json");
    public string LockPathFor(Guid uid) => Path.Combine(_directory, $"{uid}.lock");

    /// <summary>The saved inbox with its read marks, or an empty one when none is saved or it cannot be read.</summary>
    public TicketInboxState Load(Guid uid)
    {
        var state = Read<TicketInboxState>(PathFor(uid)) ?? new TicketInboxState();
        state.ReadIds = Read<HashSet<int>>(ReadPathFor(uid)) ?? new HashSet<int>();
        return state;
    }

    /// <summary>Save the items and scan point. Read marks are left alone.</summary>
    public void Save(Guid uid, TicketInboxState state) => Write(PathFor(uid), state);

    /// <summary>Save the read marks. Only the app window calls this.</summary>
    public void SaveRead(Guid uid, TicketInboxState state) => Write(ReadPathFor(uid), state.ReadIds);

    /// <summary>
    /// Take the exclusive scan lock for one person, without waiting. Null when
    /// another process holds it. Disposing the result releases it.
    /// </summary>
    public IDisposable? TryLock(Guid uid)
    {
        Directory.CreateDirectory(_directory);
        try
        {
            return new FileStream(LockPathFor(uid), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // A lock file being deleted by its holder's close reads as access denied.
            return null;
        }
    }

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(stream, Options);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Ticket inbox file could not be read; starting fresh");
            return null;
        }
    }

    /// <summary>Write through a temp file, so a quit mid-write leaves the last good copy.</summary>
    private void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(_directory);
        var temp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>Progress through one pass: scanning ticket <see cref="Done"/> + 1 of <see cref="Total"/>.</summary>
public readonly record struct TicketInboxProgress(int Done, int Total);

/// <summary>
/// Finds every comment of the last 90 days, on any ticket, that notified the
/// signed-in person. TDX keeps no per-person notification list, so each pass
/// searches the tickets modified since the last one and reads their feeds.
///
/// TDX allows about 60 requests a minute, so requests are spaced about 1.2
/// seconds apart. Tickets are read newest-modified first and the state is
/// saved after each one, so a quit resumes where it stopped: the next pass
/// searches from the same point and skips tickets already read at the same
/// ModifiedDate.
/// </summary>
public sealed class TicketInboxScanner
{
    public static readonly TimeSpan RequestSpacing = TimeSpan.FromSeconds(1.2);
    public static readonly TimeSpan Overlap = TimeSpan.FromMinutes(10);
    public const int MaxTickets = 5000;

    private readonly ITicketInboxApi _api;
    private readonly TicketInboxStore _store;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public TicketInboxScanner(
        ITicketInboxApi api,
        TicketInboxStore store,
        Func<DateTime>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _api = api;
        _store = store;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _delay = delay ?? Task.Delay;
    }

    /// <summary>
    /// Run one pass if no other process is scanning this person's inbox.
    /// False when the lock was held elsewhere: the caller reloads the saved
    /// inbox instead, which the other process keeps current.
    /// </summary>
    public async Task<bool> TryRunPassAsync(
        TicketInboxPerson person,
        TicketInboxState state,
        IProgress<TicketInboxProgress>? progress = null,
        Action? changed = null,
        CancellationToken ct = default)
    {
        using var held = _store.TryLock(person.Uid);
        if (held == null) return false;
        await RunPassAsync(person, state, progress, changed, ct);
        return true;
    }

    /// <summary>
    /// Run one pass over <paramref name="state"/>, saving as it goes.
    /// <paramref name="changed"/> is called after each ticket that changed the items.
    /// </summary>
    /// <exception cref="TdxRequestException">The ticket search failed. Feed failures skip that ticket.</exception>
    public async Task RunPassAsync(
        TicketInboxPerson person,
        TicketInboxState state,
        IProgress<TicketInboxProgress>? progress = null,
        Action? changed = null,
        CancellationToken ct = default)
    {
        var passStart = _utcNow();
        var cutoff = passStart - TicketInboxMatch.Window;
        var from = state.ScannedThrough is { } through ? TicketInboxState.ToUtc(through) - Overlap : cutoff;
        if (from < cutoff) from = cutoff;

        var tickets = await _api.SearchTicketsOrThrowAsync(new TicketSearchRequest
        {
            ModifiedDateFrom = from,
            MaxResults = MaxTickets,
        }, ct);

        var pending = tickets
            .Where(t => t.ModifiedDate is not { } modified
                        || !state.TicketModified.TryGetValue(t.Id, out var seen)
                        || TicketInboxState.ToUtc(seen) != TicketInboxState.ToUtc(modified))
            .OrderByDescending(t => t.ModifiedDate ?? DateTime.MinValue)
            .ToList();

        Log.Information("Ticket inbox pass: {Pending} of {Found} tickets changed since {From:u}", pending.Count, tickets.Count, from);
        progress?.Report(new TicketInboxProgress(0, pending.Count));

        var failures = 0;
        for (var i = 0; i < pending.Count; i++)
        {
            await _delay(RequestSpacing, ct);
            var ticket = pending[i];

            List<TdxFeedEntry> feed;
            try
            {
                feed = await _api.GetTicketFeedOrThrowAsync(ticket.Id, ct);
            }
            catch (TdxRequestException ex)
            {
                // Left unrecorded, so the next pass tries it again.
                failures++;
                Log.Debug(ex, "Ticket inbox skipped a ticket whose feed could not be read");
                progress?.Report(new TicketInboxProgress(i + 1, pending.Count));
                continue;
            }

            var before = state.Items.Count(item => item.TicketId == ticket.Id);
            var found = TicketInboxMatch.Select(ticket, feed, person, cutoff);
            state.ReplaceTicket(ticket.Id, found);
            if (ticket.ModifiedDate is { } modified) state.TicketModified[ticket.Id] = TicketInboxState.ToUtc(modified);
            _store.Save(person.Uid, state);
            if (found.Count > 0 || before > 0) changed?.Invoke();
            progress?.Report(new TicketInboxProgress(i + 1, pending.Count));
        }

        // The scan point moves only when every feed was read, so a skipped
        // ticket stays inside the next pass's search window.
        if (failures == 0) state.ScannedThrough = passStart;
        state.Prune(cutoff);
        _store.Save(person.Uid, state);
        changed?.Invoke();
    }
}
