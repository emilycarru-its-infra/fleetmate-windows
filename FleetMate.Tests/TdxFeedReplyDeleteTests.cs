using System.Net;
using System.Text;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Delete Reply… and a reply's files, as FleetMate for Mac has them:
/// <c>DELETE /api/feed/{id}/replies/{replyId}</c>, offered only on one's own
/// replies. Every fixture is synthetic, and nothing here reaches a server.
/// </summary>
public class TdxFeedReplyDeleteTests
{
    private static readonly Guid Me = new("3c9e1a40-0000-0000-0000-000000000001");
    private static readonly Guid Someone = new("3c9e1a40-0000-0000-0000-000000000002");

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public List<HttpRequestMessage> Requests { get; } = new();

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_respond(request));
        }
    }

    private static HttpResponseMessage Status(HttpStatusCode status) =>
        new(status) { Content = new StringContent("{\"Message\":\"no\"}", Encoding.UTF8, "application/json") };

    private static TdxService Service(StubHandler handler)
    {
        var service = new TdxService(new TdxConfig { BaseUrl = "https://service.example.com", AppId = 7 }, handler);
        service.SetSsoToken("synthetic-token", DateTime.UtcNow.AddHours(1), "operator@example.com", "Operator");
        return service;
    }

    [Fact]
    public void ThePathNamesTheThreadAndTheReply()
    {
        Assert.Equal("feed/77/replies/501", TdxFeedReplyRequest.DeletePath(77, 501));
    }

    [Fact]
    public async Task DeleteSendsADeleteToTheReplysRoute()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var service = Service(handler);

        await service.DeleteFeedReplyAsync(77, 501);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.EndsWith("/TDWebApi/api/feed/77/replies/501", request.RequestUri!.AbsolutePath);
        Assert.Null(request.Content);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "permission to delete")]
    [InlineData(HttpStatusCode.NotFound, "12.3")]
    [InlineData(HttpStatusCode.InternalServerError, "couldn't delete the reply (HTTP 500)")]
    public async Task AFailedDeleteReadsAsWords(HttpStatusCode status, string expected)
    {
        var service = Service(new StubHandler(_ => Status(status)));

        var error = await Assert.ThrowsAsync<TdxFeedReplyException>(() => service.DeleteFeedReplyAsync(77, 501));

        Assert.Contains(expected, error.Message);
        Assert.Equal((int)status, error.StatusCode);
        Assert.Equal(77, error.EntryId);
    }

    [Fact]
    public void OnlyYourOwnReplyCanBeDeleted()
    {
        Assert.True(TdxFeedReplyRequest.CanDelete(Me, Me));
        Assert.False(TdxFeedReplyRequest.CanDelete(Someone, Me));
        Assert.False(TdxFeedReplyRequest.CanDelete(null, Me));
        Assert.False(TdxFeedReplyRequest.CanDelete(Me, null));
        Assert.False(TdxFeedReplyRequest.CanDelete(Guid.Empty, Guid.Empty));
    }

    [Fact]
    public void FilesAloneCanBePostedAndChangeTheButton()
    {
        var thread = new FleetMate.GUI.Views.Tickets.FeedDisplayItem { Id = 77, ThreadId = 77 };
        thread.ReplyAttachments = new System.Collections.ObjectModel.ObservableCollection<PendingCommentAttachment>();
        Assert.False(thread.CanPostReply);
        Assert.Equal("Post Reply", thread.PostReplyLabel);

        thread.ReplyAttachments.Add(new PendingCommentAttachment(@"C:\synthetic\shot.png", "shot.png", 2048));

        Assert.True(thread.CanPostReply);
        Assert.Equal("Post Reply with Attachments", thread.PostReplyLabel);
        thread.IsPostingReply = true;
        Assert.False(thread.CanPostReply);
        Assert.False(thread.CanAttachReply);
    }

    [Fact]
    public void DeleteReplyIsOfferedOnOwnRepliesOnly()
    {
        var reply = new FleetMate.GUI.Views.Tickets.FeedDisplayItem { Id = 501, ThreadId = 77, CanDelete = true };
        var comment = new FleetMate.GUI.Views.Tickets.FeedDisplayItem { Id = 77, ThreadId = 77, CanDelete = true };
        var others = new FleetMate.GUI.Views.Tickets.FeedDisplayItem { Id = 502, ThreadId = 77 };

        Assert.Equal(System.Windows.Visibility.Visible, reply.DeleteReplyVisibility);
        Assert.Equal(System.Windows.Visibility.Collapsed, comment.DeleteReplyVisibility);
        Assert.Equal(System.Windows.Visibility.Collapsed, others.DeleteReplyVisibility);
    }

    [Fact]
    public void AReplysFailedFilesSayTheReplyWasHeldBack()
    {
        var file = new PendingCommentAttachment(@"C:\synthetic\log.txt", "log.txt", 12);
        var failed = new List<(PendingCommentAttachment File, string Error)> { (file, "log.txt: locked") };

        Assert.Contains("so the reply was not posted", CommentAttachments.FailureMessage(failed, hasText: true, post: "reply"));
        Assert.Contains("so the comment was not posted", CommentAttachments.FailureMessage(failed, hasText: true));
    }
}
