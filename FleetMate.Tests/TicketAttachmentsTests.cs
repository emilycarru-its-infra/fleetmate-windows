using FleetMate.Core.Models.Tickets;
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
    public void OnlyViewableTypesOpenDirectly(string name, bool canOpen)
    {
        Assert.Equal(canOpen, TicketAttachments.CanOpen(Named(name)));
    }

    [Theory]
    [InlineData("..\\..\\evil.png", "evil.png")]
    [InlineData("../../evil.png", "evil.png")]
    [InlineData("what?.txt", "what_.txt")]
    [InlineData("trailing.", "trailing")]
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
}
