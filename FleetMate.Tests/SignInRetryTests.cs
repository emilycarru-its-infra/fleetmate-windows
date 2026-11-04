using FleetMate.Core.Links;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The decision behind a request that fails before sign-in has finished, the
/// Retry button that follows it, and which links wait for Azure DevOps.
/// </summary>
public class SignInRetryTests
{
    [Fact]
    public void AFailureBeforeSignInFinishesWaits() =>
        Assert.Equal(SignInRetry.WaitForSignIn, SignInDecisions.AfterFailure(startedSignedIn: false, SignInStatus.SigningIn));

    [Fact]
    public void AFailureAfterSignInFailedStillWaitsForALaterSignIn()
    {
        Assert.Equal(SignInRetry.WaitForSignIn, SignInDecisions.AfterFailure(false, SignInStatus.Failed));
        Assert.Equal(SignInRetry.WaitForSignIn, SignInDecisions.AfterFailure(true, SignInStatus.Failed));
    }

    [Fact]
    public void SignInLandingMidRequestRetriesAtOnce() =>
        // The sign-in event that would re-run the request has already fired.
        Assert.Equal(SignInRetry.RetryNow, SignInDecisions.AfterFailure(startedSignedIn: false, SignInStatus.SignedIn));

    [Fact]
    public void AFailureWhileSignedInIsReported() =>
        Assert.Equal(SignInRetry.ShowError, SignInDecisions.AfterFailure(startedSignedIn: true, SignInStatus.SignedIn));

    [Fact]
    public void AnUnconfiguredServiceNeverWaits()
    {
        Assert.Equal(SignInRetry.ShowError, SignInDecisions.AfterFailure(false, SignInStatus.NotConfigured));
        Assert.Equal(SignInRetry.ShowError, SignInDecisions.AfterFailure(true, SignInStatus.NotConfigured));
    }

    [Fact]
    public void RetrySignsInFirstWhenSignInFailed() =>
        Assert.Equal(SignInRetryTap.SignInThenReload, SignInDecisions.OnRetry(SignInStatus.Failed));

    [Fact]
    public void RetryWaitsWhileSignInIsRunning() =>
        Assert.Equal(SignInRetryTap.WaitForSignIn, SignInDecisions.OnRetry(SignInStatus.SigningIn));

    [Fact]
    public void RetryReloadsWhenSignInIsNotTheProblem()
    {
        Assert.Equal(SignInRetryTap.Reload, SignInDecisions.OnRetry(SignInStatus.SignedIn));
        Assert.Equal(SignInRetryTap.Reload, SignInDecisions.OnRetry(SignInStatus.NotConfigured));
    }

    [Theory]
    [InlineData("fleetmate://workitem/12")]
    [InlineData("fleetmate://pull/Platform/Fleet/3")]
    [InlineData("fleetmate://commit/Platform/Agent/0a1b2c3d")]
    [InlineData("fleetmate://pipeline/Platform/42")]
    [InlineData("fleetmate://pipeline/Platform/definition/7")]
    public void AzureDevOpsLinksWaitForAzureDevOpsSignIn(string link) =>
        Assert.True(FleetMateLink.Parse(link).NeedsAzureDevOps());

    [Theory]
    [InlineData("fleetmate://pull/github/octo/tool/7")]
    [InlineData("fleetmate://commit/github/octo/tool/0a1b2c3d")]
    [InlineData("fleetmate://issue/github/octo/tool/12")]
    [InlineData("fleetmate://pipeline/github/octo/tool/99")]
    [InlineData("fleetmate://ticket/5")]
    [InlineData("fleetmate://asset/5")]
    public void OtherLinksNeverWaitForAzureDevOps(string link) =>
        Assert.False(FleetMateLink.Parse(link).NeedsAzureDevOps());
}
