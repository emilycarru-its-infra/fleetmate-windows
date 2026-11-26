using FleetMate.Core.Config;
using FleetMate.Core.Inventory;
using FleetMate.Core.Knowledge;
using FleetMate.Core.Models;
using FleetMate.Core.Models.Inventory;
using FleetMate.Core.Services.Inventory;
using Serilog;

namespace FleetMate.WinUI.Shell;

/// <summary>
/// The Inventory tab's services, following FleetMate.GUI's App: Snipe-IT, the
/// assets cache every tab reads (Inventory, search, and Devices' "missing from
/// Inventory" check), and the Handbook the asset detail's Handbook card
/// searches. Demo mode serves <see cref="InventoryDemoData"/> and calls nothing.
///
/// <code>
/// App.Services.CacheChanged += key => { if (key == "Assets") Render(); };
/// await App.Services.LoadAssetsAsync(force: true);
/// </code>
/// </summary>
public sealed partial class ShellServices
{
    private Task? _assetsLoad;
    private List<SnipeAsset> _cachedAssets = new();
    private List<SnipeStatusLabelFull>? _statusLabels;
    private Dictionary<string, List<string>>? _listboxOptions;
    private HandbookIndex? _demoHandbook;

    /// <summary>Snipe-IT: null when no Snipe-IT address is configured, and always in demo mode.</summary>
    public SnipeService? Snipe { get; private set; }

    /// <summary>The Handbook, when its repository is configured; null in demo mode.</summary>
    public HandbookStore? Handbook { get; private set; }

    /// <summary>The Handbook pages to search: the live index, or demo mode's fictional pages.</summary>
    public HandbookIndex? HandbookIndex => _demoHandbook ?? (Handbook is { IsConfigured: true } store ? store.Index : null);

    /// <summary>Raised on the UI thread when the Handbook index changes.</summary>
    public event Action? HandbookChanged;

    /// <summary>Snipe-IT is configured, or demo mode has fixtures, so the Inventory tab has something to show.</summary>
    public bool IsInventoryConfigured => Snipe != null || DemoMode;

    /// <summary>The Snipe-IT address assets link to; null when there is none.</summary>
    public string? SnipeWebUrl => Snipe?.BaseUrl ?? (DemoMode ? "https://inventory.example.com" : null);

    /// <summary>Every asset, once loaded. Replaced, never changed in place; <see cref="CacheChanged"/> raises "Assets".</summary>
    public List<SnipeAsset> CachedAssets
    {
        get => _cachedAssets;
        set { _cachedAssets = value; NotifyCacheChanged(InventoryGraphCards.CacheKey); }
    }

    /// <summary>Why the assets could not be loaded, or null.</summary>
    public string? AssetsLoadError { get; private set; }

    // MARK: - Links

    /// <summary>An asset a <c>fleetmate:</c> link asked for, waiting for the Inventory tab.</summary>
    public int? PendingAssetId { get; set; }

    /// <summary>Raised after an asset link is queued, for an Inventory tab that is already showing.</summary>
    public event Action? AssetLinkRequested;

    public void RequestAssetLink(int assetId)
    {
        PendingAssetId = assetId;
        AssetLinkRequested?.Invoke();
    }

    // MARK: - Demo mode

    /// <summary>Demo mode's fixture (registered in App): fictional assets, statuses and Handbook pages.</summary>
    public void SeedInventoryDemo()
    {
        _ui ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _cachedAssets = InventoryDemoData.Assets(DateTime.Now);
        _statusLabels = InventoryDemoData.StatusLabels();
        _listboxOptions = new();
        _demoHandbook = InventoryDemoData.Handbook();
    }

    // MARK: - Loading

    /// <summary>
    /// Load every asset into <see cref="CachedAssets"/>. Concurrent callers
    /// share one load; <paramref name="force"/> skips Snipe-IT's own cache.
    /// A failure is kept in <see cref="AssetsLoadError"/>, never thrown.
    /// </summary>
    public Task LoadAssetsAsync(bool force = false)
    {
        if (DemoMode || Snipe == null) return Task.CompletedTask;
        if (_assetsLoad is { IsCompleted: false } running) return running;
        return _assetsLoad = LoadAssetsCoreAsync(Snipe, force);
    }

    private async Task LoadAssetsCoreAsync(SnipeService snipe, bool force)
    {
        using var loading = TrackCacheLoad(InventoryGraphCards.CacheKey);
        try
        {
            var assets = await snipe.GetAssetsAsync(forceRefresh: force);
            PostToUi(() =>
            {
                AssetsLoadError = null;
                CachedAssets = assets;
            });
            Log.Information("Loaded {Count} assets", assets.Count);
        }
        catch (Exception ex)
        {
            var reason = ex is SnipeException ? ex.Message : SnipeService.Describe(ex);
            Log.Warning("[snipe] Failed to load assets: {Reason}", reason);
            PostToUi(() =>
            {
                AssetsLoadError = reason;
                NotifyCacheChanged(InventoryGraphCards.CacheKey);
            });
        }
    }

    /// <summary>One asset by id, from the cache or else Snipe-IT; null when there is none.</summary>
    public async Task<SnipeAsset?> FindAssetAsync(int id)
    {
        if (CachedAssets.FirstOrDefault(a => a.Id == id) is { } cached) return cached;
        if (Snipe == null) return null;
        var asset = await Snipe.GetAssetAsync(id);
        if (asset != null) CachedAssets = CachedAssets.Append(asset).ToList();
        return asset;
    }

    /// <summary>The asset's history, newest first; demo mode's fictional events.</summary>
    public async Task<List<SnipeHistoryEntry>> GetAssetHistoryAsync(SnipeAsset asset)
    {
        if (DemoMode) return InventoryDemoData.History(asset, DateTime.Now);
        if (Snipe == null) return new();
        var rows = await Snipe.GetAssetHistoryAsync(asset.Id);
        return rows.Select(SnipeHistory.ToEntry).ToList();
    }

    /// <summary>The statuses the Status picker offers, fetched once; empty when they cannot be had.</summary>
    public async Task<IReadOnlyList<SnipeStatusLabelFull>> GetStatusLabelsAsync()
    {
        if (_statusLabels != null || Snipe == null) return _statusLabels ?? new();
        try
        {
            _statusLabels = await Snipe.GetStatusLabelsAsync();
        }
        catch (SnipeException)
        {
            // Logged by the service; the picker shows the status as text.
            return new List<SnipeStatusLabelFull>();
        }
        return _statusLabels;
    }

    /// <summary>Listbox custom fields' choices, by db column, fetched once.</summary>
    public async Task<IReadOnlyDictionary<string, List<string>>> GetListboxOptionsAsync()
    {
        if (_listboxOptions != null || Snipe == null) return _listboxOptions ?? new();
        _listboxOptions = AssetDetailLayout.ListboxOptions(await Snipe.GetFieldDefinitionsAsync());
        return _listboxOptions;
    }

    // MARK: - Lifetime

    /// <summary>Build Inventory's services from the configuration. Called from the constructor, on the UI thread.</summary>
    private void InitializeInventory(FleetMateConfig config)
    {
        if (!string.IsNullOrEmpty(config.SnipeUrl))
        {
            try
            {
                Snipe = SnipeService.FromConfig(config);
                if (Snipe.MissingCredentialReason is { } missing)
                    Log.Warning("SnipeService initialized without a credential: {Reason}", missing);
                else
                    Log.Information("SnipeService initialized ({Auth})", Snipe.UsesOidc ? "Entra SSO" : "legacy API key");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Snipe-IT could not be set up");
            }
        }
    }

    private void StartInventory()
    {
        _ = LoadAssetsAsync();
        // The Handbook signs in with Azure DevOps, which the Development services hold.
        Handbook = new HandbookStore(Config, async () => DevOpsSsoService is { } sso ? await sso.GetValidTokenAsync() : null);
        Handbook.Changed += (_, _) => PostToUi(() => HandbookChanged?.Invoke());
        Handbook.Start();
    }

    private void DisposeInventory()
    {
        Handbook?.Stop();
        Snipe?.Dispose();
    }
}
