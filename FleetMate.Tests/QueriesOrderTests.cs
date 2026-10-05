using FleetMate.Core.Models.Projects;
using FleetMate.GUI.Views.Projects;
using Xunit;

namespace FleetMate.Tests;

/// <summary>Projects › List orders shared queries, and their areas, most recently active first.</summary>
public class QueriesOrderTests
{
    private static QueriesListControl.QueryRunDisplay Run(string name, string area, params int[] daysAgo) =>
        new(new AdoSharedQuery { Id = name, Name = name },
            daysAgo.Select(d => new QueriesListControl.QueryRowDisplay(
                new UnifiedTask { UpdatedAt = new DateTime(2026, 10, 4).AddDays(-d) }, 0, false)).ToList(),
            false, area);

    [Fact]
    public void QueriesAndAreasMostRecentFirst()
    {
        var sections = QueriesListControl.RecentFirst(new[]
        {
            Run("Old devices", "Devices", 30),
            Run("Fresh devices", "Devices", 9, 1),
            Run("Systems today", "Systems", 0),
            Run("Empty", "General"),
        });

        Assert.Equal(new[] { "Systems", "Devices", "General" }, sections.Select(s => s.Key));
        Assert.Equal(new[] { "Fresh devices", "Old devices" }, sections[1].Runs.Select(r => r.Query.Name));
    }
}
