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

        Assert.True(WidgetCatalog.Failed(queue, PullRequestSource.GitHub));
        Assert.False(WidgetCatalog.Failed(queue, PullRequestSource.AzureDevOps));
        Assert.True(WidgetCatalog.Failed(queue));
    }

    [Fact]
    public void BeingSignedOutOfGitHubIsNotAFailure()
    {
        var queue = QueueWith(PullRequestSource.GitHub, "Run gh auth login to sign in");

        Assert.False(WidgetCatalog.Failed(queue, PullRequestSource.GitHub));
        Assert.False(WidgetCatalog.Failed(queue));
    }

    [Fact]
    public void AQueueWithNoErrorsHasNoFailures()
    {
        Assert.False(WidgetCatalog.Failed(new PullRequestQueue()));
        Assert.False(WidgetCatalog.Failed(null));
    }
}
