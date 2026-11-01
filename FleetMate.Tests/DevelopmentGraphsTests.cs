using System.Globalization;
using FleetMate.Core.Models.Projects;
using FleetMate.GUI.Views.Shared.Widgets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>The Development graphs added to fill each sub-tab's rows, matching the macOS app.</summary>
public class DevelopmentGraphsTests
{
    private static readonly DateTime Today = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Local);

    [Fact]
    public void PerWeekIsContinuousAndOldestFirst()
    {
        var weeks = WidgetCatalog.PerWeek(new DateTime?[] { Today, Today.AddDays(-14), null }, 4, Today);
        Assert.Equal(4, weeks.Count);
        Assert.Equal(new[] { 0, 1, 0, 1 }, weeks.Select(w => w.Count));
        Assert.True(weeks[0].Start < weeks[3].Start);
    }

    [Fact]
    public void WeekdaysFollowTheUsersFirstDay()
    {
        var days = WidgetCatalog.Weekdays(new DateTime?[] { Today });
        Assert.Equal(7, days.Count);
        var format = CultureInfo.CurrentCulture.DateTimeFormat;
        Assert.Equal(format.GetAbbreviatedDayName(format.FirstDayOfWeek), days[0].Label);
        Assert.Equal(1, days.Sum(d => d.Value));
    }

    [Fact]
    public void FailuresCountFailedAndPartialRunsByPipeline()
    {
        PipelineRun Run(string name, PipelineRunStatus s) => new() { PipelineName = name, Status = s };
        var slices = WidgetCatalog.FailuresByPipeline(new[]
        {
            Run("A", PipelineRunStatus.Failed), Run("A", PipelineRunStatus.Partial),
            Run("B", PipelineRunStatus.Failed), Run("C", PipelineRunStatus.Succeeded),
        });
        Assert.Equal(new[] { "A", "B" }, slices.Select(s => s.Label));
        Assert.Equal(new[] { 2, 1 }, slices.Select(s => s.Value));
    }

    [Fact]
    public void MostDiscussedSkipsUncommentedPullRequests()
    {
        UnifiedPullRequest Pr(int n, int comments) => new() { Number = n, Title = $"PR {n}", CommentCount = comments };
        var top = WidgetCatalog.MostDiscussed(new[] { Pr(1, 0), Pr(2, 5), Pr(3, 9) });
        Assert.Equal(new[] { 3, 2 }, top.Select(p => p.Number));
    }

    [Fact]
    public void KpiTilesTakeUpToTheirMostColumns()
    {
        Assert.Equal(4, KpiTilePanel.ColumnsFor(4 * KpiTilePanel.MinTileWidth, 4));
        Assert.Equal(3, KpiTilePanel.ColumnsFor(3 * KpiTilePanel.MinTileWidth, 4));
        Assert.Equal(2, KpiTilePanel.ColumnsFor(100, 4));
        Assert.Equal(3, KpiTilePanel.ColumnsFor(10 * KpiTilePanel.MinTileWidth));
    }

    [Fact]
    public void DevelopmentGraphsStartHiddenAndOthersShown()
    {
        Assert.False(WidgetVisibility.ShownByDefault("Development"));
        Assert.True(WidgetVisibility.ShownByDefault("Projects"));
        Assert.EndsWith(".v2", WidgetVisibility.PersistenceKey("Development"));
        Assert.EndsWith(".v2", WidgetVisibility.PersistenceKey("Projects"));
        Assert.Equal("widgets.collapsed.Devices", WidgetVisibility.PersistenceKey("Devices"));
    }
}
