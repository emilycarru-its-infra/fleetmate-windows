using FleetMate.Core.Models.Projects;
using Serilog;

namespace FleetMate.GUI;

/// <summary>
/// What the tab widgets need beyond the shared caches: which caches are
/// loading right now, so a card shows a skeleton and a spinner instead of
/// disappearing, and the signed-in user's open work items, which no tab
/// list holds.
/// </summary>
public partial class App
{
    private readonly HashSet<string> _loadingCaches = new();

    /// <summary>Whether the cache named <paramref name="key"/> is being fetched now.</summary>
    public bool IsCacheLoading(string key)
    {
        lock (_loadingCaches) return _loadingCaches.Contains(key);
    }

    /// <summary>Mark <paramref name="key"/> as loading until the returned scope is disposed; widgets redraw at both ends.</summary>
    public IDisposable TrackCacheLoad(string key)
    {
        lock (_loadingCaches) _loadingCaches.Add(key);
        NotifyCacheChanged(key);
        return new LoadScope(this, key);
    }

    private sealed class LoadScope(App app, string key) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            lock (app._loadingCaches) app._loadingCaches.Remove(key);
            app.NotifyCacheChanged(key);
        }
    }

    /// <summary>
    /// Every open work item assigned to the signed-in user, across the
    /// organization — the Projects widgets' count, donut and list. Null until
    /// the first load finishes.
    /// </summary>
    public List<WorkItem>? CachedMyWorkItems { get; private set; }

    private DateTime? _myWorkItemsTime;

    /// <summary>A failed or signed-out load is retried no sooner than this, so a redraw never loops on it.</summary>
    private static readonly TimeSpan MyWorkItemsRetry = TimeSpan.FromMinutes(2);

    /// <summary>Fetch <see cref="CachedMyWorkItems"/> unless it is loading or fresh.</summary>
    public async Task LoadMyWorkItemsAsync(bool force = false)
    {
        if (DevOpsService is not { } devOps || IsCacheLoading(MyWorkItemsKey)) return;
        if (!force && _myWorkItemsTime is { } at && DateTime.Now - at < (CachedMyWorkItems is { Count: > 0 } ? CacheDuration : MyWorkItemsRetry))
            return;

        using var scope = TrackCacheLoad(MyWorkItemsKey);
        try
        {
            var items = await Task.Run(() => devOps.GetMyOpenWorkItemsAsync());
            await Dispatcher.InvokeAsync(() => CachedMyWorkItems = items);
            Log.Information("Loaded {Count} of my open work items", items.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load my open work items");
        }
        finally
        {
            _myWorkItemsTime = DateTime.Now;
        }
    }

    public const string MyWorkItemsKey = "MyWorkItems";
}
