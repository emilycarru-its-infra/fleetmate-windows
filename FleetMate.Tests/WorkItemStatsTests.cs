using System.Text.Json;
using FleetMate.Core.Models.Projects;
using Xunit;

namespace FleetMate.Tests;

/// <summary>The Projects strip's tiles, the same cases as the macOS app's tests.</summary>
public class WorkItemStatsTests
{
    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    private static WorkItem Item(int id, string fieldsJson) =>
        JsonSerializer.Deserialize<WorkItem>($"{{\"id\":{id},\"fields\":{fieldsJson}}}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public void TilesCountFromLoadedItems()
    {
        var items = new[]
        {
            Item(1, """{"System.State":"New","System.CreatedDate":"2026-10-16T09:00:00.123Z","System.ChangedDate":"2026-10-17T09:00:00Z"}"""),
            Item(2, """{"System.State":"Active","System.CreatedDate":"2026-01-01T00:00:00Z","System.ChangedDate":"2026-08-01T00:00:00Z","System.Tags":"Infra; Blocked","Microsoft.VSTS.Scheduling.DueDate":"2026-10-01T00:00:00Z"}"""),
            Item(3, """{"System.State":"In Progress","System.BoardColumn":"Blocked","System.ChangedDate":"2026-10-18T08:00:00Z"}"""),
        };
        var stats = WorkItemStats.From(items, Now);
        Assert.Equal(3, stats.Open);
        Assert.Equal(2, stats.InProgress);
        Assert.Equal(1, stats.NewThisWeek);
        Assert.Equal(2, stats.UpdatedThisWeek);
        Assert.Equal(2, stats.Blocked);
        Assert.Equal(1, stats.Overdue);
        Assert.Equal(1, stats.Stale);
    }

    [Fact]
    public void RankedByType()
    {
        var items = new[]
        {
            Item(1, """{"System.WorkItemType":"Task"}"""),
            Item(2, """{"System.WorkItemType":"Task"}"""),
            Item(3, """{"System.WorkItemType":"Bug"}"""),
        };
        Assert.Equal(new[] { "Task", "Bug" }, WorkItemStats.Ranked(items, w => w.WorkItemType).Select(r => r.Label));
    }
}
