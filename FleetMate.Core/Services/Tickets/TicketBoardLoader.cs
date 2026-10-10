using FleetMate.Core.Models.Tickets;

namespace FleetMate.Core.Services.Tickets;

/// <summary>
/// Loads the ticket board in step with TeamDynamix sign-in.
///
/// At launch the Tickets page and the background preload both ask for the
/// board while silent SSO is still running. Asking then gets no credential and
/// an empty board, and nothing asked again once SSO finished. So a load first
/// waits for any sign-in in flight, then fetches. Callers asking for the same
/// date range at the same time share one fetch, so the page and the preload
/// do not pull the whole board twice.
/// </summary>
public sealed class TicketBoardLoader
{
    private readonly Func<Task> _signInSettled;
    private readonly Func<TicketDateRangePreset, Task<List<TdxTicket>>> _fetch;
    private readonly object _gate = new();
    private (TicketDateRangePreset Preset, Task<List<TdxTicket>> Task)? _inFlight;

    /// <param name="signInSettled">
    /// The sign-in in flight, or a completed task when there is none. Read on
    /// every load, so a sign-in started later is waited on too.
    /// </param>
    /// <param name="fetch">The board query itself.</param>
    public TicketBoardLoader(Func<Task> signInSettled, Func<TicketDateRangePreset, Task<List<TdxTicket>>> fetch)
    {
        _signInSettled = signInSettled;
        _fetch = fetch;
    }

    public Task<List<TdxTicket>> LoadAsync(TicketDateRangePreset preset)
    {
        lock (_gate)
        {
            if (_inFlight is { } running && running.Preset == preset && !running.Task.IsCompleted)
                return running.Task;

            var task = RunAsync(preset);
            _inFlight = (preset, task);
            return task;
        }
    }

    private async Task<List<TdxTicket>> RunAsync(TicketDateRangePreset preset)
    {
        try
        {
            await _signInSettled();
        }
        catch
        {
            // A failed sign-in still ends the wait; the fetch reports the outcome.
        }
        return await _fetch(preset);
    }
}
