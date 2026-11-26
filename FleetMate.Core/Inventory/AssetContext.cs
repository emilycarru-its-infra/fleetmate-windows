using FleetMate.Core.Models.Inventory;
using FleetMate.Core.Services.Terminal;

namespace FleetMate.Core.Inventory;

/// <summary>The selected asset as the FLEETMATE_CONTEXT file describes it to terminal sessions, in both Windows apps.</summary>
public static class AssetContext
{
    public static IReadOnlyList<ContextSelection> Selection(SnipeAsset? asset) =>
        asset == null ? Array.Empty<ContextSelection>() : new[]
        {
            new ContextSelection("asset", asset.Id.ToString(), new Dictionary<string, string?>
            {
                ["name"] = asset.DisplayName,
                ["tag"] = asset.AssetTag,
                ["serial"] = asset.Serial,
                ["model"] = asset.Model?.Name,
                ["assignedTo"] = asset.AssignedTo?.Name,
                ["status"] = asset.StatusLabel?.Name,
            }),
        };
}
