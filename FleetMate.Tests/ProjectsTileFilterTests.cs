using System.Text.Json;
using FleetMate.Core.Models.Projects;
using Xunit;

namespace FleetMate.Tests;

public class ProjectsTileFilterTests
{
    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    private static WorkItem Item(int id, string fieldsJson) =>
        JsonSerializer.Deserialize<WorkItem>($"{{\"id\":{id},\"fields\":{fieldsJson}}}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private static readonly WorkItem[] Mine =
    {
        Item(1, """{"System.State":"New","System.CreatedDate":"2026-10-16T09:00:00Z","System.ChangedDate":"2026-10-17T09:00:00Z"}"""),
        Item(2, """{"System.State":"Active","System.CreatedDate":"2026-01-01T00:00:00Z","System.ChangedDate":"2026-08-01T00:00:00Z","System.Tags":"Blocked","Microsoft.VSTS.Scheduling.DueDate":"2026-10-01T00:00:00Z"}"""),
        Item(3, """{"System.State":"In Progress","System.ChangedDate":"2026-10-18T08:00:00Z"}"""),
    };

    private static UnifiedTask Task(string provider, string id) => new() { Provider = provider, Id = id, Title = id };

    [Fact]
    public void ClickingATileSelectsIt()
    {
        var filter = new ProjectsTileFilter();
        var raised = 0;
        filter.Changed += (_, _) => raised++;

        filter.Toggle(ProjectsTile.Blocked);

        Assert.Equal(ProjectsTile.Blocked, filter.Selected);
        Assert.True(filter.IsActive);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void ClickingTheSameTileAgainClears()
    {
        var filter = new ProjectsTileFilter();
        filter.Toggle(ProjectsTile.Overdue);
        filter.Toggle(ProjectsTile.Overdue);
        Assert.Null(filter.Selected);
        Assert.False(filter.IsActive);
    }

    [Fact]
    public void ClickingAnotherTileSwitches()
    {
        var filter = new ProjectsTileFilter();
        filter.Toggle(ProjectsTile.Overdue);
        filter.Toggle(ProjectsTile.Stale);
        Assert.Equal(ProjectsTile.Stale, filter.Selected);
    }

    [Fact]
    public void ClearRemovesTheFilter()
    {
        var filter = new ProjectsTileFilter();
        var raised = 0;
        filter.Toggle(ProjectsTile.InProgress);
        filter.Changed += (_, _) => raised++;

        filter.Clear();
        filter.Clear();

        Assert.Null(filter.Selected);
        Assert.Equal(1, raised);
    }

    [Theory]
    [InlineData(ProjectsTile.AssignedToMe, new[] { 1, 2, 3 })]
    [InlineData(ProjectsTile.InProgress, new[] { 2, 3 })]
    [InlineData(ProjectsTile.NewThisWeek, new[] { 1 })]
    [InlineData(ProjectsTile.UpdatedThisWeek, new[] { 1, 3 })]
    [InlineData(ProjectsTile.Blocked, new[] { 2 })]
    [InlineData(ProjectsTile.Overdue, new[] { 2 })]
    [InlineData(ProjectsTile.Stale, new[] { 2 })]
    [InlineData(ProjectsTile.GitHubIssues, new int[0])]
    public void EachTileSelectsTheItemsItCounts(ProjectsTile tile, int[] expected)
    {
        Assert.Equal(expected, ProjectsTileFilter.Ids(Mine, tile, Now).Order());
    }

    [Fact]
    public void TileSetsMatchTheFigures()
    {
        var stats = WorkItemStats.From(Mine, Now);
        Assert.Equal(stats.Open, ProjectsTileFilter.Ids(Mine, ProjectsTile.AssignedToMe, Now).Count);
        Assert.Equal(stats.InProgress, ProjectsTileFilter.Ids(Mine, ProjectsTile.InProgress, Now).Count);
        Assert.Equal(stats.Blocked, ProjectsTileFilter.Ids(Mine, ProjectsTile.Blocked, Now).Count);
        Assert.Equal(stats.Stale, ProjectsTileFilter.Ids(Mine, ProjectsTile.Stale, Now).Count);
    }

    [Fact]
    public void RowsPassOnlyWhenInTheTilesSet()
    {
        var ids = ProjectsTileFilter.Ids(Mine, ProjectsTile.Blocked, Now);

        Assert.True(ProjectsTileFilter.Passes(Task("azdevops", "2"), ProjectsTile.Blocked, ids));
        Assert.False(ProjectsTileFilter.Passes(Task("azdevops", "3"), ProjectsTile.Blocked, ids));
        Assert.False(ProjectsTileFilter.Passes(Task("github", "2"), ProjectsTile.Blocked, ids));
    }

    [Fact]
    public void GitHubIssuesTileKeepsGitHubRows()
    {
        var none = new HashSet<int>();
        Assert.True(ProjectsTileFilter.Passes(Task("github", "42"), ProjectsTile.GitHubIssues, none));
        Assert.False(ProjectsTileFilter.Passes(Task("azdevops", "1"), ProjectsTile.GitHubIssues, none));
    }

    [Fact]
    public void WithNoTileEveryRowPasses()
    {
        Assert.True(ProjectsTileFilter.Passes(Task("azdevops", "999"), null, new HashSet<int>()));
        Assert.True(ProjectsTileFilter.Passes(Task("gitea", "x"), null, new HashSet<int>()));
    }

    [Fact]
    public void TheGitHubIssuesTile_ListsTheStripsIssues_NewestFirstAndSearched()
    {
        var issues = new[]
        {
            new FleetMate.Core.Services.Projects.GitHubIssueSummary { Number = 4, Title = "Fix login", Repository = "o/app", WebUrl = "https://example.invalid/o/app/issues/4", UpdatedAt = Now.AddDays(-2) },
            new FleetMate.Core.Services.Projects.GitHubIssueSummary { Number = 9, Title = "Add export", Repository = "o/tool", UpdatedAt = Now },
        };
        var rows = ProjectsTileFilter.IssueRows(issues, null);
        Assert.Equal(new[] { "o/tool#9", "o/app#4" }, rows.Select(r => r.Id));
        Assert.All(rows, r => Assert.True(ProjectsTileFilter.Passes(r, ProjectsTile.GitHubIssues, new HashSet<int>())));
        Assert.Equal("o/app", rows[1].Metadata["repository"]);
        Assert.Equal("https://example.invalid/o/app/issues/4", rows[1].ExternalUrl);
        Assert.Equal(new[] { "o/app#4" }, ProjectsTileFilter.IssueRows(issues, " LOGIN ").Select(r => r.Id));
    }

    [Fact]
    public void TheMyItemsMenu_SelectsATileOrNone()
    {
        var filter = new ProjectsTileFilter();
        var changes = 0;
        filter.Changed += (_, _) => changes++;
        filter.Select(ProjectsTile.Blocked);
        filter.Select(ProjectsTile.Blocked);
        Assert.Equal(ProjectsTile.Blocked, filter.Selected);
        filter.Select(null);
        Assert.False(filter.IsActive);
        Assert.Equal(2, changes);
    }
}
