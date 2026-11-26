using FleetMate.Core.Shared;
using Xunit;

namespace FleetMate.Tests;

public class DiffRowsTests
{
    private static DiffFile File(string path, params DiffLine[] lines) => new()
    {
        OldPath = path,
        NewPath = path,
        Hunks = lines.Length == 0 ? new() : new() { new DiffHunk { Header = "@@ -1,2 +1,2 @@", Lines = lines.ToList() } },
    };

    private static DiffLine L(DiffLineKind kind, int? oldLine, int? newLine, string content = "code") =>
        new() { Kind = kind, OldLine = oldLine, NewLine = newLine, Content = content };

    [Fact]
    public void InlineFlattensHeaderHunkAndEveryLine()
    {
        var file = File("a.cs", L(DiffLineKind.Context, 1, 1), L(DiffLineKind.Deletion, 2, null), L(DiffLineKind.Addition, null, 2));
        var rows = DiffRows.Flatten(new[] { file }, DiffLayout.Inline);
        Assert.Equal(new[] { DiffRowKind.FileHeader, DiffRowKind.HunkHeader, DiffRowKind.Line, DiffRowKind.Line, DiffRowKind.Line },
            rows.Select(r => r.Kind));
        Assert.Equal(DiffLineKind.Deletion, rows[3].Line!.Kind);
    }

    [Fact]
    public void SplitPairsARemovedLineWithItsReplacement()
    {
        var file = File("a.cs", L(DiffLineKind.Context, 1, 1), L(DiffLineKind.Deletion, 2, null, "old"), L(DiffLineKind.Addition, null, 2, "new"));
        var rows = DiffRows.Flatten(new[] { file }, DiffLayout.Split);
        Assert.Equal(4, rows.Count);
        Assert.Equal("old", rows[3].Left!.Content);
        Assert.Equal("new", rows[3].Right!.Content);
    }

    [Fact]
    public void AFileWithNoHunksGetsTheNotice()
    {
        var rows = DiffRows.Flatten(new[] { File("image.png") }, DiffLayout.Split);
        Assert.Equal(new[] { DiffRowKind.FileHeader, DiffRowKind.EmptyNotice }, rows.Select(r => r.Kind));
    }

    [Theory]
    [InlineData(DiffLineKind.Addition, "Added line 7, code")]
    [InlineData(DiffLineKind.Deletion, "Removed line 7, code")]
    [InlineData(DiffLineKind.Context, "Unchanged line 7, code")]
    public void AccessibleTextNamesTheChange(DiffLineKind kind, string expected) =>
        Assert.Equal(expected, DiffText.Accessible(L(kind, 7, 7), "7"));

    [Fact]
    public void AnEmptySideIsSpokenAsNoLine() => Assert.Equal("No line", DiffText.Accessible(null, ""));

    [Fact]
    public void InlineLineIsNumberedByTheSideItHas()
    {
        Assert.Equal("Removed line 4, code", DiffText.Accessible(L(DiffLineKind.Deletion, 4, null)));
        Assert.Equal("Added line 9, code", DiffText.Accessible(L(DiffLineKind.Addition, null, 9)));
    }

    [Fact]
    public void SideTextCarriesTheMarkerAndTheRightNumber()
    {
        var line = L(DiffLineKind.Addition, null, 3);
        Assert.Equal("+ code", DiffText.SideContent(line));
        Assert.Equal("", DiffText.SideNumber(line, isLeft: true));
        Assert.Equal("3", DiffText.SideNumber(line, isLeft: false));
    }

    [Fact]
    public void FileStatusSaysAddedOrDeleted()
    {
        Assert.Equal("added", DiffText.FileStatus(new DiffFile { OldPath = "/dev/null", NewPath = "a" }));
        Assert.Equal("deleted", DiffText.FileStatus(new DiffFile { OldPath = "a", NewPath = "/dev/null" }));
        Assert.Equal("", DiffText.FileStatus(new DiffFile { OldPath = "a", NewPath = "a" }));
    }
}
