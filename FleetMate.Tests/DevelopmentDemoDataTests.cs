using FleetMate.Core.Development;
using Xunit;

namespace FleetMate.Tests;

/// <summary>Demo data is published in screenshots, so every link it carries must stay on example.com.</summary>
public class DevelopmentDemoDataTests
{
    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void EveryLinkIsOnTheExampleDomain()
    {
        var urls = DevelopmentDemoData.PullRequests(Now).PullRequests.Select(p => p.WebUrl)
            .Concat(DevelopmentDemoData.Commits(Now).SelectMany(r => r.Commits.Select(c => c.Url ?? "").Append(r.WebUrl)))
            .Concat(DevelopmentDemoData.Runs(Now).Select(r => r.WebUrl))
            .Concat(DevelopmentDemoData.Notifications(Now).Select(n => n.WebUrl));
        Assert.All(urls, url => Assert.Equal("example.com", new Uri(url).Host));
    }

    [Fact]
    public void EverySegmentHasSomethingToShow()
    {
        var pulls = DevelopmentDemoData.PullRequests(Now);
        Assert.Contains(pulls.PullRequests, p => p.Source == FleetMate.Core.Models.Projects.PullRequestSource.GitHub);
        Assert.Contains(pulls.PullRequests, p => p.Source == FleetMate.Core.Models.Projects.PullRequestSource.AzureDevOps);
        Assert.True(DevelopmentFilter.GroupByRepository(pulls.PullRequests, false).Count > 1);
        Assert.Contains(DevelopmentDemoData.Commits(Now), r => r.Commits.Count > DevelopmentText.CollapsedCommits);
        Assert.True(CommitsAndPipelinesFilter.FailingCount(DevelopmentDemoData.Runs(Now)) > 0);
        Assert.Contains(DevelopmentDemoData.Notifications(Now), n => n.Unread && n.IsPullRequest);
        Assert.NotEmpty(DevelopmentDemoData.Detail(pulls.PullRequests[0]).Files);
        Assert.NotEmpty(DevelopmentDemoData.Skills());
    }
}
