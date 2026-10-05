using FleetMate.GUI.Views.Projects;
using Xunit;

namespace FleetMate.Tests;

/// <summary>The GitHub issue sidebar's helpers.</summary>
public class IssueSidebarTests
{
    [Theory]
    [InlineData("https://github.com/acme/fleet/issues/42", "acme", "fleet", 42)]
    [InlineData("https://github.com/acme/fleet/issues/7#issuecomment-1", "acme", "fleet", 7)]
    public void IssueUrlsParse(string url, string owner, string repo, int number) =>
        Assert.Equal((owner, repo, number), TaskDetailPanel.ParseIssueUrl(url));

    [Theory]
    [InlineData("https://github.com/acme/fleet/pull/42")]
    [InlineData(null)]
    [InlineData("")]
    public void PullRequestsAndDraftsAreNotIssues(string? url) => Assert.Null(TaskDetailPanel.ParseIssueUrl(url));

    [Fact]
    public void DuplicateReferencesTheOriginal() =>
        Assert.Equal(("Duplicate of #5: Fix it", "Duplicate of #5\n\nBody"), TaskDetailPanel.DuplicateOf(5, "Fix it", "Body"));

    [Fact]
    public void LabelColoursReadHex()
    {
        Assert.Equal(System.Windows.Media.Color.FromArgb(70, 0xd7, 0x3a, 0x4a), TaskDetailPanel.LabelColor("d73a4a", 70));
        Assert.Equal(System.Windows.Media.Color.FromArgb(70, 128, 128, 128), TaskDetailPanel.LabelColor("nope", 70));
    }

    [Fact]
    public void IssueIdsBeyondInt32Deserialize()
    {
        long Big(int n) => (long)int.MaxValue + n;
        var json = $$"""
            { "id": {{Big(1)}}, "node_id": "I_x", "number": 7, "title": "T", "state": "open",
              "user": { "id": {{Big(2)}}, "login": "ada" },
              "labels": [ { "id": {{Big(3)}}, "name": "bug", "color": "d73a4a" } ],
              "milestone": { "id": {{Big(4)}}, "number": 1, "title": "M" } }
            """;
        var issue = System.Text.Json.JsonSerializer.Deserialize<FleetMate.Core.Models.Projects.GitHubIssueDetail>(json)!;
        Assert.Equal(Big(1), issue.Id);
        Assert.Equal(Big(2), issue.User!.Id);
        Assert.Equal(Big(3), issue.Labels[0].Id);
        Assert.Equal(Big(4), issue.Milestone!.Id);
        var comment = System.Text.Json.JsonSerializer.Deserialize<FleetMate.Core.Models.Projects.GitHubComment>($$"""{ "id": {{Big(5)}}, "body": "b" }""")!;
        Assert.Equal(Big(5), comment.Id);
    }
}
