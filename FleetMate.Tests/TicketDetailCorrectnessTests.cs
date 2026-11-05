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
        Assert.Equal(newest, loads.Current);
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
        Assert.Null(loads.Current);
    }

    [Fact]
    public void DraftEqualityIncludesPrivacyAndRecipients()
    {
        var sent = new TicketCommentDraft("Draft", true, new[] { "requestor", "contact:2" });

        Assert.True(sent.Matches(new("Draft", true, new[] { "contact:2", "requestor" })));
        Assert.False(sent.Matches(new("Draft edited", true, new[] { "requestor", "contact:2" })));
        Assert.False(sent.Matches(new("Draft", false, new[] { "requestor", "contact:2" })));
        Assert.False(sent.Matches(new("Draft", true, Array.Empty<string>())));
    }

    [Fact]
    public void ADraftWithOnlyAChoiceIsKept()
    {
        Assert.True(TicketCommentDraft.Empty.IsEmpty);
        Assert.True(new TicketCommentDraft("  \n", false, Array.Empty<string>()).IsEmpty);
        Assert.False(new TicketCommentDraft("", true, Array.Empty<string>()).IsEmpty);
        Assert.False(new TicketCommentDraft("", false, new[] { "requestor" }).IsEmpty);
    }
}
