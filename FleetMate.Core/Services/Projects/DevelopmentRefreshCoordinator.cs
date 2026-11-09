namespace FleetMate.Core.Services.Projects;

public enum DevelopmentRefreshArea { PullRequests, Commits, Pipelines }
public sealed record DevelopmentRefreshRequest(DevelopmentRefreshArea Area, bool History = false);

/// <summary>One session worker: coalesced requests, cancellable inactivity, and targeted pipeline checks.</summary>
public sealed class DevelopmentRefreshCoordinator : IDisposable, IAsyncDisposable
{
    private sealed record Work(DevelopmentRefreshRequest Request, TaskCompletionSource<bool> Completion);
    private readonly object _gate = new();
    private readonly Func<DevelopmentRefreshRequest, CancellationToken, Task<bool>> _refresh;
    private readonly Func<bool> _activeRuns;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<DevelopmentRefreshArea, Work> _pending = new();
    private readonly Task _worker;
    private Work? _running;
    private CancellationTokenSource? _operation;
    private bool _enabled;
    private bool _pipelinesVisible;
    private bool _disposed;
    private DateTimeOffset _lastPipelineAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextPipeline = DateTimeOffset.MinValue;
    private int _failures;

    public DevelopmentRefreshCoordinator(Func<DevelopmentRefreshRequest, CancellationToken, Task<bool>> refresh,
        Func<bool> activeRuns, TimeProvider? clock = null)
    {
        _refresh = refresh;
        _activeRuns = activeRuns;
        _clock = clock ?? TimeProvider.System;
        _worker = Task.Run(RunAsync);
    }

    public static TimeSpan PipelineDelay(bool visible, bool activeRuns, int failures = 0) => failures > 0
        ? TimeSpan.FromSeconds(Math.Min(300, 30 * Math.Pow(2, Math.Min(failures - 1, 4))))
        : TimeSpan.FromSeconds(visible && activeRuns ? 15 : 60);

    public void SetActivity(bool enabled, bool pipelinesVisible)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _enabled = enabled;
            _pipelinesVisible = pipelinesVisible;
            if (!enabled)
            {
                _operation?.Cancel();
                foreach (var work in _pending.Values) work.Completion.TrySetCanceled();
                _pending.Clear();
            }
            else if (_lastPipelineAttempt != DateTimeOffset.MinValue)
            {
                var due = _lastPipelineAttempt + PipelineDelay(_pipelinesVisible, _activeRuns(), _failures);
                if (due < _nextPipeline) _nextPipeline = due;
            }
        }
        Wake();
    }

    public void SetVisibleArea(DevelopmentRefreshArea? area)
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var key in _pending.Keys.Where(key => key != DevelopmentRefreshArea.Pipelines && key != area).ToArray())
            {
                _pending[key].Completion.TrySetCanceled();
                _pending.Remove(key);
            }
            if (_running is { } work && work.Request.Area != DevelopmentRefreshArea.Pipelines && work.Request.Area != area)
                _operation?.Cancel();
        }
    }

    public void InvalidatePipelines()
    {
        lock (_gate) { if (_disposed) return; _nextPipeline = DateTimeOffset.MinValue; _failures = 0; }
        Wake();
    }

    public Task<bool> RequestAsync(DevelopmentRefreshArea area, bool history = false, bool followUp = false)
    {
        lock (_gate)
        {
            if (_disposed || !_enabled) return Task.FromCanceled<bool>(new CancellationToken(true));
            if (!followUp && _running is { } running && running.Request.Area == area && (!history || running.Request.History))
                return running.Completion.Task;
            if (_pending.TryGetValue(area, out var existing))
            {
                if (history && !existing.Request.History)
                    _pending[area] = existing with { Request = existing.Request with { History = true } };
                return existing.Completion.Task;
            }
            var work = new Work(new(area, history), new(TaskCreationOptions.RunContinuationsAsynchronously));
            _pending[area] = work;
            Wake();
            return work.Completion.Task;
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                Work? work = null;
                TimeSpan delay;
                lock (_gate)
                {
                    var now = _clock.GetUtcNow();
                    if (_enabled && _pending.Count > 0)
                    {
                        var first = _pending.First();
                        work = first.Value;
                        _pending.Remove(first.Key);
                    }
                    else if (_enabled && now >= _nextPipeline)
                        work = new(new(DevelopmentRefreshArea.Pipelines), new(TaskCreationOptions.RunContinuationsAsynchronously));
                    delay = !_enabled ? Timeout.InfiniteTimeSpan : _nextPipeline > now ? _nextPipeline - now : TimeSpan.Zero;
                    if (work != null)
                    {
                        _running = work;
                        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    }
                }
                if (work == null) { await WaitAsync(delay); continue; }
                using var operation = _operation!;
                var success = false;
                try { success = await _refresh(work.Request, operation.Token); }
                catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
                catch { /* The owner reports provider failures; a failed cycle still backs off. */ }
                lock (_gate)
                {
                    if (operation.IsCancellationRequested) work.Completion.TrySetCanceled();
                    else
                    {
                        if (work.Request.Area == DevelopmentRefreshArea.Pipelines)
                        {
                            _lastPipelineAttempt = _clock.GetUtcNow();
                            _failures = success ? 0 : Math.Min(5, _failures + 1);
                            _nextPipeline = _lastPipelineAttempt + PipelineDelay(_pipelinesVisible, _activeRuns(), _failures);
                        }
                        work.Completion.TrySetResult(success);
                    }
                    _running = null;
                    _operation = null;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task WaitAsync(TimeSpan delay)
    {
        using var wake = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var signal = _wake.WaitAsync(wake.Token);
        var timer = Task.Delay(delay, _clock, wake.Token);
        await Task.WhenAny(signal, timer);
        wake.Cancel();
        _lifetime.Token.ThrowIfCancellationRequested();
    }

    private void Wake()
    {
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _enabled = false;
            _lifetime.Cancel();
            _operation?.Cancel();
            foreach (var work in _pending.Values) work.Completion.TrySetCanceled();
            _pending.Clear();
        }
        Wake();
    }

    public async ValueTask DisposeAsync() { Dispose(); await _worker; }
}
