using System.Windows;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.GUI.Views.Shared.Widgets;
using Microsoft.Win32;
using Serilog;

namespace FleetMate.GUI.Views.Development;

/// <summary>One app-session owner. Views observe caches; navigation never creates another refresh loop.</summary>
public sealed class DevelopmentDataSync : IDisposable
{
    private sealed class ProviderState
    {
        public int Failures;
        public DateTimeOffset RetryAfter;
        public DateTimeOffset HistoryAt;
        public string? Error;
        public void Complete(bool success, DateTimeOffset now)
        {
            Failures = success ? 0 : Math.Min(5, Failures + 1);
            RetryAfter = success ? now : now + DevelopmentRefreshCoordinator.PipelineDelay(false, false, Failures);
        }
    }
    private static readonly TimeSpan HistoryInterval = TimeSpan.FromMinutes(15);
    private readonly App _app;
    private readonly Window? _window;
    private readonly DevelopmentRefreshCoordinator _coordinator;
    private readonly PipelineRefreshPlan _plan = new();
    private readonly Dictionary<DevelopmentRefreshArea, DateTimeOffset> _checked = new();
    private readonly Dictionary<DevelopmentRefreshArea, string?> _errors = new();
    private readonly HashSet<DevelopmentRefreshArea> _loading = new();
    private readonly Dictionary<DevelopmentRefreshArea, int> _historyFailures = new();
    private readonly Dictionary<DevelopmentRefreshArea, DateTimeOffset> _historyRetryAfter = new();
    private readonly ProviderState _github = new();
    private readonly ProviderState _azure = new();
    private List<PipelineRepositoryScope> _repositories = new();
    private List<string> _projects = new();
    private DateTimeOffset _repositoryDirectoryAt;
    private DateTimeOffset _projectDirectoryAt;
    private object? _viewOwner;
    private DevelopmentSegment? _segment;
    private string? _repository;
    private PipelineRun? _selected;
    private DevelopmentSourceFilter _source = DevelopmentSourceFilter.All;
    private bool _suspended;
    private bool _foreground;
    private bool _focusRefresh;
    private bool _disposed;

    public DevelopmentDataSync(App app)
    {
        _app = app;
        _window = app.MainWindow;
        _foreground = app.Windows.OfType<Window>().Any(window => window.IsActive);
        _coordinator = new DevelopmentRefreshCoordinator(
            (request, ct) => app.Dispatcher.InvokeAsync(() => RefreshAsync(request, ct)).Task.Unwrap(),
            () => app.DevelopmentRuns?.Any(run => run.Status.IsActive()
                && (_repository == null || CommitsAndPipelinesFilter.RunRepositoryKey(run) == _repository)
                && CommitsAndPipelinesFilter.MatchesSource(run.Source, _source)) == true);
        if (_window != null)
        {
            _window.StateChanged += OnActivityChanged;
        }
        app.Activated += OnApplicationActivated;
        app.Deactivated += OnApplicationDeactivated;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _app.DevOpsSignedIn += OnSignedIn;
        UpdateActivity();
    }

    internal bool IsDisposed => _disposed;
    public event EventHandler? StatusChanged;
    public DateTimeOffset? LastChecked(DevelopmentRefreshArea area) => _checked.TryGetValue(area, out var date) ? date : null;
    public bool IsLoading(DevelopmentRefreshArea area) => _loading.Contains(area);
    public string StatusSuffix(DevelopmentRefreshArea area) =>
        (_errors.GetValueOrDefault(area) is { Length: > 0 } error ? " · " + error : "")
        + (LastChecked(area) is { } date ? $" · checked {date.ToLocalTime():t}" : "");

    public void AttachView(object owner, DevelopmentSegment segment, string? repository = null, PipelineRun? selected = null, DevelopmentSourceFilter source = DevelopmentSourceFilter.All)
    {
        var changed = _viewOwner != owner || _segment != segment || _repository != repository || _source != source
            || (_selected == null ? selected != null : selected == null || PipelineRunCache.Key(_selected) != PipelineRunCache.Key(selected));
        _viewOwner = owner;
        _segment = segment;
        _repository = repository;
        _selected = selected;
        _source = source;
        _coordinator.SetVisibleArea(segment switch
        {
            DevelopmentSegment.Pulls => DevelopmentRefreshArea.PullRequests,
            DevelopmentSegment.Commits => DevelopmentRefreshArea.Commits,
            _ => DevelopmentRefreshArea.Pipelines,
        });
        UpdateActivity();
        if (changed)
        {
            if (segment == DevelopmentSegment.Pipelines && Active)
            {
                _focusRefresh = true;
                _ = RefreshNowAsync(DevelopmentRefreshArea.Pipelines, history: false, followUp: true);
            }
            else RequestStaleEntry();
        }
    }

    public void DetachView(object owner)
    {
        if (!ReferenceEquals(owner, _viewOwner)) return;
        _viewOwner = null;
        _segment = null;
        _repository = null;
        _selected = null;
        _source = DevelopmentSourceFilter.All;
        _coordinator.SetVisibleArea(null);
        UpdateActivity();
    }

    public async Task RefreshNowAsync(DevelopmentRefreshArea area, bool history = true, bool followUp = false)
    {
        try { await _coordinator.RequestAsync(area, history, followUp); }
        catch (OperationCanceledException) { }
    }

    private void OnSignedIn() => _app.Dispatcher.BeginInvoke(() =>
    {
        if (_disposed) return;
        _azure.Failures = 0;
        _azure.RetryAfter = DateTimeOffset.MinValue;
        _azure.HistoryAt = DateTimeOffset.MinValue;
        _projectDirectoryAt = DateTimeOffset.MinValue;
        _coordinator.InvalidatePipelines();
    });

    private bool Active => !_disposed && !_suspended && _foreground && _window is { WindowState: not WindowState.Minimized };
    private void OnApplicationActivated(object? sender, EventArgs e) { _foreground = true; OnActivityChanged(sender, e); }
    private void OnApplicationDeactivated(object? sender, EventArgs e) { _foreground = false; OnActivityChanged(sender, e); }
    private void OnActivityChanged(object? sender, EventArgs e) { UpdateActivity(); if (Active) RequestStaleEntry(); }
    private void UpdateActivity() => _coordinator.SetActivity(Active, _segment == DevelopmentSegment.Pipelines);
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e) => _app.Dispatcher.BeginInvoke(() =>
    {
        if (_disposed) return;
        if (e.Mode == PowerModes.Suspend) _suspended = true;
        else if (e.Mode == PowerModes.Resume) _suspended = false;
        UpdateActivity();
        if (e.Mode == PowerModes.Resume) RequestStaleEntry();
    });

    private void RequestStaleEntry()
    {
        if (!Active) return;
        var area = _segment switch
        {
            DevelopmentSegment.Pulls => DevelopmentRefreshArea.PullRequests,
            DevelopmentSegment.Commits => DevelopmentRefreshArea.Commits,
            DevelopmentSegment.Pipelines => DevelopmentRefreshArea.Pipelines,
            _ => (DevelopmentRefreshArea?)null,
        };
        if (area is not { } requested) return;
        if (requested != DevelopmentRefreshArea.Pipelines && _historyRetryAfter.GetValueOrDefault(requested) > DateTimeOffset.UtcNow) return;
        var age = LastChecked(requested) is { } checkedAt ? DateTimeOffset.UtcNow - checkedAt : TimeSpan.MaxValue;
        var interval = requested == DevelopmentRefreshArea.Pipelines ? TimeSpan.FromSeconds(15) : HistoryInterval;
        if (age >= interval) _ = RefreshNowAsync(requested, requested != DevelopmentRefreshArea.Pipelines);
    }

    private async Task<bool> RefreshAsync(DevelopmentRefreshRequest request, CancellationToken ct)
    {
        if (_disposed) return false;
        ct.ThrowIfCancellationRequested();
        using var tracking = request.Area != DevelopmentRefreshArea.Pipelines || request.History || _app.DevelopmentRuns == null
            ? _app.TrackCacheLoad(request.Area switch
            {
                DevelopmentRefreshArea.PullRequests => "PullRequests", DevelopmentRefreshArea.Commits => "Commits", _ => "Runs",
            }) : null;
        _loading.Add(request.Area);
        StatusChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            var success = request.Area switch
            {
                DevelopmentRefreshArea.PullRequests => await RefreshPullsAsync(ct),
                DevelopmentRefreshArea.Commits => await RefreshCommitsAsync(ct),
                _ => await RefreshPipelinesAsync(request.History, ct),
            };
            ct.ThrowIfCancellationRequested();
            if (success) _checked[request.Area] = DateTimeOffset.UtcNow;
            if (request.Area != DevelopmentRefreshArea.Pipelines)
            {
                var failures = success ? 0 : Math.Min(5, _historyFailures.GetValueOrDefault(request.Area) + 1);
                _historyFailures[request.Area] = failures;
                _historyRetryAfter[request.Area] = success ? DateTimeOffset.MinValue
                    : DateTimeOffset.UtcNow + DevelopmentRefreshCoordinator.PipelineDelay(false, false, failures);
            }
            return success || request.Area == DevelopmentRefreshArea.Pipelines && (_github.Failures == 0 || _azure.Failures == 0 && !string.IsNullOrWhiteSpace(_app.Config.AzureDevOps?.Organization));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "Development refresh failed for {Area}", request.Area);
            _errors[request.Area] = "Update unavailable";
            return false;
        }
        finally
        {
            _loading.Remove(request.Area);
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<bool> RefreshPipelinesAsync(bool history, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (history) _plan.PrioritizeSelection();
        var previous = _app.DevelopmentRuns ?? new();
        var focusRefresh = _focusRefresh;
        _focusRefresh = false;
        var updated = new List<PipelineRun>();
        var outcomes = await Task.WhenAll(RefreshGitHubAsync(), RefreshAzureAsync());
        ct.ThrowIfCancellationRequested();
        if (_disposed) return false;
        if (updated.Count > 0 || outcomes.All(success => success))
        {
            var merged = PipelineRunCache.Merge(previous, updated, DateTime.UtcNow, _selected);
            if (!PipelineRunCache.SameContent(_app.DevelopmentRuns, merged)) _app.DevelopmentRuns = merged;
            if (_selected != null) _selected = merged.FirstOrDefault(run => PipelineRunCache.Key(run) == PipelineRunCache.Key(_selected)) ?? _selected;
        }
        _errors[DevelopmentRefreshArea.Pipelines] = string.Join(" · ", new[] { _github.Error, _azure.Error }.Where(error => error != null));
        return outcomes.All(success => success);

        async Task<bool> RefreshGitHubAsync()
        {
            if (_segment == DevelopmentSegment.Pipelines && _source == DevelopmentSourceFilter.DevOps) return true;
            if (!history && !focusRefresh && now < _github.RetryAfter) return false;
            var fullHistory = history || now - _github.HistoryAt >= HistoryInterval;
            var succeeded = true;
            using var service = new GitHubActionsService(_app.Config.GitHubProviderOrDefault());
            try
            {
                if (history || now - _repositoryDirectoryAt >= HistoryInterval)
                {
                    try
                    {
                        var scopes = await service.GetPipelineRepositoriesAsync(ct);
                        _repositories = scopes.Select(scope => new PipelineRepositoryScope(scope.Owner, scope.Repository)).ToList();
                        _repositoryDirectoryAt = now;
                    }
                    catch { ct.ThrowIfCancellationRequested(); succeeded = false; }
                }
                var scopesFromCache = previous.Where(run => run.Source == PullRequestSource.GitHub && !string.IsNullOrEmpty(run.Repository))
                    .Select(run => new PipelineRepositoryScope(run.Container, run.Repository!));
                var scopesFromCommits = (_app.DevelopmentCommits ?? new()).Where(repo => repo.Source == PullRequestSource.GitHub)
                    .Select(repo => new PipelineRepositoryScope(repo.Container, repo.Repository));
                _repositories = _repositories.Concat(scopesFromCache).Concat(scopesFromCommits).Distinct().Take(500).ToList();
                var targets = fullHistory ? PipelineRefreshPlan.GitHubHistory(_repositories)
                    : _plan.GitHub(previous, _repositories, _selected, _repository);
                using var gate = new SemaphoreSlim(2);
                await Task.WhenAll(targets.Select(async target =>
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        var rows = target.RunId is { } id
                            ? new[] { await service.ReadPipelineRunAsync(target.Container, target.Repository!, id, ct) }.ToList()
                            : await service.GetRepositoryPipelineRunsAsync(target.Container, target.Repository!, DateTime.UtcNow.AddDays(-7), ct);
                        updated.AddRange(rows);
                    }
                    catch { ct.ThrowIfCancellationRequested(); succeeded = false; }
                    finally { gate.Release(); }
                }));
            }
            catch { ct.ThrowIfCancellationRequested(); succeeded = false; }
            ct.ThrowIfCancellationRequested();
            if (fullHistory && succeeded) _github.HistoryAt = DateTimeOffset.UtcNow;
            _github.Error = succeeded ? null : "GitHub unavailable";
            _github.Complete(succeeded, DateTimeOffset.UtcNow);
            return succeeded;
        }

        async Task<bool> RefreshAzureAsync()
        {
            if (string.IsNullOrWhiteSpace(_app.Config.AzureDevOps?.Organization)) { _azure.Error = null; return true; }
            if (_segment == DevelopmentSegment.Pipelines && _source == DevelopmentSourceFilter.GitHub) return true;
            if (!history && !focusRefresh && now < _azure.RetryAfter) return false;
            var fullHistory = history || now - _azure.HistoryAt >= HistoryInterval;
            var succeeded = true;
            using var service = new AzureDevOpsService(_app.Config.AzureDevOps!, ct, requireComplete: true);
            try
            {
                if (history || now - _projectDirectoryAt >= HistoryInterval)
                {
                    try { _projects = await service.GetPipelineProjectsAsync(); _projectDirectoryAt = now; }
                    catch { ct.ThrowIfCancellationRequested(); succeeded = false; }
                }
                _projects = _projects.Concat(previous.Where(run => run.Source == PullRequestSource.AzureDevOps).Select(run => run.Container))
                    .Concat(_selected?.Source == PullRequestSource.AzureDevOps ? new[] { _selected.Container } : Array.Empty<string>()).Distinct().Take(200).ToList();
                var targets = fullHistory ? PipelineRefreshPlan.AzureDevOpsHistory(_projects)
                    : _plan.AzureDevOps(previous, _projects, _selected, _repository);
                using var gate = new SemaphoreSlim(2);
                await Task.WhenAll(targets.Select(async target =>
                {
                    await gate.WaitAsync(ct);
                    try { updated.AddRange(await service.GetProjectPipelineRunsAsync(target.Container, DateTime.UtcNow.AddDays(-7), target.BuildIds)); }
                    catch { ct.ThrowIfCancellationRequested(); succeeded = false; }
                    finally { gate.Release(); }
                }));
            }
            catch { ct.ThrowIfCancellationRequested(); succeeded = false; }
            ct.ThrowIfCancellationRequested();
            if (fullHistory && succeeded) _azure.HistoryAt = DateTimeOffset.UtcNow;
            _azure.Error = succeeded ? null : "Azure DevOps unavailable";
            _azure.Complete(succeeded, DateTimeOffset.UtcNow);
            return succeeded;
        }
    }

    private async Task<bool> RefreshPullsAsync(CancellationToken ct)
    {
        var config = _app.Config;
        var previous = _app.DevelopmentPullRequests;
        var queue = new PullRequestQueue();
        var errors = new List<string>();
        if (!string.IsNullOrWhiteSpace(config.AzureDevOps?.Organization))
        {
            try
            {
                var result = await Task.Run(async () =>
                {
                    using var service = new AzureDevOpsService(config.AzureDevOps!, ct, requireComplete: true);
                    return await service.GetDevelopmentPullRequestsAsync();
                }, ct);
                ct.ThrowIfCancellationRequested();
                if (result.Errors.Count > 0) throw new InvalidOperationException("Azure DevOps queue unavailable.");
                queue.Merge(result);
            }
            catch { ct.ThrowIfCancellationRequested(); errors.Add("Azure DevOps unavailable"); Keep(PullRequestSource.AzureDevOps); }
        }
        try
        {
            var result = await Task.Run(async () =>
            {
                var gh = config.GitHubProviderOrDefault();
                using var service = new GitHubPullRequestService(gh);
                var owners = new[] { gh.Organization, gh.Owner }.Where(owner => !string.IsNullOrWhiteSpace(owner)).Cast<string>();
                return await service.GetDevelopmentPullRequestsAsync(owners, ct: ct);
            }, ct);
            ct.ThrowIfCancellationRequested();
            if (result.Errors.Count > 0) throw new InvalidOperationException("GitHub queue unavailable.");
            queue.Merge(result);
        }
        catch { ct.ThrowIfCancellationRequested(); errors.Add("GitHub unavailable"); Keep(PullRequestSource.GitHub); }
        if (errors.Count == 0 || queue.PullRequests.Count > 0) _app.DevelopmentPullRequests = queue;
        _errors[DevelopmentRefreshArea.PullRequests] = string.Join(" · ", errors);
        return errors.Count == 0;

        void Keep(PullRequestSource source)
        {
            if (previous == null) return;
            foreach (var pr in previous.PullRequests.Where(pr => pr.Source == source)) queue.Insert(pr);
            foreach (var name in previous.ViewerNames) queue.ViewerNames.Add(name);
        }
    }

    private async Task<bool> RefreshCommitsAsync(CancellationToken ct)
    {
        var old = _app.DevelopmentCommits ?? new();
        var rows = new List<RepositoryCommits>();
        var errors = new List<string>();
        if (!string.IsNullOrWhiteSpace(_app.Config.AzureDevOps?.Organization))
        {
            try
            {
                var config = _app.Config.AzureDevOps!;
                rows.AddRange(await Task.Run(async () =>
                {
                    using var service = new AzureDevOpsService(config, ct, requireComplete: true);
                    return await service.GetRecentCommitsAsync(DateTime.UtcNow.AddDays(-14));
                }, ct));
            }
            catch { ct.ThrowIfCancellationRequested(); errors.Add("Azure DevOps unavailable"); rows.AddRange(old.Where(row => row.Source == PullRequestSource.AzureDevOps)); }
        }
        try
        {
            var config = _app.Config.GitHubProviderOrDefault();
            rows.AddRange(await Task.Run(async () =>
            {
                using var service = new GitHubPullRequestService(config);
                var (login, organizations) = await service.GetViewerAsync(ct);
                var owners = new[] { config.Organization, config.Owner, login }.Concat(organizations).Where(owner => !string.IsNullOrWhiteSpace(owner)).Cast<string>();
                return await service.GetRecentCommitsAsync(owners, DateTime.UtcNow.AddDays(-14), ct: ct, requireComplete: true);
            }, ct));
        }
        catch { ct.ThrowIfCancellationRequested(); errors.Add("GitHub unavailable"); rows.AddRange(old.Where(row => row.Source == PullRequestSource.GitHub)); }
        ct.ThrowIfCancellationRequested();
        if (errors.Count == 0 || rows.Count > 0) _app.DevelopmentCommits = rows.OrderByDescending(row => row.LatestDate).Take(500).ToList();
        _errors[DevelopmentRefreshArea.Commits] = string.Join(" · ", errors);
        return errors.Count == 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _coordinator.Dispose();
        if (_window != null)
        {
            _window.StateChanged -= OnActivityChanged;
        }
        _app.Activated -= OnApplicationActivated;
        _app.Deactivated -= OnApplicationDeactivated;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _app.DevOpsSignedIn -= OnSignedIn;
    }
}
