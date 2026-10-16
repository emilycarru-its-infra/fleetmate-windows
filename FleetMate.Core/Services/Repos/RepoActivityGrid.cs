namespace FleetMate.Core.Services.Repos;

/// <summary>The complete weekday/hour domain, including periods without commits.</summary>
public static class RepoActivityGrid
{
    public static IReadOnlyList<RepoStatsReport.ActivityCell> Expand(IReadOnlyList<RepoStatsReport.ActivityCell> cells)
    {
        var counts = cells.GroupBy(c => (c.Weekday, c.Hour)).ToDictionary(g => g.Key, g => g.Sum(c => c.Commits));
        return Enumerable.Range(1, 7).SelectMany(day => Enumerable.Range(0, 24)
            .Select(hour => new RepoStatsReport.ActivityCell(day, hour, counts.GetValueOrDefault((day, hour))))).ToArray();
    }
}
