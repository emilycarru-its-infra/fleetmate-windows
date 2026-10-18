using FleetMate.Core.Shared;
using Xunit;

namespace FleetMate.Tests;

/// <summary>The strip's row packing, the same cases as the macOS app's tests.</summary>
public class WidgetRowPackingTests
{
    private static int[] Sizes(List<List<int>> rows, int[] spans) => rows.Select(r => r.Sum(i => spans[i])).ToArray();

    [Fact]
    public void OneRowWhenEverythingFits() =>
        Assert.Equal(new[] { new[] { 0, 1, 2 } }, WidgetRowPacking.Rows(new[] { 1, 1, 2 }, 6, 1000).Select(r => r.ToArray()));

    [Fact]
    public void RowsAreBalancedNotGreedy()
    {
        var spans = new[] { 1, 1, 1, 1, 1, 1 };
        Assert.Equal(new[] { 3, 3 }, Sizes(WidgetRowPacking.Rows(spans, 5, 1000), spans));
    }

    [Fact]
    public void WideCardsBalance()
    {
        var spans = new[] { 2, 2, 2, 1, 1, 2, 2 };
        var rows = WidgetRowPacking.Rows(spans, 6, 1000);
        Assert.Equal(new[] { 6, 6 }, Sizes(rows, spans));
        Assert.Equal(Enumerable.Range(0, 7), rows.SelectMany(r => r));
    }

    [Fact]
    public void FullRowCardsBreakTheRun() =>
        Assert.Equal(new[] { new[] { 0 }, new[] { 1, 2 } }, WidgetRowPacking.Rows(new[] { 1000, 1, 1 }, 4, 1000).Select(r => r.ToArray()));

    [Fact]
    public void SpansWiderThanTheRowAreClamped() =>
        Assert.Equal(3, WidgetRowPacking.Rows(new[] { 3, 3, 1 }, 2, 1000).Count);
}
