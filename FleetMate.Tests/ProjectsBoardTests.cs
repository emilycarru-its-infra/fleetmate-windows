using FleetMate.Core.Development;
using FleetMate.Core.Models.Projects;
using Xunit;

namespace FleetMate.Tests;

/// <summary>The Projects board, GitHub Project board and strip rules both Windows apps share.</summary>
public class ProjectsBoardTests
{
    private static UnifiedTask Task(string id, string state, string provider = "azdevops", int? priority = null,
        string? assignee = null, string area = "", DateTime? updated = null) => new()
    {
        Id = id,
        Provider = provider,
        Title = $"Item {id}",
        Priority = priority,
        State = TaskBoard.IsClosedState(state) ? TaskState.Closed : TaskState.Open,
        Assignees = assignee == null ? new() : new() { assignee },
        UpdatedAt = updated ?? DateTime.UtcNow,
        Metadata = new() { ["state"] = state, ["areaPath"] = area, ["workItemType"] = "Task" },
    };

    [Fact]
    public void StateColumnsFollowTheWorkflowAndHideClosedUnlessAsked()
    {
        var tasks = new[] { Task("1", "Closed"), Task("2", "Active"), Task("3", "New"), Task("4", "Resolved") };
        Assert.Equal(new[] { "New", "Active" }, TaskBoard.Columns(tasks, "State", showClosed: false).Select(c => c.Title));
        Assert.Equal(new[] { "New", "Active", "Resolved", "Closed" }, TaskBoard.Columns(tasks, "State", showClosed: true).Select(c => c.Title));
    }

    [Fact]
    public void PriorityColumnsAreFixedAndAssignedPutsUnassignedFirst()
    {
        var tasks = new[] { Task("1", "New", priority: 2), Task("2", "New", assignee: "Zed"), Task("3", "New", assignee: "Amy") };
        Assert.Equal(new[] { "Critical", "High", "Medium", "Low", "None" }, TaskBoard.Columns(tasks, "Priority", false).Select(c => c.Title));
        Assert.Equal(new[] { "Unassigned", "Amy", "Zed" }, TaskBoard.Columns(tasks, "Assigned To", false).Select(c => c.Title));
    }

    [Fact]
    public void CardsSortNewestChangeFirst()
    {
        var now = DateTime.UtcNow;
        var column = TaskBoard.Columns(new[] { Task("1", "New", updated: now.AddDays(-2)), Task("2", "New", updated: now) }, "State", false).Single();
        Assert.Equal(new[] { "2", "1" }, column.Tasks.Select(t => t.Id));
    }

    [Theory]
    [InlineData("State", "Active", true)]
    [InlineData("Priority", "High", true)]
    [InlineData("Priority", "None", false)]
    [InlineData("Assigned To", TaskBoard.Unassigned, false)]
    [InlineData("Area Path", TaskBoard.NoArea, false)]
    [InlineData("Type", "Bug", false)]
    public void DropsChangeOnlyFieldsThatNameAValue(string groupBy, string column, bool changes) =>
        Assert.Equal(changes, TaskBoard.DropRequest(groupBy, column) != null);

    [Fact]
    public void ADropOnItsOwnColumnOrFromGitHubIsRefused()
    {
        Assert.False(TaskBoard.CanDrop(Task("1", "Active"), "State", "Active"));
        Assert.True(TaskBoard.CanDrop(Task("1", "Active"), "State", "Resolved"));
        Assert.False(TaskBoard.CanDrop(Task("7", "Open", provider: "github"), "State", "Active"));
        Assert.Equal("Set State to Active", TaskBoard.DropCaption("State", "Active"));
        Assert.Equal("Assign to Amy", TaskBoard.DropCaption("Assigned To", "Amy"));
    }

    [Fact]
    public void FilterDropsClosedOffStateColumnsAndInFlatLists()
    {
        var tasks = new[] { Task("1", "Closed"), Task("2", "Active") };
        Assert.Equal(2, TaskBoard.Filter(tasks, "", null, false, "State", flat: false).Count);
        Assert.Single(TaskBoard.Filter(tasks, "", null, false, "Priority", flat: false));
        Assert.Single(TaskBoard.Filter(tasks, "", null, false, "State", flat: true));
        Assert.Single(TaskBoard.Filter(tasks, "item 2", null, true, "State", flat: true));
    }

    [Fact]
    public void ApplyUpdatePatchesTheCardFromAzureDevOps()
    {
        var task = Task("1", "New");
        TaskBoard.ApplyUpdate(task, new WorkItem
        {
            Id = 1,
            Fields = new WorkItemFields { State = "Active", Priority = 1, AreaPath = @"P\A", AssignedTo = new IdentityRef { DisplayName = "Amy" }, WorkItemType = "Bug" },
        });
        Assert.Equal("Active", task.Metadata["state"]);
        Assert.Equal(TaskState.InProgress, task.State);
        Assert.Equal(1, task.Priority);
        Assert.Equal(new[] { "Amy" }, task.Assignees);
    }

    [Fact]
    public void RowDetailReadsLikeTheMac()
    {
        var task = Task("12", "Active", area: @"Campus\Devices");
        Assert.Equal("#12 · Task · Active · Campus › Devices", TaskCardText.RowDetail(task));
        Assert.Equal("12", TaskCardText.CopyId(task));
        Assert.Equal("#5", TaskCardText.CopyId(Task("5", "Open", provider: "github")));
    }

    [Fact]
    public void GitHubColumnsFollowTheStatusOptionsThenNoStatus()
    {
        var items = ProjectsDemoData.ProjectItems(DateTime.UtcNow);
        items.Add(new GitHubProjectItem { Id = "x", Type = "ISSUE", Content = new GitHubProjectItemContent { Title = "Loose" } });
        var columns = GitHubProjectBoard.Columns(items, ProjectsDemoData.StatusField(), null);
        Assert.Equal(new[] { "Todo", "In Progress", "In Review", "Done", GitHubProjectBoard.NoStatus }, columns.Select(c => c.Name));
        Assert.Equal(2, columns[0].Items.Count);
        Assert.Single(GitHubProjectBoard.Columns(items, ProjectsDemoData.StatusField(), "arm64").SelectMany(c => c.Items));
        Assert.Equal("Write the release notes", GitHubProjectBoard.Title(items.Single(i => i.Type == "DRAFT_ISSUE")));
    }

    [Fact]
    public void StripCardsCountTheUsersItemsAndMarkThePickedFigure()
    {
        var now = DateTime.UtcNow;
        var mine = ProjectsDemoData.MyWorkItems(now);
        var cards = ProjectsGraphCards.For(new ProjectsFigures(mine, false, new List<WorkItem>(), ProjectsDemoData.Sprints(now),
            ProjectsDemoData.Issues(now), false, ProjectsTile.InProgress, "https://dev.example.com/demo"), now);
        var figures = cards.Where(c => c.Kind == GraphCardKind.Kpis).SelectMany(c => c.Kpis).ToList();
        Assert.Equal(8, figures.Count);
        Assert.Equal(mine.Count.ToString(), figures[0].Value);
        Assert.Contains(figures, k => k.Title == "In Progress (on)");
        Assert.Equal("3", figures.Single(k => k.Title == "GitHub Issues").Value);
        Assert.StartsWith("Sprint: Sprint 14", cards.Single(c => c.Title == "By State").Caption);
        Assert.All(cards.Single(c => c.Title == "Work Items List").Rows, r => Assert.StartsWith("https://dev.example.com/demo/_workitems/edit/", r.Open!.Value));
    }

    [Fact]
    public void AFigureClickTogglesItsTileAndAListRowOpensItsAddress()
    {
        var tiles = new ProjectsTileFilter();
        Assert.Null(ProjectsGraphCards.Apply(new GraphFilter(ProjectsGraphCards.Tile, nameof(ProjectsTile.Blocked)), tiles));
        Assert.Equal(ProjectsTile.Blocked, tiles.Selected);
        ProjectsGraphCards.Apply(new GraphFilter(ProjectsGraphCards.Tile, nameof(ProjectsTile.Blocked)), tiles);
        Assert.Null(tiles.Selected);
        Assert.Equal("https://example.com/x", ProjectsGraphCards.Apply(new GraphFilter(ProjectsGraphCards.Url, "https://example.com/x"), tiles));
    }

    [Fact]
    public void DemoQueriesAreBucketedByArea()
    {
        var runs = ProjectsDemoData.QueryRuns(DateTime.UtcNow);
        Assert.Equal("Devices", runs[0].AreaBucket);
        Assert.Equal("Identity", runs[1].AreaBucket);
        Assert.True(runs[1].Rows[0].HasChildren);
    }
}
