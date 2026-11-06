using FleetMate.Core.Services.Teams;
using Xunit;

namespace FleetMate.Tests;

/// <summary>Links and names from Teams message content before they are shown or opened.</summary>
public class TeamsContentSafetyTests
{
    private static readonly IReadOnlyList<string> Hosts = TeamsLinkPolicy.Hosts("example.edu");

    [Theory]
    [InlineData("https://teams.microsoft.com/l/message/19:x/1")]
    [InlineData("https://teams.cloud.microsoft/l/message/1")]
    [InlineData("https://contoso.sharepoint.com/sites/x/a.docx")]
    [InlineData("https://example.edu/page")]
    [InlineData("https://wiki.example.edu/page")]
    public void AllowedLinksOpen(string link) => Assert.NotNull(TeamsLinkPolicy.SafeUri(link, Hosts));

    [Theory]
    [InlineData("http://teams.microsoft.com/l/message/1")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:privacy")]
    [InlineData("\\\\server\\share\\run.exe")]
    [InlineData("https://evil.test/")]
    [InlineData("https://teams.microsoft.com.evil.test/")]
    [InlineData("https://example.edu.evil.test/")]
    [InlineData("https://notexample.edu/")]
    [InlineData("https://sharepoint.com/")]
    [InlineData("https://user:pass@teams.microsoft.com/")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void EverythingElseIsRefused(string? link) => Assert.Null(TeamsLinkPolicy.SafeUri(link, Hosts));

    [Fact]
    public void ExtraHostsOnlyWhenConfigured() =>
        Assert.Null(TeamsLinkPolicy.SafeUri("https://example.edu/", TeamsLinkPolicy.Hosts(null)));

    [Theory]
    [InlineData("Alex\u202Etxt.exe", "Alextxt.exe")]
    [InlineData("Zero\u200Bwidth\u2060", "Zerowidth")]
    [InlineData("Two\nlines\u0007", "Two lines")]
    public void NamesLoseControlAndFormatCharacters(string raw, string expected) =>
        Assert.Equal(expected, TeamsText.OneLine(raw));

    [Fact]
    public void BodiesKeepLineBreaksAndTabs() =>
        Assert.Equal("First\nSecondline\tok", TeamsText.TerminalSafe("First\u202E\nSecond\u0000line\tok"));
}
