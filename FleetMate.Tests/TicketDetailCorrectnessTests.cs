using System.Text.Json;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using Xunit;

namespace FleetMate.Tests;

public class TicketDetailCorrectnessTests
{
    [Fact]
    public void ReturningToATicketDoesNotAcceptItsEarlierRequest()
    {
        var loads = new TicketDetailLoadState();
        var first = loads.Begin(1);
        var second = loads.Begin(2);
        var newest = loads.Begin(1);
        Assert.False(loads.IsCurrent(first));
        Assert.False(loads.IsCurrent(second));
        Assert.True(loads.IsCurrent(newest));
    }

    [Fact]
    public void RefreshAndCloseInvalidatePendingResponses()
    {
        var loads = new TicketDetailLoadState();
        var old = loads.Begin(1);
        var refresh = loads.Begin(1);
        Assert.False(loads.IsCurrent(old));
        loads.Invalidate();
        Assert.False(loads.IsCurrent(refresh));
    }

    [Fact]
    public void DraftEqualityIncludesPrivacyAndActualRecipients()
    {
        var sent = new TicketCommentDraft("Draft", true, ["requestor@example.com"]);
        Assert.True(sent.Matches(new("Draft", true, ["REQUESTOR@example.com"])));
        Assert.False(sent.Matches(new("Draft edited", true, ["requestor@example.com"])));
        Assert.False(sent.Matches(new("Draft", false, ["requestor@example.com"])));
        Assert.False(sent.Matches(new("Draft", true, [])));
        Assert.False(new TicketCommentDraft("", true, []).IsEmpty);
        Assert.True(TicketCommentDraft.Empty.IsEmpty);
    }

    [Theory]
    [InlineData(" user@example.com ", "user@example.com")]
    [InlineData("Person <user@example.com>", null)]
    [InlineData("group:42", null)]
    [InlineData("", null)]
    public void NotificationTargetsAreEmailAddresses(string value, string? expected) =>
        Assert.Equal(expected, TicketCommentDraft.NotificationEmail(value));

    [Fact]
    public void FeedPreservesReportedRecipientsWhenHydratingReplies()
    {
        var feed = JsonSerializer.Deserialize<TdxFeedEntry>("""{"ID":42,"NotifiedList":"requestor@example.com"}""")!;
        Assert.Equal("requestor@example.com", feed.WithReplies([]).NotifiedList);
        Assert.Null(JsonSerializer.Deserialize<TdxFeedEntry>("""{"ID":43}""")!.NotifiedList);
    }

    [Fact]
    public void CommentRequestSendsEmailStringsAndExplicitVisibility()
    {
        var json = JsonSerializer.Serialize(new CreateFeedEntryRequest
        {
            Comments = "Update", IsPrivate = false, Notify = ["requestor@example.com"]
        });
        using var parsed = JsonDocument.Parse(json);
        Assert.False(parsed.RootElement.GetProperty("IsPrivate").GetBoolean());
        Assert.Equal("requestor@example.com", parsed.RootElement.GetProperty("Notify")[0].GetString());
    }
}
