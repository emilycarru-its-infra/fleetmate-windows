using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.GUI.Views.Development;
using Xunit;

namespace FleetMate.Tests;

public sealed class RepositoryFilterScopeTests
{
    [Fact]
    public void QualifiedKeysEscapeComponentsAndKeepProvidersDistinct()
    {
        var scope = new RepositoryFilterScope(PullRequestSource.GitHub, "Example / Owner", "Tools#One");
        Assert.Equal("repository/github/example%20%2F%20owner/tools%23one", scope.Key);
        Assert.Equal(scope.Key, RepositoryFilterScope.FromKey(scope.Key)!.Key);
        Assert.False(scope.Matches(new RepositoryFilterScope(PullRequestSource.AzureDevOps, scope.Container, scope.Repository).Key));
        Assert.True(scope.Matches("Example / Owner/Tools#One"));
        Assert.False(scope.Matches("Tools#One"));
        Assert.True(scope.Matches("Tools#One", allowRepositoryOnly: true));
        Assert.Null(RepositoryFilterScope.FromKey("example/tools"));
    }

    [Fact]
    public void HierarchyRetainsDuplicateNamesAndWeightedTotalsInAlphabeticalOrder()
    {
        var rows = new[]
        {
            (Scope: new RepositoryFilterScope(PullRequestSource.GitHub, "Owner", "zeta"), Count: 2),
            (Scope: new RepositoryFilterScope(PullRequestSource.GitHub, "Owner", "alpha"), Count: 3),
            (Scope: new RepositoryFilterScope(PullRequestSource.AzureDevOps, "Owner", "alpha"), Count: 5),
            (Scope: new RepositoryFilterScope(PullRequestSource.GitHub, "owner", "ALPHA"), Count: 7),
        };
        var entries = RepositoryFilterHierarchy.Count(rows, row => row.Scope, row => row.Count);
        Assert.Equal(3, entries.Count);
        Assert.Equal(17, entries.Sum(entry => entry.Count));
        var github = entries.Where(entry => entry.Scope.Source == PullRequestSource.GitHub).ToList();
        Assert.Equal(new[] { "alpha", "zeta" }, github.Select(entry => entry.Scope.Repository));
        Assert.Equal(10, github[0].Count);
    }

    [Fact]
    public void QualifiedSelectionsFilterDuplicateRunsAndStillSupportGraphLabels()
    {
        var runs = new[] { PullRequestSource.GitHub, PullRequestSource.AzureDevOps }.Select((source, index) => new PipelineRun
        {
            Source = source, Container = "Example", Repository = "tools", RunId = index + 1,
            PipelineName = "Build", Status = PipelineRunStatus.Running,
        }).ToList();
        var scope = new RepositoryFilterScope(PullRequestSource.GitHub, "Example", "tools");
        Assert.Single(CommitsAndPipelinesFilter.Runs(runs, DevelopmentSourceFilter.All, PipelineStatusFilter.All, null, scope.Key));
        Assert.Equal(2, CommitsAndPipelinesFilter.Runs(runs, DevelopmentSourceFilter.All, PipelineStatusFilter.All, null, scope.Label).Count);
        var plan = new PipelineRefreshPlan();
        var targets = plan.GitHub(Array.Empty<PipelineRun>(), new[] { new PipelineRepositoryScope("Example", "tools"), new PipelineRepositoryScope("Other", "repo") }, null, scope.Key);
        Assert.Equal("Example", targets[0].Container);
        var projectScope = new RepositoryFilterScope(PullRequestSource.AzureDevOps, "Example");
        Assert.Equal("Example", plan.AzureDevOps(Array.Empty<PipelineRun>(), new[] { "Other", "Example" }, null, projectScope.Key)[0].Container);
    }
}
