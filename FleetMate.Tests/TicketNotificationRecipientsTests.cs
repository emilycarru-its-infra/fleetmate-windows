using System.Text.Json;
using FleetMate.Core.Models.Tickets;
using Xunit;

namespace FleetMate.Tests;

public class TicketNotificationRecipientsTests
{
    [Fact]
    public void ReportedRecipientsSurviveReplyHydration()
    {
        var entry = JsonSerializer.Deserialize<TdxFeedEntry>("""{"ID":1,"NotifiedList":"reader@example.invalid"}""")!;
        var reply = JsonSerializer.Deserialize<TdxFeedEntry>("""{"ID":2,"NotifiedList":"reviewer@example.invalid"}""")!;

        var hydrated = entry.WithReplies([reply]);

        Assert.Equal("reader@example.invalid", hydrated.NotifiedList);
        Assert.Equal("reviewer@example.invalid", hydrated.ReplyList[0].NotifiedList);
    }

    [Theory]
    [InlineData("""{"ID":1}""", null)]
    [InlineData("""{"ID":2,"NotifiedList":null}""", null)]
    [InlineData("""{"ID":3,"NotifiedList":""}""", "")]
    public void UnknownRecipientsRemainDistinctFromAnEmptyList(string json, string? expected)
    {
        var entry = JsonSerializer.Deserialize<TdxFeedEntry>(json)!;

        Assert.Equal(expected, entry.WithReplies([]).NotifiedList);
    }
}
