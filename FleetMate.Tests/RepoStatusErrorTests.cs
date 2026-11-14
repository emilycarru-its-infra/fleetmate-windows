using FleetMate.Core.Services.Repos;
using Xunit;

namespace FleetMate.Tests;

public sealed class RepoStatusErrorTests
{
    [Fact]
    public void DubiousOwnershipIsOneShortLine()
    {
        var git = "fatal: detected dubious ownership in repository at 'C:/code/example'\n"
                  + "'C:/code/example' is owned by:\n\tEXAMPLE/other (S-1-5-21-1)\n"
                  + "but the current user is:\n\tEXAMPLE/me (S-1-5-21-2)\n"
                  + "To add an exception for this directory, call:\n\n\tgit config --global --add safe.directory C:/code/example\n";
        var line = RepoStatusError.Summarize(git);
        Assert.Equal("Owned by another account. Git won't read it.", line);
        Assert.DoesNotContain('\n', line);
    }

    [Theory]
    [InlineData("fatal: not a git repository (or any of the parent directories): .git", "Not a git repository")]
    [InlineData("fatal: Unable to create 'C:/code/example/.git/index.lock': File exists.", "Locked by another git process")]
    [InlineData("error: unknown option `porcelain=v9'\nusage: git status", "Unknown option `porcelain=v9'")]
    [InlineData("", "Git status failed")]
    [InlineData(null, "Git status failed")]
    public void OtherErrorsAreSummarized(string? git, string expected) =>
        Assert.Equal(expected, RepoStatusError.Summarize(git));

    [Fact]
    public void LongFirstLinesAreTrimmed()
    {
        var line = RepoStatusError.Summarize("fatal: " + new string('x', 200));
        Assert.True(line.Length <= 80);
        Assert.EndsWith("…", line);
    }
}
