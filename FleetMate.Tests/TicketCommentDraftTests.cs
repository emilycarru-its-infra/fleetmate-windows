using FleetMate.Core.Models.Projects;
using FleetMate.GUI.Views.Shared;
using FleetMate.GUI.Views.Tickets;
using Xunit;

namespace FleetMate.Tests;

public class TicketQuoteTests
{
    private static FeedDisplayItem Entry(string author, string body) => new()
    {
        CreatedFullName = author,
        StrippedBody = body,
    };

    [Fact]
    public void QuotesTheAuthorAndBody()
    {
        var quote = TicketsPage.BuildQuote(Entry("Ada Lovelace", "The printer is jammed."));

        Assert.Equal("Ada Lovelace wrote:\n> The printer is jammed.", quote);
    }

    [Fact]
    public void PrefixesEveryLine()
    {
        var quote = TicketsPage.BuildQuote(Entry("Ada", "First line\nSecond line"));

        Assert.Equal("Ada wrote:\n> First line\n> Second line", quote);
    }

    [Fact]
    public void NamesAnUnknownAuthorRatherThanLeavingAGap()
    {
        var quote = TicketsPage.BuildQuote(Entry("", "text"));

        Assert.StartsWith("someone wrote:", quote);
    }

    [Fact]
    public void HandlesAnEmptyBody()
    {
        // A system entry can have no text; quoting it must not throw.
        var quote = TicketsPage.BuildQuote(Entry("Ada", ""));

        Assert.Equal("Ada wrote:\n> ", quote);
    }

    [Fact]
    public void TrimsTrailingWhitespaceFromQuotedLines()
    {
        var quote = TicketsPage.BuildQuote(Entry("Ada", "padded   \nlines  "));

        Assert.DoesNotContain("   \n", quote);
        Assert.Equal("Ada wrote:\n> padded\n> lines", quote);
    }
}

public class PullRequestQueueErrorTests
{
    private static PullRequestQueueError Error(PullRequestSource source, string message) =>
        new() { Source = source, Message = message };

    [Theory]
    // Not signed in to GitHub is a configuration state, not a fault. An orange
    // banner for it cries wolf on a queue working exactly as it should.
    //
    // These are the messages the stack actually produces, not plausible-looking
    // ones — the first is verbatim what GitHubGraphQLClient throws when the
    // token source comes back empty, and the third is GitHub's own reply.
    [InlineData("GitHub GraphQL: No authentication token available")]
    [InlineData("Not authenticated to GitHub")]
    [InlineData("GitHub GraphQL error: Bad credentials")]
    [InlineData("Run gh auth login to authenticate")]
    public void SignedOutGitHubIsNotReportedAsAFault(string message)
    {
        Assert.True(PullRequestQueueView.IsExpectedSignedOut(Error(PullRequestSource.GitHub, message)));
    }

    [Theory]
    [InlineData("API rate limit exceeded")]
    [InlineData("Could not resolve to a Repository")]
    [InlineData("HTTP 500: internal server error")]
    public void RealGitHubFailuresStillSurface(string message)
    {
        Assert.False(PullRequestQueueView.IsExpectedSignedOut(Error(PullRequestSource.GitHub, message)));
    }

    [Fact]
    public void AzureDevOpsFailuresAlwaysSurface()
    {
        // DevOps auth is brokered, so "not authenticated" there is a genuine
        // problem rather than an unconfigured provider.
        Assert.False(PullRequestQueueView.IsExpectedSignedOut(
            Error(PullRequestSource.AzureDevOps, "Not authenticated")));
    }

    [Fact]
    public void AnEmptyMessageIsNotTreatedAsSignedOut()
    {
        Assert.False(PullRequestQueueView.IsExpectedSignedOut(Error(PullRequestSource.GitHub, "")));
    }
}
