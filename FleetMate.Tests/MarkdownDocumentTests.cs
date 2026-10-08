using FleetMate.GUI.Views.Shared;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Pull request descriptions and shared skills are drawn as a WPF document,
/// never in a browser: markup becomes formatting, raw HTML stays text, and
/// only web links can be followed. WPF documents need an STA thread.
/// </summary>
public class MarkdownDocumentTests
{
    private static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw failure;
        return result;
    }

    [Fact]
    public void MarkupBecomesFormattingNotText()
    {
        var text = OnSta(() => MarkdownDocument.PlainText("## Summary\n\n- **Bold** item\n- `code` item\n\n[Docs](https://example.com/docs)"));

        Assert.Contains("Summary", text);
        Assert.Contains("Bold item", text);
        Assert.Contains("code item", text);
        Assert.Contains("Docs", text);
        Assert.DoesNotContain("##", text);
        Assert.DoesNotContain("**", text);
        Assert.DoesNotContain("](", text);
    }

    [Fact]
    public void RawHtmlIsShownAsTextAndNeverBecomesALink()
    {
        var text = OnSta(() => MarkdownDocument.PlainText("<script>alert(1)</script>\n\nHello <b>there</b>"));
        Assert.Contains("<script>", text);
        Assert.Contains("<b>", text);
    }

    [Fact]
    public void OnlyWebLinksCanBeFollowed()
    {
        var links = OnSta(() => MarkdownDocument.Links(
            "[web](https://example.com/a) [file](file:///C:/Windows/notepad.exe) [js](javascript:alert(1)) <https://example.com/b>"));

        Assert.Equal(new[] { "https://example.com/a", "https://example.com/b" }, links.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public void ImagesAreNotFetched()
    {
        var text = OnSta(() => MarkdownDocument.PlainText("![diagram](https://example.com/x.png)"));
        Assert.Contains("[image: diagram]", text);
    }

    [Fact]
    public void EmptyTextGivesAnEmptyDocument()
    {
        Assert.Equal("", OnSta(() => MarkdownDocument.PlainText(null)).Trim());
    }
}
