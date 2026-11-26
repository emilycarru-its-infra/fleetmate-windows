using System.Globalization;
using FleetMate.Core.Models.Inventory;
using FleetMate.Core.Shared;

namespace FleetMate.Core.Inventory;

/// <summary>
/// The Inventory figures both Windows apps show: the KPI counts, the asset
/// categories and status types the graphs chart, and the Status and Status
/// Type filter values the list uses, so a graph click always finds its assets.
/// The Mac's rules.
/// </summary>
public static class InventoryFigures
{
    /// <summary>Assets per category, most first, top eight.</summary>
    public static List<ChartSlice> AssetCategorySlices(IEnumerable<SnipeAsset> assets) => assets
        .GroupBy(a => a.Category?.Name ?? "Uncategorized")
        .Select(g => new ChartSlice(g.Key, g.Count()))
        .OrderByDescending(s => s.Value)
        .Take(8)
        .ToList();

    /// <summary>
    /// The status type an asset is counted under by the Asset Status graph
    /// and filtered by in the Status Type filter: Snipe-IT's status meta
    /// (deployed, deployable, pending, archived…), else the status name, else
    /// "Unknown". Both read this, so a wedge always finds its assets.
    /// </summary>
    public static string AssetStatusType(SnipeAsset asset) =>
        !string.IsNullOrEmpty(asset.StatusLabel?.StatusMeta) ? asset.StatusLabel.StatusMeta
        : !string.IsNullOrEmpty(asset.StatusLabel?.Name) ? asset.StatusLabel.Name
        : "Unknown";

    /// <summary>The Status Type filter's value for an asset: its status type, capitalised for the list.</summary>
    public static string AssetStatusTypeFilterValue(SnipeAsset asset) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(AssetStatusType(asset).ToLowerInvariant());

    /// <summary>
    /// The Status filter's value for an asset: its own status name ("Ready to
    /// Deploy", "In Repair") exactly as Snipe-IT names it, or null when it has
    /// none. Several names can share one status type, so this is the finer of
    /// the two filters.
    /// </summary>
    public static string? AssetStatusNameFilterValue(SnipeAsset asset) =>
        string.IsNullOrWhiteSpace(asset.StatusLabel?.Name) ? null : asset.StatusLabel.Name.Trim();

    /// <summary>
    /// Assets per status type (see <see cref="AssetStatusType"/>), top five,
    /// each labelled "Status (count)", the Mac's grouping.
    /// </summary>
    public static List<ChartSlice> AssetStatusSlices(IEnumerable<SnipeAsset> assets) => assets
        .GroupBy(AssetStatusType)
        .Select(g => (Status: g.Key, Count: g.Count()))
        .OrderByDescending(s => s.Count)
        .Take(5)
        .Select(s => new ChartSlice($"{s.Status} ({s.Count})", s.Count))
        .ToList();

    /// <summary>Checked out to someone or something: Snipe-IT's "deployed" status meta.</summary>
    public static int DeployedCount(IEnumerable<SnipeAsset> assets) =>
        assets.Count(a => a.StatusLabel?.StatusMeta?.Equals("deployed", StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>Not checked out to anyone or anything.</summary>
    public static int UnassignedCount(IEnumerable<SnipeAsset> assets) => assets.Count(a => a.AssignedTo == null);

    /// <summary>
    /// The few words a graph or the asset count shows when the assets could
    /// not be loaded: a token that could not be had is a failed sign-in, which
    /// is fixed differently from Snipe-IT itself failing.
    /// </summary>
    public static string AssetsFailureHeadline(string? reason) =>
        reason is null ? "No asset data"
        : reason.StartsWith("Sign-in failed", StringComparison.OrdinalIgnoreCase)
          || reason.Contains("refused the sign-in", StringComparison.OrdinalIgnoreCase) ? "Sign-in failed"
        : "Could not load assets";
}
