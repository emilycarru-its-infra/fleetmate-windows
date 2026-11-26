using FleetMate.Core.Links;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Shared;
using Xunit;

namespace FleetMate.Tests;

public class CommitsAndPipelinesDetailTests
{
    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

    private static PipelineRun Run(PipelineRunStatus status, PullRequestSource source = PullRequestSource.GitHub, int? pipelineId = 1) => new()
    {
        Source = source,
        Container = "contoso",
        Repository = "widgets",
        PipelineName = "CI",
        PipelineId = pipelineId,
        RunId = 7,
        RunNumber = "7",
        Status = status,
        CommitSha = "0123456789abcdef",
        TriggeredBy = "Avery",
    };

    [Fact]
    public void CommitBodyDropsTheSubjectLine()
    {
        Assert.Equal("", CommitDetailText.Body("Only a subject"));
        Assert.Equal("Why it changed.", CommitDetailText.Body("Subject\n\nWhy it changed.\n"));
    }

    [Fact]
    public void ChangesHeaderCountsDiffsOrTheChangeList()
    {
        var diff = new CommitDetail { Files = { new DiffFile(), new DiffFile() }, Additions = 5, Deletions = 2 };
        Assert.Equal("Changes (2 files, +5 −2)", CommitDetailText.ChangesHeader(diff));
        var list = new CommitDetail { Changes = { new CommitChange { Path = "/a" } } };
        Assert.Equal("Changes (1 files)", CommitDetailText.ChangesHeader(list));
    }

    [Fact]
    public void RunDetailsShortenTheShaAndSkipWhatIsMissing() =>
        Assert.Equal("01234567 · Avery", PipelineRunDetailText.Details(Run(PipelineRunStatus.Succeeded)));

    [Theory]
    [InlineData(PipelineRunStatus.Running, true, false)]
    [InlineData(PipelineRunStatus.Queued, true, false)]
    [InlineData(PipelineRunStatus.Failed, false, true)]
    [InlineData(PipelineRunStatus.Succeeded, false, true)]
    public void CancelWhileActiveRerunOnceFinished(PipelineRunStatus status, bool cancel, bool rerun)
    {
        Assert.Equal(cancel, PipelineRunDetailText.CanCancel(Run(status)));
        Assert.Equal(rerun, PipelineRunDetailText.CanRerun(Run(status)));
    }

    [Fact]
    public void AnAzureRunWithNoPipelineCannotBeRerun() =>
        Assert.False(PipelineRunDetailText.CanRerun(Run(PipelineRunStatus.Failed, PullRequestSource.AzureDevOps, pipelineId: null)));

    [Fact]
    public void FailedSectionsOpenOtherwiseTheLast()
    {
        PipelineRunLog Log(params PipelineRunStatus[] statuses) => new()
        {
            Sections = statuses.Select((s, i) => new PipelineRunLog.Section { Id = $"{i}", Status = s }).ToList(),
        };
        Assert.Equal(new[] { false, true, true }, PipelineRunDetailText.ExpandedSections(Log(PipelineRunStatus.Succeeded, PipelineRunStatus.Failed, PipelineRunStatus.Partial)));
        Assert.Equal(new[] { false, true }, PipelineRunDetailText.ExpandedSections(Log(PipelineRunStatus.Succeeded, PipelineRunStatus.Succeeded)));
        Assert.Empty(PipelineRunDetailText.ExpandedSections(Log()));
    }

    [Fact]
    public void ApprovalReviewWords()
    {
        Assert.Equal("No instructions given.", ApprovalReviewText.Instructions(new PipelineApproval()));
        Assert.Equal("", ApprovalReviewText.More(1));
        Assert.Equal("2 more after this one", ApprovalReviewText.More(3));
        Assert.Equal("Review CI #7", ApprovalReviewText.Title(Run(PipelineRunStatus.Running)));
    }

    [Fact]
    public void ACommitLinkFindsTheLoadedCommitByShaPrefix()
    {
        var repos = DevelopmentDemoData.Commits(Now);
        var target = repos[0];
        var sha = target.Commits[1].Id;
        var (repository, commit) = DevelopmentLinkTargets.Commit(repos,
            new FleetMateLink.Commit(new FleetMateLink.GitHub(target.Container.ToUpperInvariant(), target.Repository), sha[..7]));
        Assert.Same(target, repository);
        Assert.Same(target.Commits[1], commit);
    }

    [Fact]
    public void ACommitLinkNotLoadedOpensBySha()
    {
        var (repository, commit) = DevelopmentLinkTargets.Commit(null, new FleetMateLink.Commit(new FleetMateLink.AzureDevOps("Proj", "Repo"), "abc123"));
        Assert.Equal(PullRequestSource.AzureDevOps, repository.Source);
        Assert.Equal("Repo", repository.RepositoryId);
        Assert.Equal("abc123", commit.Id);
    }

    [Fact]
    public void RunLinksMatchTheirRun()
    {
        var runs = DevelopmentDemoData.Runs(Now);
        Assert.Equal(3301, DevelopmentLinkTargets.Run(runs, new FleetMateLink.AzureDevOpsRun("fabrikam", 3301))?.RunId);
        Assert.Equal(990, DevelopmentLinkTargets.Run(runs, new FleetMateLink.AzureDevOpsPipeline("Fabrikam", 21))?.RunId);
        Assert.Equal(77, DevelopmentLinkTargets.Run(runs, new FleetMateLink.GitHubRun("northwind-labs", "design-tokens", 77))?.RunId);
        Assert.Null(DevelopmentLinkTargets.Run(runs, new FleetMateLink.GitHubRun("northwind-labs", "design-tokens", 1)));
    }

    [Fact]
    public async Task DemoDetailsCoverEveryPane()
    {
        var repos = DevelopmentDemoData.Commits(Now);
        Assert.NotEmpty(DevelopmentDemoData.CommitDetail(repos[0], repos[0].Commits[0]).Files);
        Assert.NotEmpty(DevelopmentDemoData.CommitDetail(repos[2], repos[2].Commits[0]).Changes);

        var runs = DevelopmentDemoData.Runs(Now);
        var failed = runs.First(r => r.Status == PipelineRunStatus.Failed);
        Assert.Contains(DevelopmentDemoData.RunLog(failed).Sections, s => s.Status == PipelineRunStatus.Failed);

        var tracker = new FleetMate.Core.Services.Projects.PipelineApprovalTracker();
        await tracker.RefreshAsync(runs, DevelopmentDemoData.Approvals(Now));
        Assert.Equal(1, CommitsAndPipelinesFilter.ApprovalRunCount(runs, tracker.IsAwaiting));
    }
}
