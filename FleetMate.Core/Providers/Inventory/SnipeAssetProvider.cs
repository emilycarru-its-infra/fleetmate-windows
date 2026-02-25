using FleetMate.Core.Models.Inventory;
using FleetMate.Core.Services.Inventory;

namespace FleetMate.Core.Providers.Inventory;

/// <summary>
/// Wraps SnipeService to expose Snipe-IT assets through the unified IAssetProvider interface.
/// </summary>
public class SnipeAssetProvider : IAssetProvider
{
    private readonly SnipeService _snipeService;

    public string ProviderId => "snipe";
    public string ProviderName => "Snipe-IT";
    public bool IsEnabled => true;

    public SnipeAssetProvider(SnipeService snipeService)
    {
        _snipeService = snipeService;
    }

    public Task<bool> AuthenticateAsync(CancellationToken ct = default) =>
        Task.FromResult(true);

    public async Task<List<UnifiedAsset>> ListAssetsAsync(AssetFilter? filter = null, CancellationToken ct = default)
    {
        var assets = filter?.SearchQuery != null
            ? await _snipeService.SearchAssetsAsync(filter.SearchQuery)
            : await _snipeService.GetAssetsAsync();

        return assets.Select(ToUnified).ToList();
    }

    public async Task<UnifiedAsset?> GetAssetAsync(string assetId, CancellationToken ct = default)
    {
        if (!int.TryParse(assetId, out var id)) return null;
        var asset = await _snipeService.GetAssetAsync(id);
        return asset != null ? ToUnified(asset) : null;
    }

    public async Task<UnifiedAsset?> GetAssetByTagAsync(string assetTag, CancellationToken ct = default)
    {
        var asset = await _snipeService.GetAssetByTagAsync(assetTag);
        return asset != null ? ToUnified(asset) : null;
    }

    public async Task<List<UnifiedAsset>> SearchAssetsAsync(string query, CancellationToken ct = default)
    {
        var assets = await _snipeService.SearchAssetsAsync(query);
        return assets.Select(ToUnified).ToList();
    }

    private static UnifiedAsset ToUnified(SnipeAsset a) => new()
    {
        Id = a.Id.ToString(),
        ProviderId = "snipe",
        AssetTag = a.AssetTag,
        Name = a.Name,
        Serial = a.Serial,
        ModelName = a.Model?.Name,
        CategoryName = a.Category?.Name,
        StatusLabel = a.StatusLabel?.Name,
        AssignedTo = a.AssignedTo?.Name,
        LocationName = a.Location?.Name,
        PurchaseDate = a.PurchaseDate?.Datetime,
        PurchaseCost = a.PurchaseCostDecimal,
        OrderNumber = a.OrderNumber,
        Notes = a.Notes,
        CustomFields = a.CustomFields?.ToDictionary(
            kv => kv.Key,
            kv => kv.Value?.Value ?? ""),
    };
}
