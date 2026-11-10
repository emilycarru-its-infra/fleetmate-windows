using FleetMate.Core.Models.Tickets;

namespace FleetMate.Core.Services.Tickets;

/// <summary>Coalesces board reads; cancel the shared fetch only after its last interested caller leaves.</summary>
public sealed class TicketBoardLoader : IDisposable
{
    private readonly Func<Task> _signInSettled;
    private readonly Func<TicketDateRangePreset, CancellationToken, Task<List<TdxTicket>>> _fetch;
    private readonly object _gate = new();
    private Flight? _inFlight;
    private bool _disposed;
    private long _generation;
    public readonly record struct Request(Task<List<TdxTicket>> Task, long Generation);
    private sealed class Flight
    {
        public TicketDateRangePreset Preset;
        public long Generation;
        public readonly CancellationTokenSource Cancellation = new();
        public Task<List<TdxTicket>> Task = null!;
        public int Waiters;
        public bool Uncancellable;
    }

    public TicketBoardLoader(Func<Task> signInSettled, Func<TicketDateRangePreset, Task<List<TdxTicket>>> fetch)
        : this(signInSettled, (preset, _) => fetch(preset)) { }

    public TicketBoardLoader(Func<Task> signInSettled, Func<TicketDateRangePreset, CancellationToken, Task<List<TdxTicket>>> fetch)
    {
        _signInSettled = signInSettled;
        _fetch = fetch;
    }

    public Task<List<TdxTicket>> LoadAsync(TicketDateRangePreset preset, CancellationToken ct = default) => LoadRequest(preset, ct).Task;

    public Request LoadRequest(TicketDateRangePreset preset, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var flight = _inFlight;
            if (flight == null || flight.Preset != preset || flight.Task.IsCompleted)
            {
                flight = new Flight { Preset = preset, Generation = ++_generation };
                flight.Task = RunAsync(preset, flight.Cancellation.Token);
                _inFlight = flight;
                _ = flight.Task.ContinueWith(_ => { lock (_gate) flight.Cancellation.Dispose(); }, CancellationToken.None,
                    TaskContinuationOptions.None, TaskScheduler.Default);
            }
            if (!ct.CanBeCanceled) { flight.Uncancellable = true; return new(flight.Task, flight.Generation); }
            flight.Waiters++;
            return new(WaitAsync(flight, ct), flight.Generation);
        }
    }

    private async Task<List<TdxTicket>> WaitAsync(Flight flight, CancellationToken ct)
    {
        try { return await flight.Task.WaitAsync(ct).ConfigureAwait(false); }
        finally
        {
            lock (_gate)
            {
                flight.Waiters--;
                if (flight.Waiters == 0 && !flight.Uncancellable && !flight.Task.IsCompleted)
                {
                    if (ReferenceEquals(_inFlight, flight)) _inFlight = null;
                    flight.Cancellation.Cancel();
                }
            }
        }
    }

    private async Task<List<TdxTicket>> RunAsync(TicketDateRangePreset preset, CancellationToken ct)
    {
        try { await _signInSettled().WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* The fetch reports a failed sign-in after it settles. */ }
        return await _fetch(preset, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            if (_inFlight is { Task.IsCompleted: false } flight) flight.Cancellation.Cancel();
            _inFlight = null;
        }
    }
}
