using System.Text.Json;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.GUI.Views.Projects;
using Xunit;

namespace FleetMate.Tests;

/// <summary>Projects Mine view and the card menu's helpers.</summary>
public class ProjectsMenuTests
{
    private static UnifiedTask Task(string provider, string id, DateTime updated, params string[] assignees) => new()
    {
        Provider = provider, Id = id, Title = id, UpdatedAt = updated, Assignees = assignees.ToList(),
    };

    [Fact]
    public void Mine_IsMeWorkItemsPlusGitHubAssignedToViewerNewestFirst()
    {
        var now = new DateTime(2026, 10, 5);
        var loaded = new[]
        {
            Task("azdevops", "1", now.AddDays(-3)),
            Task("azdevops", "2", now.AddDays(-1)),
            Task("github", "acme/fleet#7", now, "Ada"),
            Task("github", "acme/fleet#8", now, "bob"),
        };
        var extra = new[] { Task("azdevops", "9", now.AddDays(-2)) };

        var mine = BoardsPage.MineTasks(loaded, extra, new HashSet<int> { 1, 9 }, "ada");

        Assert.Equal(new[] { "acme/fleet#7", "9", "1" }, mine.Select(t => t.Id));
    }

    [Theory]
    [InlineData("2026-10-05", "tomorrow", "2026-10-06")] // Monday
    [InlineData("2026-10-05", "monday", "2026-10-12")]   // on a Monday, the next one
    [InlineData("2026-10-07", "monday", "2026-10-12")]   // Wednesday
    [InlineData("2026-10-11", "monday", "2026-10-12")]   // Sunday
    [InlineData("2026-02-10", "month", "2026-02-28")]
    public void RescheduleDates(string today, string choice, string expected) =>
        Assert.Equal(DateTime.Parse(expected), BoardsPage.RescheduleDate(choice, DateTime.Parse(today)));

    [Theory]
    [InlineData(5324, "Give each tab a Widgets section!", "5324-give-each-tab-a-widgets-section")]
    [InlineData(1, "***", "1")]
    public void BranchNames(int id, string title, string expected) =>
        Assert.Equal(expected, AzureDevOpsService.BranchNameFor(id, title));

    [Fact]
    public void BranchNameSlugIsAtMostFifty() =>
        Assert.True(AzureDevOpsService.BranchNameFor(7, new string('a', 80)).Length <= "7-".Length + 50);

    [Fact]
    public void ClassificationPathsFlattenDepthFirst()
    {
        using var doc = JsonDocument.Parse("""
            { "name": "Projects", "children": [
              { "name": "Devices", "children": [ { "name": "Windows" } ] },
              { "name": "Systems" } ] }
            """);
        Assert.Equal(new[] { @"Projects", @"Projects\Devices", @"Projects\Devices\Windows", @"Projects\Systems" },
            AzureDevOpsService.FlattenClassification(doc.RootElement));
    }

    [Fact]
    public void MembersDropGroupsAndServiceIdentities()
    {
        using var doc = JsonDocument.Parse("""
            { "value": [
              { "identity": { "displayName": "Ada", "uniqueName": "ada@example.org" } },
              { "identity": { "displayName": "Team", "uniqueName": "[Proj]\\Team", "isContainer": true } },
              { "identity": { "displayName": "Build", "uniqueName": "Build\\abc" } },
              { "identity": { "displayName": "Ada again", "uniqueName": "ADA@example.org" } } ] }
            """);
        var members = AzureDevOpsService.ParseMembers(doc.RootElement);
        Assert.Equal("ada@example.org", Assert.Single(members).UniqueName);
    }

    [Fact]
    public void WiqlIdsParse()
    {
        using var doc = JsonDocument.Parse("""{ "workItems": [ { "id": 3 }, { "id": 5 } ] }""");
        Assert.Equal(new HashSet<int> { 3, 5 }, AzureDevOpsService.ParseWiqlIds(doc.RootElement));
    }
}
