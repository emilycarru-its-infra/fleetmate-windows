using System.IO;
using FleetMate.Core.Config;
using FleetMate.Core.Knowledge;
using Serilog;

namespace FleetMate.GUI.Knowledge;

/// <summary>
/// Development › Skills: the shared agent setup from FleetMate's own copy of
/// the hub repository (always the remote's main, refreshed at launch and every
/// fifteen minutes, as the Handbook is), plus this PC's own skills and hooks
/// under <c>%USERPROFILE%\.claude</c> (macOS parity). Without a hub address
/// configured it shows only the local ones.
/// </summary>
public sealed class SkillsStore
{
    private readonly RepoMirror? _mirror;
    private readonly Func<Task<string?>> _token;
    private CancellationTokenSource? _loop;

    public IReadOnlyList<SkillEntry> Entries { get; private set; } = Array.Empty<SkillEntry>();
    public DateTime? SyncedAt { get; private set; }
    public string? SyncError { get; private set; }
    public bool IsSyncing { get; private set; }
    public bool IsHubConfigured => _mirror != null;

    /// <summary>Raised on the thread pool after the entries change.</summary>
    public event EventHandler? Changed;

    public SkillsStore(FleetMateConfig config, Func<Task<string?>> token)
    {
        _token = token;
        if (string.IsNullOrWhiteSpace(config.AgentsHubRepoUrl)) return;
        // The DevOps sign-in only ever goes to the DevOps server.
        string? devOpsHost = null;
        if (config.AzureDevOps?.HostUrl is { Length: > 0 } host && Uri.TryCreate(host, UriKind.Absolute, out var hostUri))
            devOpsHost = hostUri.Host;
        _mirror = new RepoMirror("agents-hub", config.AgentsHubRepoUrl, new[] { "agents" }, tokenHost: devOpsHost);
    }

    private static string ClaudeHome =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// <summary>Show what is already on disk at once, then keep it current.</summary>
    public void Start()
    {
        if (_loop != null) return;
        _loop = new CancellationTokenSource();
        var ct = _loop.Token;
        _ = Task.Run(async () =>
        {
            Load();
            if (_mirror == null) return;
            while (!ct.IsCancellationRequested)
            {
                await SyncAsync(ct);
                try { await Task.Delay(HandbookStore.RefreshInterval, ct); }
                catch (OperationCanceledException) { break; }
            }
        }, ct);
    }

    public void Stop() => _loop?.Cancel();

    /// <summary>This PC's own skills change when the person edits them; reread on demand.</summary>
    public Task ReloadLocalAsync() => Task.Run(Load);

    private async Task SyncAsync(CancellationToken ct)
    {
        if (_mirror == null) return;
        IsSyncing = true;
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            string? token = null;
            try { token = await _token(); }
            catch (Exception ex) { Log.Debug(ex, "Skills: no DevOps token; trying git's own credentials"); }

            var head = await _mirror.SyncAsync(token, ct);
            SyncedAt = DateTime.Now;
            SyncError = null;
            Log.Information("Agents hub synced at {Head}", head);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SyncError = RepoMirror.Scrub(ex.Message);
            Log.Warning("Agents hub sync failed: {Reason}", SyncError);
        }
        finally
        {
            IsSyncing = false;
            Load();
        }
    }

    private void Load()
    {
        var shared = _mirror is { IsCloned: true } ? SkillCatalog.Load(_mirror.LocalPath) : Array.Empty<SkillEntry>();
        Entries = shared.Concat(SkillCatalog.LoadLocal(ClaudeHome)).ToList();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
