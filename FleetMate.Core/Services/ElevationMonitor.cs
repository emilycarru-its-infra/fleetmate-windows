using FleetMate.Core.Config;
using Serilog;

namespace FleetMate.Core.Services;

/// <summary>
/// Tracks the desktop app's elevation sessions for the window chrome, and
/// optionally starts them at launch.
///
/// The signal is Azure's, not a local guess: <c>az container show</c> on
/// <c>aze-{domain}-{user}</c>, reading <c>instanceView.state</c> and the
/// <c>expires</c> tag. On top of that, <see cref="ElevationSession.CreatingChanged"/>
/// covers the half-minute inside the create call, before Azure has a container
/// to report on. Between polls the expires tag lets Ready flip to Expired on
/// time without another az call.
/// </summary>
public sealed class ElevationMonitor : IDisposable
{
    /// <summary>The domains the desktop app's Graph transport uses.</summary>
    public static readonly GraphDomain[] DesktopDomains = { GraphDomain.Devices, GraphDomain.Identity };

    private readonly ElevationConfig? _config;
    private readonly bool _enabled;
    private readonly object _gate = new();
    private readonly Dictionary<GraphDomain, ElevationSessionInfo> _info = new();
    private readonly HashSet<GraphDomain> _creating = new();
    private readonly Dictionary<GraphDomain, string> _errors = new();
    private Timer? _timer;

    public event Action? Changed;

    public ElevationMonitor(ElevationConfig? config, bool directTransport)
    {
        _config = config;
        _enabled = config is { IsConfigured: true } && !directTransport;
        ElevationSession.CreatingChanged += OnCreatingChanged;
    }

    public bool Enabled => _enabled;

    /// <summary>Current state of one domain.</summary>
    public ElevationSessionState StateOf(GraphDomain domain, DateTimeOffset? now = null)
    {
        if (!_enabled) return ElevationSessionState.Off;
        lock (_gate)
        {
            var creating = _creating.Contains(domain);
            if (_info.TryGetValue(domain, out var info))
                return info.Classify(now ?? DateTimeOffset.UtcNow, creating);
            if (creating) return ElevationSessionState.Starting;
            return ElevationSessionState.Unknown;
        }
    }

    public DateTimeOffset? ExpiresOf(GraphDomain domain)
    {
        lock (_gate) return _info.TryGetValue(domain, out var i) ? i.Expires : null;
    }

    public string? ErrorOf(GraphDomain domain)
    {
        lock (_gate) return _errors.GetValueOrDefault(domain);
    }

    /// <summary>
    /// One state for the chrome: the one that most needs the operator's
    /// attention wins, so Ready means every desktop domain is ready.
    /// </summary>
    public ElevationSessionState Overall(DateTimeOffset? now = null) =>
        Aggregate(DesktopDomains.Select(d => StateOf(d, now)));

    public static ElevationSessionState Aggregate(IEnumerable<ElevationSessionState> states)
    {
        var list = states.ToList();
        if (list.Count == 0 || list.All(s => s == ElevationSessionState.Off)) return ElevationSessionState.Off;
        foreach (var s in new[]
                 {
                     ElevationSessionState.Starting, ElevationSessionState.Unknown,
                     ElevationSessionState.Expired, ElevationSessionState.None,
                 })
            if (list.Contains(s)) return s;
        return ElevationSessionState.Ready;
    }

    /// <summary>
    /// Begin polling and, when configured, start whatever is not running.
    /// Returns immediately; work happens in the background.
    /// </summary>
    public void Start(TimeSpan? pollInterval = null)
    {
        if (!_enabled) return;
        var interval = pollInterval ?? TimeSpan.FromMinutes(5);
        _timer = new Timer(_ => _ = RefreshAsync(), null, TimeSpan.Zero, interval);
        if (PrewarmEnabled(_config!)) _ = PrewarmAsync();
    }

    /// <summary>
    /// Config decides, and FLEETMATE_ELEVATION_PREWARM=0 overrides it for one
    /// run — for launching the app without starting cloud sessions.
    /// </summary>
    internal static bool PrewarmEnabled(ElevationConfig config, string? env = null)
    {
        env ??= Environment.GetEnvironmentVariable("FLEETMATE_ELEVATION_PREWARM");
        if (env is "0" || string.Equals(env, "false", StringComparison.OrdinalIgnoreCase)) return false;
        return config.PrewarmOnLaunch;
    }

    /// <summary>Read every desktop domain's state from Azure.</summary>
    public async Task RefreshAsync()
    {
        if (!_enabled) return;
        await Task.WhenAll(DesktopDomains.Select(RefreshDomainAsync));
        Changed?.Invoke();
    }

    private async Task RefreshDomainAsync(GraphDomain domain)
    {
        try
        {
            var info = await new ElevationSession(_config!).GetSessionInfoAsync(domain);
            lock (_gate)
            {
                _info[domain] = info;
                _errors.Remove(domain);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Elevation status check failed for {Domain}", domain.Slug());
            lock (_gate)
            {
                _info.Remove(domain);
                _errors[domain] = ex.Message;
            }
        }
    }

    /// <summary>
    /// Create the session for each desktop domain that is not already running.
    /// One session object per domain, so the two boots run side by side
    /// rather than queueing behind one gate.
    /// </summary>
    public async Task PrewarmAsync()
    {
        if (!_enabled) return;
        await RefreshAsync();
        var cold = DesktopDomains
            .Where(d => StateOf(d) is ElevationSessionState.None or ElevationSessionState.Expired)
            .ToList();
        if (cold.Count == 0) return;

        Log.Information("Pre-warming elevation sessions: {Domains}", string.Join(", ", cold.Select(d => d.Slug())));
        await Task.WhenAll(cold.Select(async d =>
        {
            try { await new ElevationSession(_config!).EnsureSessionAsync(d); }
            catch (Exception ex)
            {
                Log.Warning("Elevation pre-warm failed for {Domain}: {Message}", d.Slug(), ex.Message);
                lock (_gate) _errors[d] = ex.Message;
            }
        }));
        await RefreshAsync();
    }

    private void OnCreatingChanged(GraphDomain domain, bool creating)
    {
        lock (_gate)
        {
            if (creating) _creating.Add(domain);
            else _creating.Remove(domain);
        }
        Changed?.Invoke();
        // Creation finishing means Azure now has something new to report.
        if (!creating && _enabled) _ = RefreshAsync();
    }

    public void Dispose()
    {
        ElevationSession.CreatingChanged -= OnCreatingChanged;
        _timer?.Dispose();
    }
}
