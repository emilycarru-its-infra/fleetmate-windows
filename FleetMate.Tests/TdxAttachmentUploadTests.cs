using System.Net;
using System.Text;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The multipart body a comment's attachment goes up in, the requests that
/// carry it and the Reassign search, and the comment draft's file handling.
/// Every fixture is synthetic, and nothing here reaches a server.
/// </summary>
public class TdxAttachmentUploadTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public List<(HttpRequestMessage Request, byte[]? Body, string? ContentType)> Requests { get; } = new();

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsByteArrayAsync(ct);
            var type = request.Content?.Headers.TryGetValues("Content-Type", out var values) == true ? values.First() : null;
            Requests.Add((request, body, type));
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

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    // ── Multipart body ────────────────────────────────────────────

    [Fact]
    public void BodyIsOneFilePartBetweenBoundaries()
    {
        var form = new TdxMultipartForm("notes.txt", Encoding.UTF8.GetBytes("hello, ticket"), boundary: "XYZ");

        const string expected = "--XYZ\r\n"
            + "Content-Disposition: form-data; name=\"file\"; filename=\"notes.txt\"\r\n"
            + "Content-Type: text/plain\r\n\r\n"
            + "hello, ticket"
            + "\r\n--XYZ--\r\n";
        Assert.Equal(expected, Text(form.Body));
        Assert.Equal("multipart/form-data; boundary=XYZ", form.ContentType);
    }

    [Fact]
    public void BinaryPayloadIsCopiedByteForByte()
    {
        var payload = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF, 0x0D, 0x0A };
        var form = new TdxMultipartForm("image.png", payload, boundary: "B");

        var header = Encoding.UTF8.GetBytes("--B\r\nContent-Disposition: form-data; name=\"file\"; filename=\"image.png\"\r\nContent-Type: image/png\r\n\r\n");
        var footer = Encoding.UTF8.GetBytes("\r\n--B--\r\n");
        Assert.Equal(header.Concat(payload).Concat(footer).ToArray(), form.Body);
    }

    [Theory]
    [InlineData("a.pdf", "application/pdf")]
    [InlineData("a.PNG", "image/png")]
    [InlineData("a.jpeg", "image/jpeg")]
    [InlineData("a.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("no-extension", "application/octet-stream")]
    [InlineData("a.unknownext123", "application/octet-stream")]
    public void MimeTypeFollowsExtension(string name, string expected) =>
        Assert.Equal(expected, TdxMultipartForm.MimeType(name));

    [Fact]
    public void ExplicitMimeTypeWins()
    {
        var form = new TdxMultipartForm("a.log", Array.Empty<byte>(), mimeType: "text/x-log", boundary: "B");
        Assert.Contains("Content-Type: text/x-log\r\n", Text(form.Body));
    }

    [Theory]
    [InlineData("/tmp/dir/say \"hi\".txt")]
    [InlineData(@"C:\Users\someone\say ""hi"".txt")]
    public void QuotesAndPathsCannotBreakTheHeader(string name)
    {
        var text = Text(new TdxMultipartForm(name, Array.Empty<byte>(), boundary: "B").Body);
        Assert.Contains("filename=\"say \\\"hi\\\".txt\"", text);
        Assert.DoesNotContain("dir", text);
        Assert.DoesNotContain("someone", text);
    }

    [Fact]
    public void LineBreaksInNameAreDropped()
    {
        var text = Text(new TdxMultipartForm("evil\r\nX-Injected: 1.txt", Array.Empty<byte>(), boundary: "B").Body);
        Assert.DoesNotContain("\r\nX-Injected", text);
        Assert.Contains("filename=\"evilX-Injected: 1.txt\"", text);
    }

    [Fact]
    public void NonAsciiNameGetsRfc5987Parameter()
    {
        var text = Text(new TdxMultipartForm("café.txt", Array.Empty<byte>(), boundary: "B").Body);
        Assert.Contains("filename=\"caf_.txt\"; filename*=UTF-8''caf%C3%A9.txt", text);
    }

    [Fact]
    public void Rfc5987KeepsAttrCharsAndEncodesTheRest() =>
        Assert.Equal("a%20b%27c%2Ad!#$&+-.^_`|~", TdxMultipartForm.Rfc5987Encode("a b'c*d!#$&+-.^_`|~"));

    [Fact]
    public void EmptyNameFallsBack() =>
        Assert.Contains("filename=\"attachment\"", Text(new TdxMultipartForm("  ", Array.Empty<byte>(), boundary: "B").Body));

    [Fact]
    public void DefaultBoundaryIsUniquePerForm() =>
        Assert.NotEqual(new TdxMultipartForm("a.txt", Array.Empty<byte>()).Boundary,
                        new TdxMultipartForm("a.txt", Array.Empty<byte>()).Boundary);

    // ── Upload request ────────────────────────────────────────────

    [Fact]
    public async Task UploadPostsMultipartToTicketAttachments()
    {
        var handler = new StubHandler(_ => Json("""{"ID":"00000000-0000-4000-8000-0000000000a1","Name":"report.pdf","Size":3}"""));

        var attachment = await Service(handler).UploadAttachmentOrThrowAsync(42, "report.pdf", Encoding.UTF8.GetBytes("pdf"), isPrivate: true);

        var (request, body, contentType) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/TDWebApi/api/7/tickets/42/attachments", request.RequestUri!.AbsolutePath);
        Assert.Equal("?isPrivate=true", request.RequestUri.Query);
        Assert.Equal("Bearer synthetic-token", request.Headers.Authorization?.ToString());

        Assert.NotNull(contentType);
        Assert.StartsWith("multipart/form-data; boundary=", contentType);
        var boundary = contentType!["multipart/form-data; boundary=".Length..];
        var text = Text(body!);
        Assert.StartsWith($"--{boundary}\r\n", text);
        Assert.Contains("name=\"file\"; filename=\"report.pdf\"", text);
        Assert.Contains("Content-Type: application/pdf\r\n\r\npdf\r\n", text);
        Assert.EndsWith($"--{boundary}--\r\n", text);

        Assert.Equal("report.pdf", attachment?.Name);
    }

    [Fact]
    public async Task UploadDefaultsToPublicAndToleratesAnUnreadableAnswer()
    {
        var handler = new StubHandler(_ => Json("not json"));

        var attachment = await Service(handler).UploadAttachmentOrThrowAsync(42, "a.txt", new byte[] { 1 });

        Assert.Null(attachment);
        Assert.Equal("?isPrivate=false", handler.Requests[0].Request.RequestUri!.Query);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "", "huge.mov: the file is larger than TeamDynamix accepts")]
    [InlineData(HttpStatusCode.Forbidden, "", "huge.mov: you don't have permission to attach files to this ticket")]
    [InlineData(HttpStatusCode.BadRequest, "Attachments are disabled.", "huge.mov: Attachments are disabled.")]
    [InlineData(HttpStatusCode.InternalServerError, "", "huge.mov: TeamDynamix answered 500")]
    public async Task UploadFailureNamesTheFileAndReason(HttpStatusCode status, string body, string expected)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });

        var error = await Assert.ThrowsAsync<TdxUploadException>(() =>
            Service(handler).UploadAttachmentOrThrowAsync(42, "huge.mov", new byte[] { 1 }));

        Assert.Equal((int)status, error.StatusCode);
        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public async Task BooleanUploadStillReportsFailureAsFalse()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        Assert.False(await Service(handler).UploadAttachmentAsync(42, "a.txt", new byte[] { 1 }));
    }

    // ── Eligible responsibles ─────────────────────────────────────

    [Fact]
    public async Task EligibleResponsiblesSearchTheTicketingAppResources()
    {
        var handler = new StubHandler(_ => Json("""
            [{"Name":"Pat Example","Value":"00000000-0000-4000-8000-000000000001","Email":"pat@example.invalid","IsUser":true},
             {"Name":"Example Group","Value":"77","Email":null,"IsUser":false}]
            """));
        var service = Service(handler);

        var found = await service.SearchEligibleResponsiblesAsync("  Ex&ample ");

        var request = Assert.Single(handler.Requests).Request;
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.EndsWith("/TDWebApi/api/7/tickets/resources", request.RequestUri!.AbsolutePath);
        Assert.Equal("?searchText=ex%26ample", request.RequestUri.Query);

        Assert.Equal(2, found.Count);
        Assert.True(found[0].IsUser);
        Assert.Equal("pat@example.invalid", found[0].Subtitle);
        Assert.NotNull(found[0].Uid);
        Assert.Null(found[0].GroupId);
        Assert.False(found[1].IsUser);
        Assert.Equal(77, found[1].GroupId);
        Assert.Equal("Group", found[1].Subtitle);
        Assert.NotEqual(found[0].Key, found[1].Key);

        // A repeat search is served from the cache.
        await service.SearchEligibleResponsiblesAsync("ex&ample");
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task EligibleSearchFailureThrows()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        await Assert.ThrowsAsync<TdxRequestException>(() => Service(handler).SearchEligibleResponsiblesAsync("pat"));
    }

    [Fact]
    public void PickingAGroupClearsTheResponsiblePerson()
    {
        var group = new TdxEligibleAssignment { Value = "77", Name = "Example Group", IsUser = false };
        var updates = group.ResponsibleUpdates()!;
        Assert.Equal(77, updates["ResponsibleGroupID"]);
        Assert.Equal("", updates["ResponsibleUid"]);

        var person = new TdxEligibleAssignment { Value = "00000000-0000-4000-8000-000000000001", Name = "Pat", IsUser = true };
        Assert.Equal(new[] { "ResponsibleUid" }, person.ResponsibleUpdates()!.Keys);

        Assert.Null(new TdxEligibleAssignment { Value = "not-a-uid", IsUser = true }.ResponsibleUpdates());
    }

    [Fact]
    public void CurrentResponsibleIsMarked()
    {
        var uid = Guid.Parse("00000000-0000-4000-8000-000000000001");
        var person = new TdxEligibleAssignment { Value = uid.ToString(), IsUser = true };
        var group = new TdxEligibleAssignment { Value = "77", IsUser = false };

        Assert.True(person.IsCurrent(uid, 77));
        Assert.False(group.IsCurrent(uid, 77));
        Assert.True(group.IsCurrent(null, 77));
    }

    [Fact]
    public void PeopleAreListedBeforeGroups()
    {
        var ordered = TdxEligibleAssignment.Ordered(new[]
        {
            new TdxEligibleAssignment { Value = "1", Name = "G1", IsUser = false },
            new TdxEligibleAssignment { Value = "u1", Name = "P1", IsUser = true },
            new TdxEligibleAssignment { Value = "2", Name = "G2", IsUser = false },
            new TdxEligibleAssignment { Value = "u2", Name = "P2", IsUser = true },
        });
        Assert.Equal(new[] { "P1", "P2", "G1", "G2" }, ordered.Select(a => a.Name));
    }

    [Fact]
    public async Task ReassignWithNotifyAsksTdxToEmailTheNewResponsible()
    {
        var handler = new StubHandler(_ => Json("""{"ID":42}"""));
        await Service(handler).UpdateTicketAsync(42, new Dictionary<string, object?> { ["ResponsibleUid"] = "x" }, notifyNewResponsible: true);
        Assert.Equal("?notifyNewResponsible=true", handler.Requests[0].Request.RequestUri!.Query);
    }

    // ── Comment draft files ───────────────────────────────────────

    [Fact]
    public void DraftSkipsMissingFilesAndDuplicates()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[2048]);
            var draft = new List<PendingCommentAttachment>();

            Assert.Equal(1, CommentAttachments.Add(draft, new[] { path, path.ToUpperInvariant(), Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid()) }));
            Assert.Single(draft);
            Assert.Equal(2048, draft[0].Size);
            Assert.Equal("2 KB", draft[0].SizeLabel);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PastedImagesAreNamedLikeScreenshots() =>
        Assert.Equal("Pasted Image 2026-03-04 at 14.05.09.png",
            CommentAttachments.PastedImageName(new DateTime(2026, 3, 4, 14, 5, 9)));

    [Fact]
    public async Task UploadPassKeepsOnlyTheFailedFiles()
    {
        var files = new[]
        {
            new PendingCommentAttachment(@"C:\x\a.txt", "a.txt", 1),
            new PendingCommentAttachment(@"C:\x\b.txt", "b.txt", 1),
            new PendingCommentAttachment(@"C:\x\c.txt", "c.txt", 1),
        };
        var uploaded = new List<string>();

        var failed = await CommentAttachments.UploadAllAsync(files, f =>
        {
            if (f.Name == "b.txt") throw new TdxUploadException(f.Name, 413);
            uploaded.Add(f.Name);
            return Task.CompletedTask;
        });

        Assert.Equal(new[] { "a.txt", "c.txt" }, uploaded);
        var (file, error) = Assert.Single(failed);
        Assert.Equal("b.txt", file.Name);
        Assert.Equal("b.txt: the file is larger than TeamDynamix accepts", error);

        var message = CommentAttachments.FailureMessage(failed, hasText: true);
        Assert.Contains("comment was not posted", message);
        Assert.Contains("b.txt: the file is larger than TeamDynamix accepts.", message);
    }
}

public class TicketInboxBadgeTests
{
    [Theory]
    [InlineData(0, "", "No unread")]
    [InlineData(1, "1", "1 unread")]
    [InlineData(99, "99", "99 unread")]
    [InlineData(250, "99+", "250 unread")]
    public void BadgeShowsTheUnreadCount(int unread, string label, string spoken)
    {
        Assert.Equal(label, TicketInboxBadge.Label(unread));
        Assert.Equal(spoken, TicketInboxBadge.AccessibleValue(unread));
    }
}
