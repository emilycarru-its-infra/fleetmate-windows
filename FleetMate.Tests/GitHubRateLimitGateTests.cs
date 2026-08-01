using FleetMate.Core.Services.Projects;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The gate is process-wide mutable state, so these run in one collection to
/// keep them from tripping each other. Each resets it first.
/// </summary>
[Collection("GitHubRateLimitGate")]
public class GitHubRateLimitGateTests : IDisposable
{
    public GitHubRateLimitGateTests() => GitHubRateLimitGate.Reset();
    public void Dispose() => GitHubRateLimitGate.Reset();

    [Fact]
    public void AnOpenGateLetsCallsThrough()
    {
        GitHubRateLimitGate.Check();

        Assert.False(GitHubRateLimitGate.IsTripped);
        Assert.Null(GitHubRateLimitGate.OpenAt);
    }

    [Fact]
    public void TrippingClosesTheGate()
    {
        GitHubRateLimitGate.Trip();

        Assert.True(GitHubRateLimitGate.IsTripped);
        Assert.Throws<GitHubRateLimitException>(GitHubRateLimitGate.Check);
    }

    [Fact]
    public void TheErrorSaysRateLimit()
    {
        // Callers that already special-case throttling match on this phrase to
        // show a quiet cached-results note rather than an error.
        GitHubRateLimitGate.Trip();

        var ex = Assert.Throws<GitHubRateLimitException>(GitHubRateLimitGate.Check);
        Assert.Contains("rate limit", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheErrorCarriesTheRetryTime()
    {
        GitHubRateLimitGate.Trip(TimeSpan.FromMinutes(15));

        var ex = Assert.Throws<GitHubRateLimitException>(GitHubRateLimitGate.Check);
        Assert.True(ex.RetryAt > DateTimeOffset.UtcNow.AddMinutes(14));
    }

    [Fact]
    public void AnExpiredWindowReopensTheGate()
    {
        // A negative backoff puts the deadline in the past, standing in for a
        // window that has elapsed.
        GitHubRateLimitGate.Trip(TimeSpan.FromSeconds(-1));

        GitHubRateLimitGate.Check();
        Assert.False(GitHubRateLimitGate.IsTripped);
    }

    [Fact]
    public void ALaterTripExtendsTheWindow()
    {
        GitHubRateLimitGate.Trip(TimeSpan.FromMinutes(5));
        var first = GitHubRateLimitGate.OpenAt;

        GitHubRateLimitGate.Trip(TimeSpan.FromMinutes(30));
        var second = GitHubRateLimitGate.OpenAt;

        Assert.True(second > first);
    }

    [Fact]
    public void AShorterTripDoesNotCutTheWindowShort()
    {
        // A stray secondary limit must not release a client that GitHub has
        // already told to wait longer.
        GitHubRateLimitGate.Trip(TimeSpan.FromMinutes(30));
        var long_ = GitHubRateLimitGate.OpenAt;

        GitHubRateLimitGate.Trip(TimeSpan.FromMinutes(1));

        Assert.Equal(long_, GitHubRateLimitGate.OpenAt);
    }

    [Theory]
    [InlineData("API rate limit exceeded for user ID 1234.")]
    [InlineData("You have exceeded a secondary rate limit.")]
    [InlineData("You have triggered an abuse detection mechanism.")]
    [InlineData("RATE LIMIT EXCEEDED")]
    public void RecognisesGitHubsThrottlingLanguage(string message)
    {
        Assert.True(GitHubRateLimitGate.LooksLikeRateLimit(message));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Could not resolve to a Repository with the name 'x/y'.")]
    [InlineData("Bad credentials")]
    [InlineData("Resource not accessible by integration")]
    public void OrdinaryErrorsDoNotTripIt(string? message)
    {
        Assert.False(GitHubRateLimitGate.LooksLikeRateLimit(message));

        GitHubRateLimitGate.TripIfRateLimit(message);
        Assert.False(GitHubRateLimitGate.IsTripped);
    }

    [Fact]
    public void A429AlwaysTrips()
    {
        GitHubRateLimitGate.TripIfRateLimitStatus(429);

        Assert.True(GitHubRateLimitGate.IsTripped);
    }

    [Fact]
    public void A403OnlyTripsWhenTheBodySaysRateLimit()
    {
        // 403 is ambiguous — it is also plain forbidden. Latching on every
        // permission error would silence a client that works fine but lacks
        // access to one repository.
        GitHubRateLimitGate.TripIfRateLimitStatus(403, "Resource not accessible by integration");
        Assert.False(GitHubRateLimitGate.IsTripped);

        GitHubRateLimitGate.TripIfRateLimitStatus(403, "API rate limit exceeded");
        Assert.True(GitHubRateLimitGate.IsTripped);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(404)]
    [InlineData(422)]
    [InlineData(500)]
    public void OtherStatusesLeaveItOpen(int status)
    {
        GitHubRateLimitGate.TripIfRateLimitStatus(status, "something went wrong");

        Assert.False(GitHubRateLimitGate.IsTripped);
    }

    [Fact]
    public void TheGateIsSharedAcrossCallers()
    {
        // The whole point: one client tripping it must stop the others, since
        // they drain the same hourly quota.
        GitHubRateLimitGate.TripIfRateLimit("API rate limit exceeded");

        Assert.Throws<GitHubRateLimitException>(GitHubRateLimitGate.Check);
        Assert.True(GitHubRateLimitGate.IsTripped);
    }

    [Fact]
    public void ConcurrentTripsAndChecksDoNotTearState()
    {
        Parallel.For(0, 200, i =>
        {
            if (i % 2 == 0) GitHubRateLimitGate.Trip(TimeSpan.FromMinutes(5));
            else try { GitHubRateLimitGate.Check(); } catch (GitHubRateLimitException) { }
        });

        Assert.True(GitHubRateLimitGate.IsTripped);
    }
}
