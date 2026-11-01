using FleetMate.Core.Models.Projects;
using FleetMate.GUI.Views.Projects;
using Xunit;

namespace FleetMate.Tests;

/// <summary>Sort Queries By orders the queries themselves, matching the macOS QueryListOrder.</summary>
public class QueryListOrderTests
{
    private static readonly QueryOrderKey[] Keys =
    {
        new("Bravo", 5, new DateTime(2026, 10, 3), 0),
        new("alpha", 40, new DateTime(2026, 10, 1), 1),
        new("Charlie", 5, new DateTime(2026, 10, 2), 2),
    };

    private static string[] Names(QueryListOrder order, bool ascending) =>
        order.Sort(Keys, ascending, k => k).Select(k => k.Name).ToArray();

    [Fact]
    public void DefaultIsNewestFirst()
    {
        Assert.Equal(QueryListOrder.LastChanged, QueryListOrders.Default);
        Assert.False(QueryListOrders.Default.DefaultAscending());
        Assert.Equal(new[] { "Bravo", "Charlie", "alpha" }, Names(QueryListOrder.LastChanged, false));
    }

    [Fact]
    public void NameIgnoresCase()
    {
        Assert.Equal(new[] { "alpha", "Bravo", "Charlie" }, Names(QueryListOrder.Name, true));
        Assert.Equal(new[] { "Charlie", "Bravo", "alpha" }, Names(QueryListOrder.Name, false));
    }

    [Fact]
    public void ResultCountTiesFallBackToName()
    {
        Assert.Equal(new[] { "alpha", "Bravo", "Charlie" }, Names(QueryListOrder.ResultCount, false));
        Assert.Equal(new[] { "Bravo", "Charlie", "alpha" }, Names(QueryListOrder.ResultCount, true));
    }

    [Fact]
    public void QueryOrderKeepsStoredPosition()
    {
        Assert.Equal(new[] { "Bravo", "alpha", "Charlie" }, Names(QueryListOrder.QueryOrder, true));
        Assert.Equal(new[] { "Charlie", "alpha", "Bravo" }, Names(QueryListOrder.QueryOrder, false));
    }

    [Fact]
    public void StoredValuesMatchTheMacApp()
    {
        Assert.Equal(new[] { "lastChanged", "name", "resultCount", "queryOrder" },
            QueryListOrders.All.Select(o => o.StorageValue()));
        foreach (var order in QueryListOrders.All)
            Assert.Equal(order, QueryListOrders.Parse(order.StorageValue()));
        Assert.Equal(QueryListOrders.Default, QueryListOrders.Parse(null));
    }

    private static QueriesListControl.QueryRunDisplay Run(string name, string area, int rows, int daysAgo) =>
        new(new AdoSharedQuery { Id = name, Name = name },
            Enumerable.Range(0, rows).Select(_ => new QueriesListControl.QueryRowDisplay(
                new UnifiedTask { UpdatedAt = new DateTime(2026, 10, 4).AddDays(-daysAgo) }, 0, false)).ToList(),
            false, area);

    [Fact]
    public void ByNameAreasAlphabeticalWithGeneralLast()
    {
        var sections = QueriesListControl.Ordered(new[]
        {
            Run("Zulu", "Systems", 1, 0),
            Run("Loose", "General", 1, 0),
            Run("Mike", "Devices", 1, 9),
            Run("Echo", "Devices", 1, 1),
        }, QueryListOrder.Name, true, r => r.Rows.Count);

        Assert.Equal(new[] { "Devices", "Systems", "General" }, sections.Select(s => s.Key));
        Assert.Equal(new[] { "Echo", "Mike" }, sections[0].Runs.Select(r => r.Query.Name));
    }

    [Fact]
    public void ByResultCountBiggestFirst()
    {
        var sections = QueriesListControl.Ordered(new[]
        {
            Run("Small", "Devices", 2, 0),
            Run("Big", "Devices", 30, 5),
            Run("Cloud", "Cloud", 3, 0),
        }, QueryListOrder.ResultCount, false, r => r.Rows.Count);

        Assert.Equal(new[] { "Devices", "Cloud" }, sections.Select(s => s.Key));
        Assert.Equal(new[] { "Big", "Small" }, sections[0].Runs.Select(r => r.Query.Name));
    }
}
