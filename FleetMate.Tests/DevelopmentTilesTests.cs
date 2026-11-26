using FleetMate.Core.Models.Projects;
using FleetMate.GUI.Views.Shared.Widgets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The Development overview tiles: a provider that failed this load has no
/// count to show, so its tile reads "--" rather than a misleading 0.
/// </summary>
public class DevelopmentTilesTests
{
    private static PullRequestQueue QueueWith(PullRequestSource source, string message) => new()
    {
        Errors = { new PullRequestQueueError { Source = source, Message = message } },
    };

    [Fact]
    public void ARateLimitedProviderCountsAsFailed()
    {
        var queue = QueueWith(PullRequestSource.GitHub, "API rate limit exceeded");

        Assert.True(DevelopmentFigures.Failed(queue, PullRequestSource.GitHub));
        Assert.False(DevelopmentFigures.Failed(queue, PullRequestSource.AzureDevOps));
        Assert.True(DevelopmentFigures.Failed(queue));
    }

    [Fact]
    public void BeingSignedOutOfGitHubIsNotAFailure()
    {
        var queue = QueueWith(PullRequestSource.GitHub, "Run gh auth login to sign in");

        Assert.False(DevelopmentFigures.Failed(queue, PullRequestSource.GitHub));
        Assert.False(DevelopmentFigures.Failed(queue));
    }

    [Fact]
    public void AQueueWithNoErrorsHasNoFailures()
    {
        Assert.False(DevelopmentFigures.Failed(new PullRequestQueue()));
        Assert.False(DevelopmentFigures.Failed(null));
    }
}
