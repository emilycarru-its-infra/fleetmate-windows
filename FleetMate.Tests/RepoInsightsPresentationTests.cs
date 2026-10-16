using FleetMate.Core.Services.Repos;
using Xunit;

namespace FleetMate.Tests;

public class RepoInsightsPresentationTests
{
    private static RepoStatsReport.TimelinePoint P(int added, int removed = 0) => new(DateTimeOffset.UnixEpoch, 1, added, removed);

    [Fact]
    public void AutomaticScaleFindsOutliersAndIgnoresEmptyBuckets()
    {
        Assert.Equal(RepoChurnScale.Compressed, RepoChurnScaling.Resolve(RepoChurnScale.Automatic, new[] { P(100), P(100), P(10_000), P(0) }));
        Assert.Equal(RepoChurnScale.Linear, RepoChurnScaling.Resolve(RepoChurnScale.Automatic, new[] { P(1), P(1), P(9_999) }));
        Assert.Equal(RepoChurnScale.Linear, RepoChurnScaling.Resolve(RepoChurnScale.Automatic, new[] { P(100), P(102), P(10_000) }));
        Assert.Equal(RepoChurnScale.Linear, RepoChurnScaling.Resolve(RepoChurnScale.Automatic, Array.Empty<RepoStatsReport.TimelinePoint>()));
        Assert.Equal(RepoChurnScale.Linear, RepoChurnScaling.Resolve(RepoChurnScale.Linear, new[] { P(1), P(1), P(100_000) }));
    }

    [Fact]
    public void MedianAveragesBothMiddleValuesAndIncludesRemovals()
    {
        var points = new[] { P(1, 100), P(200, 10_000) };
        Assert.Equal(RepoChurnScale.Linear, RepoChurnScaling.Resolve(RepoChurnScale.Automatic, points));
        Assert.Equal(RepoChurnScale.Compressed, RepoChurnScaling.Resolve(RepoChurnScale.Automatic, new[] { P(1, 100), P(100, 10_000) }));
    }

    [Theory]
    [InlineData(-1_000_000)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1_000_000)]
    public void CompressionPreservesSignAndRoundTripsRawValues(double value)
    {
        var transformed = RepoChurnScaling.Transform(value, RepoChurnScale.Compressed);
        Assert.Equal(Math.Sign(value), Math.Sign(transformed));
        Assert.Equal(value, RepoChurnScaling.Inverse(transformed, RepoChurnScale.Compressed), 6);
        Assert.Equal(value, RepoChurnScaling.Transform(value, RepoChurnScale.Linear));
    }

    [Fact]
    public void CompressedTicksAreActualLineCountsAndStayLegible()
    {
        Assert.Equal(new[] { 0, 100, 1000, 10000 }, RepoChurnScaling.Ticks(12345, RepoChurnScale.Compressed));
        Assert.Equal(new[] { 0, 100, 10000, 1000000 }, RepoChurnScaling.Ticks(2_000_000, RepoChurnScale.Compressed));
        Assert.Equal(new[] { 0, 1 }, RepoChurnScaling.Ticks(1, RepoChurnScale.Compressed));
    }

    [Fact]
    public void HeatmapIncludesAll168CellsAndExactCounts()
    {
        var grid = RepoActivityGrid.Expand(new[] { new RepoStatsReport.ActivityCell(1, 0, 7), new RepoStatsReport.ActivityCell(7, 23, 99) });
        Assert.Equal(168, grid.Count);
        Assert.Equal(168, grid.Select(c => (c.Weekday, c.Hour)).Distinct().Count());
        Assert.Equal(7, grid.Single(c => c.Weekday == 1 && c.Hour == 0).Commits);
        Assert.Equal(99, grid.Single(c => c.Weekday == 7 && c.Hour == 23).Commits);
        Assert.Equal(166, grid.Count(c => c.Commits == 0));
    }
}
