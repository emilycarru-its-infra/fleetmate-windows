using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using FleetMate.Core.Shared;
using FleetMate.Core.Services.Projects;
using FleetMate.GUI.Views.Shared;
using Xunit;

namespace FleetMate.Tests;

public sealed class RecordAttachmentTests
{
    [Fact]
    public void TitledClipboardLinkEscapesMarkupAndUsesUtf8Offsets()
    {
        var payload = RecordLinkPayload.Create("Café <review> & 📎", "https://example.invalid/item/1?q=a&b=2");
        Assert.Equal("Café <review> & 📎\nhttps://example.invalid/item/1?q=a&b=2", payload.Text);
        var bytes = Encoding.UTF8.GetBytes(payload.Html);
        int Offset(string label) => int.Parse(Regex.Match(payload.Html, label + @":(\d+)").Groups[1].Value);
        var fragment = Encoding.UTF8.GetString(bytes[Offset("StartFragment")..Offset("EndFragment")]);
        Assert.Equal("<a href=\"https://example.invalid/item/1?q=a&amp;b=2\">Caf&#233; &lt;review&gt; &amp; &#128206;</a>", fragment);
        Assert.Equal(bytes.Length, Offset("EndHTML"));
        Assert.Throws<ArgumentException>(() => RecordLinkPayload.Create("title", "file:///unsafe.exe"));
    }

    [Fact]
    public void WorkItemAttachmentUrlsStayInsideTheConfiguredOrganization()
    {
        var org = new Uri("https://workitems.example.invalid/example");
        const string attachment = "/_apis/wit/attachments/00000000-0000-0000-0000-000000000001";
        Assert.True(RecordAttachments.IsDevOpsAttachment(new Uri(org + attachment), org));
        Assert.False(RecordAttachments.IsDevOpsAttachment(new Uri("https://workitems.example.invalid/other" + attachment), org));
        Assert.False(RecordAttachments.IsDevOpsAttachment(new Uri("https://workitems.example.invalid.evil.invalid/example" + attachment), org));
        Assert.False(RecordAttachments.IsDevOpsAttachment(new Uri("http://workitems.example.invalid/example" + attachment), org));
        Assert.Equal(FleetMate.Core.Models.Projects.WorkItemRelationKind.Attachment, AzureDevOpsService.RelationKind("AttachedFile"));
    }

    [Fact]
    public void RenderedPrivateAssetsKeepTheOriginalBrowserLinkAndName()
    {
        const string id = "00000000-0000-0000-0000-000000000001";
        var raw = $"![Diagram](https://github.com/user-attachments/assets/{id})";
        var html = $"<img src=\"https://private-user-images.githubusercontent.com/1/{id}.png?jwt=synthetic\">";
        var asset = Assert.Single(RecordAttachments.GitHub(raw, html, "https://github.com/example/repo/issues/1"));
        Assert.Equal("Diagram", asset.Name);
        Assert.Equal($"https://github.com/user-attachments/assets/{id}", asset.OriginalUrl);
        Assert.Contains("private-user-images.githubusercontent.com", asset.Url);
        Assert.Empty(RecordAttachments.Discover("<img src='https://evil.invalid/image.png'>", RecordAttachments.IsGitHubAsset));
    }

    [Fact]
    public void SeparateSignedAssetsDoNotMatchOnlyBecauseTheyShareTheIssueBrowserFallback()
    {
        const string browser = "https://github.com/example/repo/issues/1";
        var first = new RecordAttachment("one", "https://private-user-images.githubusercontent.com/1/one.png?jwt=first", browser);
        var second = new RecordAttachment("two", "https://private-user-images.githubusercontent.com/1/two.png?jwt=second", browser);
        Assert.False(RecordAttachmentSections.Matches(first, second));
        Assert.True(RecordAttachmentSections.Matches(first, first with { Url = "https://private-user-images.githubusercontent.com/1/one.png?jwt=refreshed" }));
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000001", "application/pdf", ".pdf")]
    [InlineData("00000000-0000-0000-0000-000000000001", "image/png", ".png")]
    [InlineData("report.pdf", "application/octet-stream", ".pdf")]
    [InlineData("program.exe", "application/octet-stream", ".bin")]
    public void PreviewFileExtensionsFollowTrustedMimeAndSafeFileTypes(string name, string type, string expected) =>
        Assert.Equal(expected, RecordAttachments.SafeExtension(name, type));

    [Fact]
    public void LegacyRepositoryFileUploadsAreRecognizedWithoutAllowingOtherOrigins()
    {
        Assert.True(RecordAttachments.IsGitHubAsset(new Uri("https://github.com/example/repo/files/1/document.pdf")));
        Assert.True(RecordAttachments.IsGitHubAsset(new Uri("https://github.com/example/repo/assets/1/image.png")));
        Assert.False(RecordAttachments.IsGitHubAsset(new Uri("https://github.com.evil.invalid/example/repo/files/1/document.pdf")));
    }

    [Fact]
    public void PdfResourcesCannotReadAnotherFileOrFetchRemoteSubresources()
    {
        var path = Path.Combine(Path.GetTempPath(), "preview.pdf");
        Assert.True(RecordAttachmentPreview.AllowsLocalResource(new Uri(path).AbsoluteUri, path));
        Assert.False(RecordAttachmentPreview.AllowsLocalResource(new Uri(Path.Combine(Path.GetTempPath(), "other.txt")).AbsoluteUri, path));
        Assert.False(RecordAttachmentPreview.AllowsLocalResource("https://example.invalid/tracker", path));
        Assert.False(RecordAttachmentPreview.AllowsLocalResource("file://files.example.invalid/share/private.txt", path));
    }

    private sealed class WaitingBody : Stream
    {
        public bool Closed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }

    [Fact]
    public async Task DeadlineCancelsAStalledBodyAfterHeadersHaveCompleted()
    {
        var body = new WaitingBody();
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(body) });
        var attachment = new RecordAttachment("document.pdf", "https://github.com/example/repo/files/1/document.pdf", "https://github.com/example/repo/files/1/document.pdf");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RecordAttachments.DownloadWithDeadlineAsync(attachment,
            new Uri("https://github.com"), "synthetic-token", RecordAttachments.IsGitHubAsset, TimeSpan.FromMilliseconds(100), handler: handler));
        Assert.True(body.Closed);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(send(request));
    }

    [Fact]
    public async Task CrossOriginRedirectsDropCredentialsAndDownloadsRemainTemporary()
    {
        var calls = new List<(string Host, string? Token)>();
        using var handler = new Handler(request =>
        {
            calls.Add((request.RequestUri!.Host, request.Headers.Authorization?.Parameter));
            if (calls.Count == 1)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://private-user-images.githubusercontent.com/1/example.png?jwt=synthetic");
                return redirect;
            }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };
        });
        var attachment = new RecordAttachment("image.png", "https://github.com/user-attachments/assets/example", "https://github.com/user-attachments/assets/example");
        var staged = await RecordAttachments.DownloadAsync(attachment, new Uri("https://github.com"), "synthetic-token", RecordAttachments.IsGitHubAsset, handler: handler);
        Assert.Equal("synthetic-token", calls[0].Token);
        Assert.Null(calls[1].Token);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(staged.Path));
        var path = staged.Path;
        staged.Dispose();
        for (var attempt = 0; attempt < 30 && File.Exists(path); attempt++) await Task.Delay(100);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task UnrecognizedRedirectsAreRejectedBeforeAnotherRequest()
    {
        var calls = 0;
        using var handler = new Handler(_ =>
        {
            calls++;
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://evil.invalid/file.png");
            return redirect;
        });
        var attachment = new RecordAttachment("image.png", "https://github.com/user-attachments/assets/example", "https://github.com/user-attachments/assets/example");
        await Assert.ThrowsAsync<HttpRequestException>(() => RecordAttachments.DownloadAsync(attachment, new Uri("https://github.com"), "synthetic-token", RecordAttachments.IsGitHubAsset, handler: handler));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoginHtmlAndOversizedResponsesCannotBecomeAttachments(bool html)
    {
        using var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("<!DOCTYPE html><html>Sign in</html>")) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            if (!html) response.Content.Headers.ContentLength = RecordAttachments.MaximumBytes + 1;
            return response;
        });
        var attachment = new RecordAttachment("document.pdf", "https://github.com/user-attachments/files/1/document.pdf", "https://github.com/user-attachments/files/1/document.pdf");
        await Assert.ThrowsAsync<InvalidDataException>(() => RecordAttachments.DownloadAsync(attachment, new Uri("https://github.com"), "synthetic-token", RecordAttachments.IsGitHubAsset, handler: handler));
    }
}
