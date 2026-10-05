using System.Text.Json;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.GUI.Views.Projects;
using Xunit;

namespace FleetMate.Tests;

/// <summary>The work item sidebar: detail parsing, code artifacts, Save All, the discussion.</summary>
public class WorkItemSidebarTests
{
    private const string Org = "https://dev.example.org/acme";

    private static WorkItemDetail Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return AzureDevOpsService.ParseWorkItemDetail(doc.RootElement.Clone(), Org);
    }

    [Fact]
    public void DetailReadsFieldsAndRelations()
    {
        var d = Parse("""
            { "id": 42,
              "fields": {
                "System.Title": "Fix it", "System.State": "Active", "System.Reason": "Approved",
                "System.WorkItemType": "Bug", "System.TeamProject": "Projects",
                "System.AssignedTo": { "displayName": "Ada", "uniqueName": "ada@example.org" },
                "Microsoft.VSTS.Common.Priority": 2, "Microsoft.VSTS.Scheduling.RemainingWork": 1.5,
                "Microsoft.VSTS.Scheduling.DueDate": "2026-10-09T00:00:00Z", "System.Tags": "a; b",
                "System.CreatedBy": { "displayName": "Bob" } },
              "relations": [
                { "rel": "System.LinkTypes.Hierarchy-Reverse", "url": "https://x/_apis/wit/workItems/7", "attributes": { "name": "Parent" } },
                { "rel": "System.LinkTypes.Related", "url": "https://x/_apis/wit/workItems/9" },
                { "rel": "ArtifactLink", "url": "vstfs:///Git/Commit/p%2Fr%2Fabcdef1234", "attributes": { "name": "Fixed in Commit" } } ] }
            """);

        Assert.Equal(("Fix it", "Active", "Bug", "Projects"), (d.Title, d.State, d.Type, d.Project));
        Assert.Equal("ada@example.org", d.AssignedToUniqueName);
        Assert.Equal(2, d.Priority);
        Assert.Equal(1.5, d.RemainingWork);
        Assert.True(d.HasEffort);
        Assert.Equal(new[] { "a", "b" }, d.TagList);
        Assert.Equal("Bob", d.CreatedBy);
        Assert.Equal(7, d.ParentId);
        Assert.Equal(9, d.Relations.Single(r => r.Kind == WorkItemRelationKind.Related).LinkedWorkItemId);
        Assert.Null(d.Relations.Single(r => r.Kind == WorkItemRelationKind.Artifact).LinkedWorkItemId);
        Assert.Equal($"{Org}/Projects/_workitems/edit/42", d.WebUrl);
    }

    [Theory]
    [InlineData("vstfs:///Git/Commit/p%2Fr%2Fabcdef1234", "Commit", "abcdef1")]
    [InlineData("vstfs:///Git/PullRequestId/p%2Fr%2F12", "Pull Request", "PR #12")]
    [InlineData("vstfs:///Git/Ref/p%2Fr%2FGBfeature%252Fsidebar", "Branch", "feature/sidebar")]
    [InlineData("vstfs:///Git/Ref/p%2fr%2fGBmain", "Branch", "main")]
    public void ArtifactsReadWithoutANetworkCall(string url, string kind, string label)
    {
        Assert.Equal(kind, AzureDevOpsService.ArtifactKind(url));
        Assert.Equal(label, AzureDevOpsService.ArtifactLabel(url));
    }

    [Fact]
    public void ArtifactUrisRoundTrip()
    {
        var branch = AzureDevOpsService.BranchArtifactUri("p", "r", "feature/sidebar");
        Assert.Equal("feature/sidebar", AzureDevOpsService.ArtifactLabel(branch));
        Assert.Equal($"{Org}/p/_git/r/commit/abc", AzureDevOpsService.ArtifactWebUrl(AzureDevOpsService.CommitArtifactUri("p", "r", "abc"), Org));
        Assert.Equal($"{Org}/p/_git/r/pullrequest/5", AzureDevOpsService.ArtifactWebUrl(AzureDevOpsService.PullRequestArtifactUri("p", "r", 5), Org));
    }

    private static readonly WorkItemDetail Item = new()
    {
        Id = 1, Title = "T", State = "Active", Type = "Task", AssignedToUniqueName = "ada@example.org",
        Priority = 2, AreaPath = "P", IterationPath = "P\\S1", Tags = "a; b", RemainingWork = 3,
        DueDate = new DateTime(2026, 10, 9),
    };

    [Fact]
    public void SaveAllSendsOnlyChangedFields()
    {
        var unchanged = new WorkItemEdit
        {
            Title = "T ", State = "Active", Type = "Task", AssignedToUniqueName = "ADA@example.org", Priority = 2,
            AreaPath = "P", IterationPath = "P\\S1", Tags = "b;a", RemainingWork = 3, DueDate = new DateTime(2026, 10, 9),
        };
        Assert.Empty(AzureDevOpsService.EditOperations(Item, unchanged));

        unchanged.State = "Closed";
        unchanged.RemainingWork = null;
        unchanged.DueDate = null;
        var ops = AzureDevOpsService.EditOperations(Item, unchanged);
        Assert.Equal(new[] { "/fields/System.State", "/fields/Microsoft.VSTS.Scheduling.RemainingWork", "/fields/Microsoft.VSTS.Scheduling.DueDate" },
            ops.Select(o => o.Path));
        Assert.Equal(new[] { "add", "remove", "remove" }, ops.Select(o => o.Op));
    }

    [Fact]
    public void DiscussionIsNewestFirstAndSkipsDeleted()
    {
        using var doc = JsonDocument.Parse("""
            { "comments": [
              { "id": 1, "text": "old", "createdDate": "2026-10-01T00:00:00Z", "createdBy": { "displayName": "Ada", "uniqueName": "ada@example.org" } },
              { "id": 2, "text": "gone", "isDeleted": true, "createdDate": "2026-10-03T00:00:00Z" },
              { "id": 3, "text": "<p>new</p>", "createdDate": "2026-10-02T00:00:00Z",
                "reactions": [ { "type": "like", "count": 2, "isCurrentUserEngaged": true }, { "type": "heart", "count": 0 } ] } ] }
            """);
        var comments = AzureDevOpsService.ParseDiscussion(doc.RootElement);
        Assert.Equal(new[] { 3, 1 }, comments.Select(c => c.Id));
        Assert.Equal(new CommentReaction("like", 2, true), Assert.Single(comments[0].Reactions));
        Assert.Equal("ada@example.org", comments[1].AuthorUniqueName);
    }

    [Fact]
    public void ReactionsUpdateInPlace()
    {
        var start = new List<CommentReaction> { new("like", 2, true), new("heart", 1, false) };
        Assert.Equal(new CommentReaction("like", 1, false), TaskDetailPanel.ApplyReaction(start, "like", false)[0]);
        Assert.Equal(new CommentReaction("heart", 2, true), TaskDetailPanel.ApplyReaction(start, "heart", true)[1]);
        Assert.Equal(new CommentReaction("laugh", 1, true), TaskDetailPanel.ApplyReaction(start, "laugh", true)[2]);
        Assert.Same(start[0], TaskDetailPanel.ApplyReaction(start, "like", true)[0]);
        Assert.DoesNotContain(TaskDetailPanel.ApplyReaction(new() { new("like", 1, true) }, "like", false), r => r.Type == "like");
    }

    [Fact]
    public void CommentTextBecomesHtmlWithMentions()
    {
        var html = TaskDetailPanel.MentionsToHtml(TaskDetailPanel.TextToHtml("Hi @Ada Lovelace\n<ok>"),
            new[] { new DevOpsMember("Ada Lovelace", "ada@example.org") });
        Assert.Equal("Hi <a href=\"mailto:ada@example.org\" data-vss-mention=\"version:2.0\">@Ada Lovelace</a><br>&lt;ok&gt;", html);
    }

    [Theory]
    [InlineData("abcdef1", "abcdef1")]
    [InlineData("https://x/_git/r/commit/ABCDEF12", "ABCDEF12")]
    [InlineData("abc", null)]
    [InlineData("not-a-sha", null)]
    public void PastedShas(string query, string? expected) => Assert.Equal(expected, LinkCodeDialog.ShaFromQuery(query));

    [Fact]
    public void NotificationSettingsLiveAtTheOrg() =>
        Assert.Equal($"{Org}/_usersSettings/notifications", TaskDetailPanel.NotificationSettingsUrl($"{Org}/Projects/_workitems/edit/42"));

    [Fact]
    public void OwnCommentsMatchByAccountOrDisplayName()
    {
        var me = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ada@example.org", "Ada Lovelace" };
        Assert.True(TaskDetailPanel.IsOwnComment(new WorkItemDiscussionComment { AuthorUniqueName = "ADA@example.org" }, me));
        Assert.True(TaskDetailPanel.IsOwnComment(new WorkItemDiscussionComment { Author = "Ada Lovelace" }, me));
        Assert.False(TaskDetailPanel.IsOwnComment(new WorkItemDiscussionComment { Author = "Bob" }, me));
        Assert.True(TaskDetailPanel.IsOwnComment(new WorkItemDiscussionComment { AuthorId = "9db1", Author = "Ada" }, new HashSet<string> { "9db1" }));
        Assert.False(TaskDetailPanel.IsOwnComment(new WorkItemDiscussionComment { Author = "Ada Lovelace" }, null));
    }
}
