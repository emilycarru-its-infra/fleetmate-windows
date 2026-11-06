using System.Net;
using System.Text;
using System.Text.Json;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Threaded replies, as FleetMate for Mac posts them: <c>POST /api/feed/{id}/replies</c>
/// with a <c>Body</c> and nothing else. Every fixture is synthetic, and
/// nothing here reaches a server.
/// </summary>
public class TdxFeedReplyTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = new();

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request, body));
            return _respond(request);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static TdxService Service(StubHandler handler)
    {
        var service = new TdxService(new TdxConfig { BaseUrl = "https://service.example.com", AppId = 7 }, handler);
        service.SetSsoToken("synthetic-token", DateTime.UtcNow.AddHours(1), "operator@example.com", "Operator");
        return service;
    }

    // ── Body ──────────────────────────────────────────────────────

    [Fact]
    public void PlainTextIsEscapedAndLineBreaksBecomeBreaks()
    {
        Assert.Equal("a &lt;b&gt; &amp; &quot;c&quot; &#39;d&#39;<br />next<br />last",
            TdxFeedReplyRequest.Html("a <b> & \"c\" 'd'\r\nnext\nlast"));
    }

    [Fact]
    public void AccentedTextIsLeftAsTyped()
    {
        Assert.Equal("Café déjà vu", TdxFeedReplyRequest.Html("Café déjà vu"));
    }

    [Fact]
    public void BodyIsTheOnlyEncodedField()
    {
        var json = JsonSerializer.Serialize(TdxFeedReplyRequest.FromPlainText("Thanks"));

        Assert.Equal("{\"Body\":\"Thanks\"}", json);
    }

    // ── Request ───────────────────────────────────────────────────

    [Fact]
    public async Task PostsToTheEntrysRepliesRoute()
    {
        var handler = new StubHandler(_ => Json(
            "{\"ID\":501,\"Body\":\"On it\",\"CreatedFullName\":\"Operator\",\"IsRichHtml\":true}",
            HttpStatusCode.Created));
        var service = Service(handler);

        var reply = await service.ReplyToFeedEntryAsync(77, "  On it  ");

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/TDWebApi/api/feed/77/replies", request.RequestUri!.AbsolutePath);
        Assert.Equal("{\"Body\":\"On it\"}", body);
        Assert.Equal(501, reply!.Id);
        Assert.Equal("On it", reply.Body);
    }

    [Fact]
    public async Task ABlankReplyIsRefusedBeforeAnyRequest()
    {
        var handler = new StubHandler(_ => Json("{}"));
        var service = Service(handler);

        await Assert.ThrowsAsync<TdxFeedReplyException>(() => service.ReplyToFeedEntryAsync(77, "  \n "));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "permission")]
    [InlineData(HttpStatusCode.NotFound, "12.3")]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
    public async Task AFailureReadsAsWords(HttpStatusCode status, string expected)
    {
        var service = Service(new StubHandler(_ => Json("{\"Message\":\"no\"}", status)));

        var error = await Assert.ThrowsAsync<TdxFeedReplyException>(() => service.ReplyToFeedEntryAsync(77, "Hello"));

        Assert.Contains(expected, error.Message);
        Assert.Equal((int)status, error.StatusCode);
        Assert.Equal(77, error.EntryId);
    }

    // ── Drafts ────────────────────────────────────────────────────

    [Fact]
    public void ADraftIsKeptPerEntryAndDroppedWhenBlank()
    {
        var drafts = new Dictionary<int, string>();

        FeedReplyDrafts.Keep(drafts, 1, "first");
        FeedReplyDrafts.Keep(drafts, 2, "second");
        FeedReplyDrafts.Keep(drafts, 1, "   ");

        Assert.Equal(new Dictionary<int, string> { [2] = "second" }, drafts);
    }

    [Fact]
    public void PostingKeepsTextTypedWhileItRan()
    {
        var drafts = new Dictionary<int, string> { [1] = "sent", [2] = "sent, then more" };

        FeedReplyDrafts.Posted(drafts, 1, "sent");
        FeedReplyDrafts.Posted(drafts, 2, "sent");

        Assert.False(drafts.ContainsKey(1));
        Assert.Equal("sent, then more", drafts[2]);
    }
}
