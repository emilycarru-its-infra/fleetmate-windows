namespace FleetMate.Core.Services.Tickets;

/// <summary>One cancellable active-view loop, with no overlapping ticks across deactivation/reactivation.</summary>
public sealed class ActiveTicketRefreshLoop : IDisposable
{
    private readonly Func<bool, CancellationToken, Task> _refresh;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource? _cancellation;
    private Task _tail = Task.CompletedTask;
    private bool _active;
    private bool _disposed;
    public ActiveTicketRefreshLoop(Func<bool, CancellationToken, Task> refresh,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _refresh = refresh;
        _delay = delay ?? Task.Delay;
    }
    public Task Completion => _tail;
    public void SetActive(bool active)
    {
        if (_disposed || _active == active) return;
        _active = active;
        CancelCurrent();
        if (!active) return;
        var previous = _tail;
        var cancellation = _cancellation = new();
        _tail = RunAsync(previous, cancellation);
    }
    private async Task RunAsync(Task previous, CancellationTokenSource cancellation)
    {
        var ct = cancellation.Token;
        try
        {
            try { await previous.ConfigureAwait(false); } catch { }
            var activation = true;
            while (!ct.IsCancellationRequested)
            {
                var pause = TimeSpan.FromSeconds(60);
                try { await _refresh(activation, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch { pause = TimeSpan.FromMinutes(5); }
                activation = false;
                await _delay(pause, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { Interlocked.CompareExchange(ref _cancellation, null, cancellation); cancellation.Dispose(); }
    }
    private void CancelCurrent()
    {
        try { _cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _active = false;
        CancelCurrent(); _cancellation = null;
    }
}
