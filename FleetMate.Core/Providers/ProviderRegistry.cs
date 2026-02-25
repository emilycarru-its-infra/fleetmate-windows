namespace FleetMate.Core.Providers;

/// <summary>
/// Central registry that holds all enabled providers and provides multi-provider queries.
/// </summary>
public class ProviderRegistry
{
    private readonly List<IManagementProvider> _managementProviders = new();
    private readonly List<IAssetProvider> _assetProviders = new();
    private readonly List<ITicketProvider> _ticketProviders = new();
    private readonly List<IGroupProvider> _groupProviders = new();

    public IReadOnlyList<IManagementProvider> ManagementProviders => _managementProviders;
    public IReadOnlyList<IAssetProvider> AssetProviders => _assetProviders;
    public IReadOnlyList<ITicketProvider> TicketProviders => _ticketProviders;
    public IReadOnlyList<IGroupProvider> GroupProviders => _groupProviders;

    public void Register(IManagementProvider provider) => _managementProviders.Add(provider);
    public void Register(IAssetProvider provider) => _assetProviders.Add(provider);
    public void Register(ITicketProvider provider) => _ticketProviders.Add(provider);
    public void Register(IGroupProvider provider) => _groupProviders.Add(provider);

    /// <summary>List devices across all management providers.</summary>
    public async Task<List<UnifiedManagedDevice>> ListAllDevicesAsync(DeviceFilter? filter = null, CancellationToken ct = default)
    {
        var results = new List<UnifiedManagedDevice>();
        foreach (var provider in _managementProviders.Where(p => p.IsEnabled))
        {
            var devices = await provider.ListDevicesAsync(filter, ct);
            results.AddRange(devices);
        }
        return results;
    }

    /// <summary>List assets across all asset providers.</summary>
    public async Task<List<UnifiedAsset>> ListAllAssetsAsync(AssetFilter? filter = null, CancellationToken ct = default)
    {
        var results = new List<UnifiedAsset>();
        foreach (var provider in _assetProviders.Where(p => p.IsEnabled))
        {
            var assets = await provider.ListAssetsAsync(filter, ct);
            results.AddRange(assets);
        }
        return results;
    }

    /// <summary>List tickets across all ticket providers.</summary>
    public async Task<List<UnifiedTicket>> ListAllTicketsAsync(TicketFilter? filter = null, CancellationToken ct = default)
    {
        var results = new List<UnifiedTicket>();
        foreach (var provider in _ticketProviders.Where(p => p.IsEnabled))
        {
            var tickets = await provider.ListTicketsAsync(filter, ct);
            results.AddRange(tickets);
        }
        return results;
    }

    /// <summary>Search devices across all management providers.</summary>
    public async Task<List<UnifiedManagedDevice>> SearchAllDevicesAsync(string query, CancellationToken ct = default)
    {
        var results = new List<UnifiedManagedDevice>();
        foreach (var provider in _managementProviders.Where(p => p.IsEnabled))
        {
            var devices = await provider.SearchDevicesAsync(query, ct);
            results.AddRange(devices);
        }
        return results;
    }

    /// <summary>Search assets across all asset providers.</summary>
    public async Task<List<UnifiedAsset>> SearchAllAssetsAsync(string query, CancellationToken ct = default)
    {
        var results = new List<UnifiedAsset>();
        foreach (var provider in _assetProviders.Where(p => p.IsEnabled))
        {
            var assets = await provider.SearchAssetsAsync(query, ct);
            results.AddRange(assets);
        }
        return results;
    }
}
