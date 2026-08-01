using System.Windows;
using System.Windows.Media;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Shared;
using FleetMate.GUI.Views.Shared;
using Xunit;

namespace FleetMate.Tests;

public class DiffLineViewModelTests
{
    private static DiffLineViewModel Vm(DiffLineKind kind, int? oldLine = null, int? newLine = null) =>
        new()
        {
            Line = new DiffLine { Kind = kind, OldLine = oldLine, NewLine = newLine, Content = "code" },
        };

    [Theory]
    [InlineData(DiffLineKind.Addition, "+")]
    [InlineData(DiffLineKind.Deletion, "-")]
    [InlineData(DiffLineKind.Context, " ")]
    [InlineData(DiffLineKind.NoNewline, "\\")]
    public void RestoresTheMarkerForDisplay(DiffLineKind kind, string expected)
    {
        // The parser strips it from Content, so the view has to put it back.
        Assert.Equal(expected, Vm(kind).Marker);
    }

    [Fact]
    public void MissingLineNumbersRenderBlank()
    {
        // A deletion has no line on the new side. Showing 0 would put a number
        // in the gutter next to a line that is not there.
        var deletion = Vm(DiffLineKind.Deletion, oldLine: 12);

        Assert.Equal("12", deletion.OldLineLabel);
        Assert.Equal("", deletion.NewLineLabel);
    }

    [Fact]
    public void ContextRowsCarryBothNumbers()
    {
        var context = Vm(DiffLineKind.Context, oldLine: 5, newLine: 7);

        Assert.Equal("5", context.OldLineLabel);
        Assert.Equal("7", context.NewLineLabel);
    }

    [Fact]
    public void ChangedRowsAreTintedAndContextIsNot()
    {
        Assert.Equal(Brushes.Transparent, Vm(DiffLineKind.Context).RowBackground);
        Assert.NotEqual(Brushes.Transparent, Vm(DiffLineKind.Addition).RowBackground);
        Assert.NotEqual(Brushes.Transparent, Vm(DiffLineKind.Deletion).RowBackground);
    }

    [Fact]
    public void AdditionsAndDeletionsAreVisuallyDistinct()
    {
        Assert.NotEqual(
            Vm(DiffLineKind.Addition).RowBackground.ToString(),
            Vm(DiffLineKind.Deletion).RowBackground.ToString());
    }

    [Fact]
    public void ChangedRowsGetAGutterStrip()
    {
        // The strip carries the signal where the row tint is too subtle, and is
        // what makes the diff readable without relying on red versus green.
        Assert.NotEqual(Brushes.Transparent, Vm(DiffLineKind.Addition).GutterStrip);
        Assert.NotEqual(Brushes.Transparent, Vm(DiffLineKind.Deletion).GutterStrip);
        Assert.Equal(Brushes.Transparent, Vm(DiffLineKind.Context).GutterStrip);
    }

    [Fact]
    public void DiffTintsAreFrozenAndShared()
    {
        // Thousands of rows would otherwise allocate a brush each.
        var one = Vm(DiffLineKind.Addition).RowBackground;
        var two = Vm(DiffLineKind.Addition).RowBackground;

        Assert.True(one.IsFrozen);
        Assert.Same(one, two);
    }

    [Fact]
    public void TheNoNewlineMarkerIsRenderedAsAnAside()
    {
        var marker = Vm(DiffLineKind.NoNewline);

        Assert.Equal(FontStyles.Italic, marker.ContentStyle);
        Assert.Equal(Brushes.Gray, marker.ContentBrush);
    }

    [Fact]
    public void ResolvesWithoutARunningApplication()
    {
        // Application.Current is null in tests and the designer; a getter that
        // throws there takes the whole diff down.
        Assert.NotNull(Vm(DiffLineKind.Context).ContentBrush);
    }
}

public class DiffFileViewModelTests
{
    [Fact]
    public void SummarisesInsertionsAndDeletions()
    {
        var vm = new DiffFileViewModel
        {
            File = DiffBuilder.Build("a.txt", "one\ntwo", "one\nTWO"),
        };

        Assert.Equal("+1  −1", vm.StatLabel);
    }

    [Fact]
    public void NamesTheFileByItsDisplayPath()
    {
        var vm = new DiffFileViewModel { File = DiffBuilder.Build("src/App.cs", "a", "b") };

        // Not the a// b/ prefixed form.
        Assert.Equal("src/App.cs", vm.Path);
    }

    [Fact]
    public void LabelsAddedAndDeletedFiles()
    {
        var added = new DiffFileViewModel
        {
            File = new DiffFile { OldPath = "/dev/null", NewPath = "b/new.txt" },
        };
        var deleted = new DiffFileViewModel
        {
            File = new DiffFile { OldPath = "a/gone.txt", NewPath = "/dev/null" },
        };
        var modified = new DiffFileViewModel { File = DiffBuilder.Build("a.txt", "a", "b") };

        Assert.Equal("added", added.StatusLabel);
        Assert.Equal("deleted", deleted.StatusLabel);
        Assert.Equal("", modified.StatusLabel);
        Assert.Equal(Visibility.Collapsed, modified.StatusVisibility);
    }

    [Fact]
    public void AFileWithNoHunksExplainsItself()
    {
        // Binary, too large, or renamed without changes. The card still renders
        // because the reader needs to know it changed.
        var vm = new DiffFileViewModel
        {
            File = new DiffFile { OldPath = "a/logo.png", NewPath = "b/logo.png" },
        };

        Assert.Equal(Visibility.Visible, vm.EmptyNoticeVisibility);
        Assert.Contains("binary", vm.EmptyNotice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFileWithHunksHidesTheNotice()
    {
        var vm = new DiffFileViewModel { File = DiffBuilder.Build("a.txt", "one", "ONE") };

        Assert.Equal(Visibility.Collapsed, vm.EmptyNoticeVisibility);
    }

    [Fact]
    public void ExposesHunksInOrder()
    {
        var old = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"line {i:D4}"));
        var updated = old.Replace("line 0010", "A").Replace("line 0090", "B");

        var vm = new DiffFileViewModel { File = DiffBuilder.Build("a.txt", old, updated) };

        Assert.Equal(2, vm.Hunks.Count);
        Assert.All(vm.Hunks, h => Assert.NotEmpty(h.Lines));
    }
}

public class PullRequestCommentViewModelTests
{
    private static PullRequestCommentViewModel Vm(string body, bool isSystem = false, DateTime? date = null) =>
        new()
        {
            Comment = new PullRequestComment
            {
                Id = "1", AuthorName = "Ada", Body = body, IsSystem = isSystem, Date = date,
            },
        };

    [Fact]
    public void SystemNoiseIsGreyedDown()
    {
        // It is context, not conversation; equal weight buries what people said.
        Assert.Equal(Brushes.Gray, Vm("approved", isSystem: true).BodyBrush);
        Assert.NotEqual(Brushes.Gray, Vm("real comment").BodyBrush);
    }

    [Theory]
    [InlineData("<p>Hello</p>", "Hello")]
    [InlineData("Line one<br>Line two", "Line one\nLine two")]
    [InlineData("<div><b>Bold</b> text</div>", "Bold text")]
    // Entities are decoded once the body is known to be HTML.
    [InlineData("<p>Tom &amp; Jerry</p>", "Tom & Jerry")]
    [InlineData("<p>a &lt;b&gt; c</p>", "a <b> c")]
    public void StripsAzureDevOpsHtml(string html, string expected)
    {
        // A wall of raw markup is worse than plain text, and hosting a browser
        // control for a two-line comment is not worth it.
        Assert.Equal(expected, PullRequestCommentViewModel.Strip(html));
    }

    [Theory]
    // GitHub sends markdown; with no tags there is nothing to strip.
    [InlineData("Fixes **the** thing, see `Program.cs`")]
    // And an ampersand in markdown is literal text, not an entity to decode —
    // which is why tag-free input is left completely alone.
    [InlineData("Ben &amp; co, see the docs")]
    public void LeavesTagFreeBodiesAlone(string markdown)
    {
        Assert.Equal(markdown, PullRequestCommentViewModel.Strip(markdown));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyBodiesBecomeEmptyStrings(string? body)
    {
        Assert.Equal("", PullRequestCommentViewModel.Strip(body));
    }

    [Fact]
    public void UndatedCommentsRenderNoStamp()
    {
        Assert.Equal("", Vm("x").DateLabel);
    }

    [Fact]
    public void DatedCommentsRenderAStamp()
    {
        Assert.NotEqual("", Vm("x", date: new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc)).DateLabel);
    }
}
