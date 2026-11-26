using FleetMate.Core.Models.Inventory;

namespace FleetMate.Core.Inventory;

/// <summary>The Snipe-IT calls a re-allocation makes; <see cref="Services.Inventory.SnipeService"/> in the apps.</summary>
public interface IAssetCheckout
{
    Task<SnipeResponse?> CheckinAssetAsync(int assetId, SnipeCheckinRequest? request = null);
    Task<SnipeResponse?> CheckoutAssetAsync(int assetId, SnipeCheckoutRequest request);
}

/// <summary>
/// Re-allocate an asset to another person, as both Windows apps do: check it
/// in when it is checked out, then check it out to the new user. Returns
/// null on success, or the message to show.
/// </summary>
public static class AssetReallocation
{
    public const string DefaultNote = "Re-allocated via FleetMate";

    public static async Task<string?> RunAsync(IAssetCheckout snipe, SnipeAsset asset, int userId, string? note)
    {
        if (asset.AssignedTo != null)
        {
            var checkin = await snipe.CheckinAssetAsync(asset.Id, new SnipeCheckinRequest { Note = DefaultNote });
            if (checkin == null || !checkin.IsSuccess)
                return $"Check-in failed: {checkin?.Messages ?? "Unknown error"}";
        }

        var checkout = await snipe.CheckoutAssetAsync(asset.Id, new SnipeCheckoutRequest
        {
            AssignedUser = userId,
            CheckoutToType = "user",
            Note = string.IsNullOrWhiteSpace(note) ? DefaultNote : note.Trim(),
        });
        return checkout != null && checkout.IsSuccess ? null : $"Checkout failed: {checkout?.Messages ?? "Unknown error"}";
    }
}
