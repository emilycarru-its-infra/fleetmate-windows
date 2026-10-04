using System.IO;
using FleetMate.Core.Config;
using FleetMate.Core.Knowledge;
using Serilog;

namespace FleetMate.GUI.Knowledge;

/// <summary>
/// The Handbook, from FleetMate's own copy of its repository: always the
/// remote's main branch, never the person's checkout, refreshed at launch
/// and every fifteen minutes (macOS parity). Unset in config, it does nothing
/// and every Handbook feature stays hidden.
/// </summary>
public sealed class HandbookStore
{
    /// <summary>The Hugo content folder inside the repository; the sparse checkout takes only these.</summary>
    private static readonly string[] ContentFolders = { "website/content", "content" };

    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    private readonly RepoMirror? _mirror;
    private readonly string? _siteUrl;
    private readonly Func<Task<string?>> _token;
    private CancellationTokenSource? _loop;

    public HandbookIndex Index { get; private set; } = HandbookIndex.Empty;
    public DateTime? SyncedAt { get; private set; }
    public string? SyncError { get; private set; }
    public bool IsConfigured => _mirror != null;

    /// <summary>Raised on the thread pool after the index changes.</summary>
    public event EventHandler? Changed;

    public HandbookStore(FleetMateConfig config, Func<Task<string?>> token)
    {
        _token = token;
        _siteUrl = config.HandbookSiteUrl;
        if (string.IsNullOrWhiteSpace(config.HandbookRepoUrl)) return;

        // The DevOps sign-in only ever goes to the DevOps server.
        string? devOpsHost = null;
        if (config.AzureDevOps?.HostUrl is { Length: > 0 } host && Uri.TryCreate(host, UriKind.Absolute, out var hostUri))
            devOpsHost = hostUri.Host;
        _mirror = new RepoMirror("handbook", config.HandbookRepoUrl, ContentFolders, tokenHost: devOpsHost);
    }

    /// <summary>Show what is already on disk at once, then keep it current.</summary>
    public void Start()
    {
        if (_mirror == null || _loop != null) return;
        _loop = new CancellationTokenSource();
        var ct = _loop.Token;
        _ = Task.Run(async () =>
        {
            if (_mirror.IsCloned) Load();
            while (!ct.IsCancellationRequested)
            {
                await SyncAsync(ct);
                try { await Task.Delay(RefreshInterval, ct); }
                catch (OperationCanceledException) { break; }
            }
        }, ct);
    }

    public void Stop() => _loop?.Cancel();

    private async Task SyncAsync(CancellationToken ct)
    {
        if (_mirror == null) return;
        try
        {
            string? token = null;
            try { token = await _token(); }
            catch (Exception ex) { Log.Debug(ex, "Handbook: no DevOps token; trying git's own credentials"); }

            var head = await _mirror.SyncAsync(token, ct);
            SyncedAt = DateTime.Now;
            SyncError = null;
            Log.Information("Handbook synced at {Head}", head);
            Load();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SyncError = RepoMirror.Scrub(ex.Message);
            Log.Warning("Handbook sync failed: {Reason}", SyncError);
        }
    }

    private void Load()
    {
        var content = ContentFolders.Select(f => Path.Combine(_mirror!.LocalPath, f.Replace('/', Path.DirectorySeparatorChar)))
            .FirstOrDefault(Directory.Exists);
        if (content == null) return;
        Index = HandbookIndex.Load(content);
        Log.Information("Handbook index: {Count} pages", Index.Pages.Count);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Where a page opens: the published site when its address is configured,
    /// otherwise FleetMate's local copy of the page.
    /// </summary>
    public string? OpenTarget(HandbookPage page)
    {
        if (HandbookSite.PageUrl(_siteUrl, page) is { } url) return url.ToString();
        if (_mirror == null) return null;
        foreach (var folder in ContentFolders)
        {
            var file = Path.Combine(_mirror.LocalPath, folder.Replace('/', Path.DirectorySeparatorChar),
                page.Path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(file)) return file;
        }
        return null;
    }
}
