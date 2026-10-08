using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services;
using Xunit;

namespace FleetMate.Tests;

public class TicketAttachmentsTests
{
    private static TdxAttachment Named(string? name, long size = 0) => new() { Id = Guid.NewGuid(), Name = name, Size = size };

    [Theory]
    [InlineData("screenshot.png", true)]
    [InlineData("Report.PDF", true)]
    [InlineData("notes.txt", true)]
    [InlineData("setup.exe", false)]
    [InlineData("fix.ps1", false)]
    [InlineData("macro.docm", false)]
    [InlineData("shortcut.lnk", false)]
    [InlineData("no-extension", false)]
    // Double extensions are judged on the last one, which is what Windows runs.
    [InlineData("invoice.pdf.exe", false)]
    [InlineData("photo.png.js", false)]
    // Trailing dots and spaces are stripped on write, so they cannot hide the real type.
    [InlineData("setup.exe.", false)]
    [InlineData("setup.exe . ", false)]
    [InlineData("photo.png ", true)]
    // A colon would name an alternate data stream; it is replaced, not honoured.
    [InlineData("photo.png:evil.exe", false)]
    [InlineData("notes.txt:hidden", false)]
    [InlineData(".png", false)]
    [InlineData("trailing.", false)]
    public void OnlyViewableTypesOpenDirectly(string name, bool canOpen)
    {
        Assert.Equal(canOpen, TicketAttachments.CanOpen(Named(name)));
    }

    [Theory]
    [InlineData("..\\..\\evil.png", "evil.png")]
    [InlineData("../../evil.png", "evil.png")]
    [InlineData("what?.txt", "what_.txt")]
    [InlineData("trailing.", "trailing")]
    [InlineData("setup.exe . ", "setup.exe")]
    [InlineData("photo.png:evil.exe", "photo.png_evil.exe")]
    [InlineData("a<b>|c.txt", "a_b__c.txt")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("nul", "_nul")]
    [InlineData("", "Attachment")]
    [InlineData(null, "Attachment")]
    public void FileNamesCannotEscapeTheirFolder(string? name, string expected)
    {
        Assert.Equal(expected, TicketAttachments.SafeFileName(Named(name)));
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(5 * 1024 * 1024, "5 MB")]
    public void SizesReadInTheLargestWholeUnit(long bytes, string expected)
    {
        Assert.Equal(expected, TicketAttachments.SizeLabel(bytes));
    }

    [Fact]
    public void TheSubtitleLeavesOutWhatIsUnknown()
    {
        Assert.Equal("", TicketAttachments.Subtitle(Named("a.png")));
        Assert.Equal("2 KB", TicketAttachments.Subtitle(Named("a.png", 2048)));
    }

    [Fact]
    public void TheMarkNamesTheInternetZoneAndTheHost()
    {
        var content = MarkOfTheWeb.Content("https://service.example.com/TDWebApi");
        Assert.Contains("[ZoneTransfer]", content);
        Assert.Contains("ZoneId=3", content);
        Assert.Contains("HostUrl=https://service.example.com/", content);
        Assert.DoesNotContain("HostUrl", MarkOfTheWeb.Content(null));
    }

    [Fact]
    public void ADownloadedFileCarriesTheMark()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), $"motw-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "attachment");
        try
        {
            Assert.False(MarkOfTheWeb.IsMarked(path));
            Assert.True(MarkOfTheWeb.Apply(path, "https://service.example.com/"));
            Assert.True(MarkOfTheWeb.IsMarked(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
