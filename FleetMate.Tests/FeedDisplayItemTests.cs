using System.Windows;
using FleetMate.GUI.Views.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// A feed card's reply state: which thread it belongs to, when Post Reply is
/// offered, and that the draft is reported as it is typed.
/// </summary>
public class FeedDisplayItemTests
{
    [Fact]
    public void AReplyBelongsToItsParentsThread()
    {
        var comment = new FeedDisplayItem { Id = 10, ThreadId = 10 };
        var reply = new FeedDisplayItem { Id = 11, ThreadId = 10 };

        Assert.False(comment.IsReply);
        Assert.True(reply.IsReply);
    }

    [Fact]
    public void PostReplyNeedsTextAndNoPostInFlight()
    {
        var thread = new FeedDisplayItem { Id = 10, ThreadId = 10 };
        Assert.False(thread.CanPostReply);

        thread.ReplyDraft = "  ";
        Assert.False(thread.CanPostReply);

        thread.ReplyDraft = "On it";
        Assert.True(thread.CanPostReply);

        thread.IsPostingReply = true;
        Assert.False(thread.CanPostReply);
    }

    [Fact]
    public void TypingReportsTheDraft()
    {
        var reported = new List<string>();
        var thread = new FeedDisplayItem { Id = 10, ThreadId = 10, ReplyDraftChanged = t => reported.Add(t.ReplyDraft) };

        thread.ReplyDraft = "a";
        thread.ReplyDraft = "a";
        thread.ReplyDraft = "ab";

        Assert.Equal(new[] { "a", "ab" }, reported);
    }

    [Fact]
    public void TheThreadShowsWithRepliesOrAnOpenComposer()
    {
        var thread = new FeedDisplayItem { Id = 10, ThreadId = 10 };
        Assert.Equal(Visibility.Collapsed, thread.ThreadVisibility);

        thread.IsComposing = true;
        Assert.Equal(Visibility.Visible, thread.ThreadVisibility);
        Assert.Equal(Visibility.Visible, thread.ComposerVisibility);

        thread.IsComposing = false;
        thread.Replies.Add(new FeedDisplayItem { Id = 11, ThreadId = 10 });
        Assert.Equal(Visibility.Visible, thread.ThreadVisibility);
    }

    [Fact]
    public void ThePrivateMarkerFollowsTheEntry()
    {
        Assert.Equal(Visibility.Visible, new FeedDisplayItem { IsPrivate = true }.PrivateVisibility);
        Assert.Equal(Visibility.Collapsed, new FeedDisplayItem().PrivateVisibility);
        Assert.Contains("private", new FeedDisplayItem { IsPrivate = true, CreatedFullName = "Operator" }.AccessibleName);
    }
}
