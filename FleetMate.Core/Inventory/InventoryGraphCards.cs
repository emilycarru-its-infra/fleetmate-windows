using FleetMate.Core.Development;
using FleetMate.Core.Models.Inventory;

namespace FleetMate.Core.Inventory;

/// <summary>The filter categories an Inventory graph click hands the tab, as the macOS filters name them.</summary>
public static class InventoryGraphCategory
{
    /// <summary>Filter the list to an asset category.</summary>
    public const string Category = "Category";

    /// <summary>Filter the list to a status type (Deployable, Deployed…), which several status names share.</summary>
    public const string StatusType = "Status Type";
}

/// <summary>
/// The Inventory graphs, matching the macOS app: Assets, Deployed and
/// Unassigned, Assets by Category and Asset Status. Each Windows app draws
/// these cards; every figure comes from <see cref="InventoryFigures"/>.
/// </summary>
public static class InventoryGraphCards
{
    /// <summary>The cache whose change redraws the cards.</summary>
    public const string CacheKey = "Assets";

    private static readonly ChartTone[] CategoryTones =
    {
        ChartTone.Orange, ChartTone.Blue, ChartTone.Purple, ChartTone.Teal,
        ChartTone.Green, ChartTone.Pink, ChartTone.Brown, ChartTone.Indigo,
    };

    private static readonly ChartTone[] StatusTones =
        { ChartTone.Green, ChartTone.Blue, ChartTone.Orange, ChartTone.Purple, ChartTone.Gray };

    /// <param name="assets">The loaded assets.</param>
    /// <param name="loading">The assets are loading.</param>
    /// <param name="loadError">Why the last load failed, or null.</param>
    public static List<GraphCard> Build(IReadOnlyCollection<SnipeAsset> assets, bool loading, string? loadError)
    {
        var waiting = loading && assets.Count == 0;
        var empty = InventoryFigures.AssetsFailureHeadline(loadError);

        var cards = new List<GraphCard>
        {
            new(GraphCardKind.Kpis, "")
            {
                Kpis = new[]
                {
                    new GraphKpi("Assets", assets.Count.ToString("N0"), ChartTone.Orange, waiting, null),
                    new GraphKpi("Deployed", InventoryFigures.DeployedCount(assets).ToString("N0"), ChartTone.Green, waiting, null),
                    new GraphKpi("Unassigned", InventoryFigures.UnassignedCount(assets).ToString("N0"), ChartTone.Blue, waiting, null),
                },
            },
        };

        var categories = InventoryFigures.AssetCategorySlices(assets);
        cards.Add(Chart(GraphCardKind.Bars, "Assets by Category", categories.Count > 0, loading, empty,
            categories.Select((s, i) => new GraphBar(s.Label, s.Value, CategoryTones[i % CategoryTones.Length],
                GraphFilter.For(InventoryGraphCategory.Category, s.Label),
                DevelopmentGraphCards.Describe(s.Label, s.Value, "asset"))).ToList()));

        var statuses = InventoryFigures.AssetStatusSlices(assets);
        cards.Add(Chart(GraphCardKind.Donut, "Asset Status", statuses.Count > 0, loading, empty,
            statuses.Select((s, i) => new GraphBar(s.Label, s.Value, StatusTones[i % StatusTones.Length],
                GraphFilter.For(InventoryGraphCategory.StatusType, s.Label),
                DevelopmentGraphCards.Describe(Shared.ChartSlice.FilterValue(s.Label), s.Value, "asset"))).ToList()));

        return cards;
    }

    private static GraphCard Chart(GraphCardKind kind, string title, bool hasData, bool loading, string empty, IReadOnlyList<GraphBar> bars) =>
        new(kind, title)
        {
            Loading = loading,
            Body = hasData ? GraphCardBody.Chart : loading ? GraphCardBody.Loading : GraphCardBody.Empty,
            Message = hasData ? "" : empty,
            Bars = bars,
        };
}
