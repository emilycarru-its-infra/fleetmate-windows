using FleetMate.Core.Models.Projects;
using FleetMate.GUI.Views.Development;
using FleetMate.GUI.Views.Shared;
using Xunit;

namespace FleetMate.Tests;

/// <summary>The repository dropdown shared by Pulls, Commits, Pipelines and the dashboard queue.</summary>
public class RepoFilterMenuTests
{
    [Fact]
    public void Counts_BusiestFirstThenByName()
    {
        var counts = RepoFilterMenu.Counts(new[] { "b", "a", "c", "b", "a", "b" }, s => s);
        Assert.Equal(new[] { ("b", 3), ("a", 2), ("c", 1) }, counts);
    }

    [Fact]
    public void Counts_EmptyWhenOnlyOneRepository() =>
        Assert.Empty(RepoFilterMenu.Counts(new[] { "a", "a" }, s => s));

    private static RepositoryCommits Repo(PullRequestSource source, string container, string name, int commits) => new()
    {
        Source = source,
        Container = container,
        Repository = name,
        Commits = Enumerable.Range(0, commits)
            .Select(i => new PullRequestCommit { Id = $"{name}{i}", Message = "m", Date = DateTime.UtcNow.AddMinutes(-i) })
            .ToList(),
    };

    [Fact]
    public void Commits_DropdownCountsCommitsWithinTheSource()
    {
        var repos = new[]
        {
            Repo(PullRequestSource.GitHub, "acme", "fleet", 2),
            Repo(PullRequestSource.GitHub, "acme", "tools", 5),
            Repo(PullRequestSource.AzureDevOps, "Platform", "infra", 9),
        };

        Assert.Equal(new[] { ("acme/tools", 5), ("acme/fleet", 2) },
            CommitsAndPipelinesFilter.CommitRepositoryCounts(repos, DevelopmentSourceFilter.GitHub));
        Assert.Equal("Platform/infra",
            CommitsAndPipelinesFilter.CommitRepositoryCounts(repos, DevelopmentSourceFilter.All)[0].Repository);

        var picked = CommitsAndPipelinesFilter.Commits(repos, DevelopmentSourceFilter.All, null, "acme/fleet");
        Assert.Equal("acme/fleet", Assert.Single(picked).DisplayName);
    }

    private static PipelineRun Run(PullRequestSource source, string container, string? repo, int id) => new()
    {
        Source = source,
        Container = container,
        Repository = repo,
        PipelineName = "CI",
        RunId = id,
        RunNumber = id.ToString(),
        Status = PipelineRunStatus.Succeeded,
        StartedAt = DateTime.UtcNow.AddMinutes(-id),
    };

    [Fact]
    public void Runs_DropdownKeysByRepositoryAndFilters()
    {
        var runs = new[]
        {
            Run(PullRequestSource.GitHub, "acme", "fleet", 1),
            Run(PullRequestSource.GitHub, "acme", "fleet", 2),
            Run(PullRequestSource.AzureDevOps, "Platform", null, 3),
        };

        Assert.Equal(new[] { ("acme/fleet", 2), ("Platform", 1) },
            CommitsAndPipelinesFilter.RunRepositoryCounts(runs, DevelopmentSourceFilter.All));

        // Within one source there is only one repository: no choice to offer.
        Assert.Empty(CommitsAndPipelinesFilter.RunRepositoryCounts(runs, DevelopmentSourceFilter.GitHub));

        var picked = CommitsAndPipelinesFilter.Runs(runs, DevelopmentSourceFilter.All, PipelineStatusFilter.All, null, "Platform");
        Assert.Equal(3, Assert.Single(picked).RunId);
    }
}
