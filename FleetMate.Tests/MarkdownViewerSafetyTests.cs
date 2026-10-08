using FleetMate.GUI.Views.Shared;
using Xunit;

namespace FleetMate.Tests;

/// <summary>Untrusted Markdown (Handbook pages, skills) never renders raw HTML, and its page loads nothing.</summary>
public class MarkdownViewerSafetyTests
{
    [Theory]
    [InlineData("<script>alert(1)</script>", "<script")]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>", "<iframe")]
    [InlineData("<img src=x onerror=\"alert(1)\">", "<img")]
    [InlineData("text <a href=\"#\" onclick=\"alert(1)\">x</a>", "<a href=\"#\" onclick")]
    public void RawHtmlIsShownAsText(string markdown, string forbidden)
    {
        var html = MarkdownViewer.ConvertMarkdownToHtml(markdown, untrusted: true);
        Assert.DoesNotContain(forbidden, html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;", html);
    }

    [Fact]
    public void TrustedContentStillRendersHtml() =>
        Assert.Contains("<b>", MarkdownViewer.ConvertMarkdownToHtml("<b>bold</b>"));

    [Fact]
    public void PolicyLoadsNothingButStylesAndSiteImages()
    {
        Assert.Equal("default-src 'none'; img-src data: https://handbook.example.org; style-src 'unsafe-inline'",
            MarkdownViewer.ContentSecurityPolicy("https://handbook.example.org/some/page/"));
        Assert.Equal("default-src 'none'; img-src data:; style-src 'unsafe-inline'",
            MarkdownViewer.ContentSecurityPolicy("file:///C:/"));
        Assert.Equal("default-src 'none'; img-src data:; style-src 'unsafe-inline'",
            MarkdownViewer.ContentSecurityPolicy(null));
        Assert.Equal("default-src 'none'; img-src data:; style-src 'unsafe-inline'",
            MarkdownViewer.ContentSecurityPolicy("https://x@evil.example/"));
    }
}
