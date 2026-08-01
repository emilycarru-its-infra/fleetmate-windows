using FleetMate.Core.Shared;
using Xunit;

namespace FleetMate.Tests;

public class DiffParserTests
{
    private const string TwoFilePatch = """
        diff --git a/src/App.cs b/src/App.cs
        index 1234567..89abcde 100644
        --- a/src/App.cs
        +++ b/src/App.cs
        @@ -1,4 +1,5 @@
         using System;
        -var x = 1;
        +var x = 2;
        +var y = 3;
         Console.WriteLine(x);
        diff --git a/README.md b/README.md
        --- a/README.md
        +++ b/README.md
        @@ -10,2 +10,2 @@ section heading
        -old line
        +new line
        """;

    [Fact]
    public void SplitsOnFileHeaders()
    {
        var patch = DiffParser.Parse(TwoFilePatch);

        Assert.Equal(2, patch.Files.Count);
        Assert.Equal("src/App.cs", patch.Files[0].DisplayPath);
        Assert.Equal("README.md", patch.Files[1].DisplayPath);
    }

    [Fact]
    public void TracksLineNumbersOnBothSides()
    {
        var hunk = DiffParser.Parse(TwoFilePatch).Files[0].Hunks[0];

        var context = hunk.Lines[0];
        Assert.Equal(DiffLineKind.Context, context.Kind);
        Assert.Equal(1, context.OldLine);
        Assert.Equal(1, context.NewLine);

        var deletion = hunk.Lines[1];
        Assert.Equal(DiffLineKind.Deletion, deletion.Kind);
        Assert.Equal(2, deletion.OldLine);
        // A deletion has no line on the new side; rendering one would put a
        // number in the gutter next to a line that is not there.
        Assert.Null(deletion.NewLine);

        var addition = hunk.Lines[2];
        Assert.Equal(DiffLineKind.Addition, addition.Kind);
        Assert.Null(addition.OldLine);
        Assert.Equal(2, addition.NewLine);
    }

    [Fact]
    public void StripsTheMarkerFromContent()
    {
        var hunk = DiffParser.Parse(TwoFilePatch).Files[0].Hunks[0];

        Assert.Equal("var x = 1;", hunk.Lines[1].Content);
        Assert.Equal("var x = 2;", hunk.Lines[2].Content);
        Assert.Equal("using System;", hunk.Lines[0].Content);
    }

    [Fact]
    public void CountsInsertionsAndDeletions()
    {
        var file = DiffParser.Parse(TwoFilePatch).Files[0];

        Assert.Equal(2, file.Insertions);
        Assert.Equal(1, file.Deletions);
    }

    [Theory]
    [InlineData("@@ -1,4 +1,5 @@", 1, 4, 1, 5)]
    [InlineData("@@ -10,2 +10,2 @@ section heading", 10, 2, 10, 2)]
    // Git omits the count when it is 1.
    [InlineData("@@ -5 +5 @@", 5, 1, 5, 1)]
    [InlineData("@@ -0,0 +1,3 @@", 0, 0, 1, 3)]
    [InlineData("@@ -1,200 +1,198 @@", 1, 200, 1, 198)]
    public void ParsesHunkRanges(string header, int oldStart, int oldCount, int newStart, int newCount)
    {
        var parsed = DiffParser.ParseHunkRanges(header);

        Assert.Equal((oldStart, oldCount, newStart, newCount), parsed);
    }

    [Fact]
    public void HandlesMultipleHunksInOneFile()
    {
        const string patch = """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1,2 +1,2 @@
            -one
            +ONE
            @@ -20,2 +20,2 @@
            -twenty
            +TWENTY
            """;

        var file = DiffParser.Parse(patch).Files.Single();

        Assert.Equal(2, file.Hunks.Count);
        Assert.Equal(20, file.Hunks[1].OldStart);
        Assert.Equal(2, file.Insertions);
        Assert.Equal(2, file.Deletions);
    }

    [Fact]
    public void RecognisesTheNoNewlineMarker()
    {
        const string patch = """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1 +1 @@
            -old
            \ No newline at end of file
            +new
            """;

        var lines = DiffParser.Parse(patch).Files.Single().Hunks.Single().Lines;

        var marker = lines.Single(l => l.Kind == DiffLineKind.NoNewline);
        // It occupies no line on either side — it is an annotation, not content.
        Assert.Null(marker.OldLine);
        Assert.Null(marker.NewLine);
    }

    [Fact]
    public void DetectsAdditionsAndDeletionsOfWholeFiles()
    {
        const string added = """
            diff --git a/new.txt b/new.txt
            --- /dev/null
            +++ b/new.txt
            @@ -0,0 +1 @@
            +hello
            """;

        const string removed = """
            diff --git a/gone.txt b/gone.txt
            --- a/gone.txt
            +++ /dev/null
            @@ -1 +0,0 @@
            -goodbye
            """;

        var addedFile = DiffParser.Parse(added).Files.Single();
        Assert.True(addedFile.IsAddition);
        // The display name has to come from the surviving side.
        Assert.Equal("new.txt", addedFile.DisplayPath);

        var removedFile = DiffParser.Parse(removed).Files.Single();
        Assert.True(removedFile.IsDeletion);
        Assert.Equal("gone.txt", removedFile.DisplayPath);
    }

    [Fact]
    public void FallsBackToThePathsInTheGitHeader()
    {
        // A rename or mode-only change carries no ---/+++ pair.
        const string patch = """
            diff --git a/old/name.cs b/new/name.cs
            similarity index 98%
            rename from old/name.cs
            rename to new/name.cs
            """;

        var file = DiffParser.Parse(patch).Files.Single();

        Assert.Equal("a/old/name.cs", file.OldPath);
        Assert.Equal("b/new/name.cs", file.NewPath);
        Assert.Equal("new/name.cs", file.DisplayPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a diff at all")]
    [InlineData("Binary files a/x and b/x differ")]
    public void UnrecognisedInputProducesAnEmptyPatch(string? text)
    {
        // Refusing to render because of an unrecognised line would be worse
        // than ignoring it — providers add their own preambles and trailers.
        var patch = DiffParser.Parse(text);

        Assert.True(patch.IsEmpty);
        Assert.Empty(patch.Files);
    }

    [Fact]
    public void ToleratesCrlf()
    {
        var patch = DiffParser.Parse(TwoFilePatch.Replace("\n", "\r\n"));

        Assert.Equal(2, patch.Files.Count);
        // A stray CR left on the content shows up as a spurious change.
        Assert.DoesNotContain(
            patch.Files.SelectMany(f => f.Hunks).SelectMany(h => h.Lines),
            l => l.Content.Contains('\r'));
    }

    [Fact]
    public void KeepsEmptyContextLines()
    {
        // A blank line inside a hunk is content; dropping it shifts every line
        // number after it.
        const string patch = "diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n@@ -1,3 +1,3 @@\n one\n\n-three\n+THREE\n";

        var lines = DiffParser.Parse(patch).Files.Single().Hunks.Single().Lines;

        Assert.Equal("", lines[1].Content);
        Assert.Equal(DiffLineKind.Context, lines[1].Kind);
        Assert.Equal(3, lines[2].OldLine);
    }
}

public class DiffParserBareHunkTests
{
    [Fact]
    public void ParsesGitHubsHeaderlessPatchStrings()
    {
        // GitHub's /pulls/files returns hunks with no diff --git header.
        const string githubPatch = "@@ -1,3 +1,4 @@\n context\n-removed\n+added\n+also added\n";

        var file = DiffParser.ParseBareHunks(githubPatch, "src/Program.cs");

        Assert.Equal("src/Program.cs", file.DisplayPath);
        Assert.Single(file.Hunks);
        Assert.Equal(2, file.Insertions);
        Assert.Equal(1, file.Deletions);
    }

    [Fact]
    public void NamesTheFileEvenWhenThePatchIsEmpty()
    {
        // A binary or too-large file comes back with no patch at all; the row
        // still has to render with its name.
        var file = DiffParser.ParseBareHunks(null, "assets/logo.png");

        Assert.Equal("assets/logo.png", file.DisplayPath);
        Assert.Empty(file.Hunks);
    }

    [Fact]
    public void HandlesAPathWithSpaces()
    {
        var file = DiffParser.ParseBareHunks("@@ -1 +1 @@\n-a\n+b\n", "My Folder/file name.txt");

        Assert.Single(file.Hunks);
    }
}

public class DiffBuilderTests
{
    [Fact]
    public void IdenticalContentProducesNoHunks()
    {
        var file = DiffBuilder.Build("a.txt", "one\ntwo\nthree", "one\ntwo\nthree");

        Assert.Empty(file.Hunks);
        Assert.Equal(0, file.Insertions);
        Assert.Equal(0, file.Deletions);
    }

    [Fact]
    public void DetectsASingleChangedLine()
    {
        var file = DiffBuilder.Build("a.txt", "one\ntwo\nthree", "one\nTWO\nthree");

        Assert.Single(file.Hunks);
        Assert.Equal(1, file.Insertions);
        Assert.Equal(1, file.Deletions);

        var changed = file.Hunks[0].Lines.Single(l => l.Kind == DiffLineKind.Addition);
        Assert.Equal("TWO", changed.Content);
        Assert.Equal(2, changed.NewLine);
    }

    [Fact]
    public void DetectsPureInsertion()
    {
        var file = DiffBuilder.Build("a.txt", "one\nthree", "one\ntwo\nthree");

        Assert.Equal(1, file.Insertions);
        Assert.Equal(0, file.Deletions);
        Assert.Equal("two", file.Hunks[0].Lines.Single(l => l.Kind == DiffLineKind.Addition).Content);
    }

    [Fact]
    public void DetectsPureDeletion()
    {
        var file = DiffBuilder.Build("a.txt", "one\ntwo\nthree", "one\nthree");

        Assert.Equal(0, file.Insertions);
        Assert.Equal(1, file.Deletions);
        Assert.Equal("two", file.Hunks[0].Lines.Single(l => l.Kind == DiffLineKind.Deletion).Content);
    }

    [Fact]
    public void HandlesAnEmptyOldFile()
    {
        var file = DiffBuilder.Build("new.txt", "", "alpha\nbeta");

        Assert.Equal(2, file.Insertions);
    }

    [Fact]
    public void HandlesAnEmptyNewFile()
    {
        var file = DiffBuilder.Build("gone.txt", "alpha\nbeta", "");

        Assert.Equal(2, file.Deletions);
    }

    [Fact]
    public void LineNumbersSurviveAPrefixOfUnchangedLines()
    {
        // Prefix trimming is an optimisation; it must not shift the numbering.
        var old = string.Join("\n", Enumerable.Range(1, 50).Select(i => $"line {i}"));
        var updated = old.Replace("line 40", "LINE 40");

        var file = DiffBuilder.Build("a.txt", old, updated);
        var changed = file.Hunks.SelectMany(h => h.Lines)
            .Single(l => l.Kind == DiffLineKind.Addition);

        Assert.Equal(40, changed.NewLine);
    }

    /// <summary>
    /// Zero-padded so no line's text is a substring of another's — with plain
    /// numbering, replacing "line 10" also rewrites "line 100".
    /// </summary>
    private static string NumberedLines(int count) =>
        string.Join("\n", Enumerable.Range(1, count).Select(i => $"line {i:D4}"));

    [Fact]
    public void SeparateEditsBecomeSeparateHunks()
    {
        var old = NumberedLines(100);
        var updated = old.Replace("line 0010", "CHANGED A").Replace("line 0090", "CHANGED B");

        var file = DiffBuilder.Build("a.txt", old, updated);

        // Far apart, so they must not be welded into one hunk spanning the file.
        Assert.Equal(2, file.Hunks.Count);
    }

    [Fact]
    public void NearbyEditsMergeIntoOneHunk()
    {
        var old = NumberedLines(100);
        var updated = old.Replace("line 0050", "CHANGED A").Replace("line 0052", "CHANGED B");

        var file = DiffBuilder.Build("a.txt", old, updated);

        // Their context overlaps, so one hunk reads better than two.
        Assert.Single(file.Hunks);
    }

    [Fact]
    public void HunksCarryThreeLinesOfContextByDefault()
    {
        var old = string.Join("\n", Enumerable.Range(1, 50).Select(i => $"line {i}"));
        var updated = old.Replace("line 25", "LINE 25");

        var hunk = DiffBuilder.Build("a.txt", old, updated).Hunks.Single();
        var context = hunk.Lines.Count(l => l.Kind == DiffLineKind.Context);

        // Three either side.
        Assert.Equal(6, context);
    }

    [Fact]
    public void ContextWidthIsConfigurable()
    {
        var old = string.Join("\n", Enumerable.Range(1, 50).Select(i => $"line {i}"));
        var updated = old.Replace("line 25", "LINE 25");

        var hunk = DiffBuilder.Build("a.txt", old, updated, context: 1).Hunks.Single();

        Assert.Equal(2, hunk.Lines.Count(l => l.Kind == DiffLineKind.Context));
    }

    [Fact]
    public void HunkHeaderIsWellFormed()
    {
        var file = DiffBuilder.Build("a.txt", "one\ntwo\nthree", "one\nTWO\nthree");

        Assert.Matches(@"^@@ -\d+,\d+ \+\d+,\d+ @@$", file.Hunks[0].Header);
    }

    [Fact]
    public void ProducesPathsTheParserWouldRecognise()
    {
        // The built file feeds the same renderer as parsed ones, so its paths
        // have to follow the same a//b/ convention.
        var file = DiffBuilder.Build("src/App.cs", "a", "b");

        Assert.Equal("a/src/App.cs", file.OldPath);
        Assert.Equal("b/src/App.cs", file.NewPath);
        Assert.Equal("src/App.cs", file.DisplayPath);
    }

    [Fact]
    public void VeryLargeFilesDegradeToAWholesaleReplacement()
    {
        // The guard exists so a huge pair reports something rather than
        // exhausting memory building a quadratic table.
        var old = string.Join("\n", Enumerable.Range(1, 3000).Select(i => $"old {i}"));
        var updated = string.Join("\n", Enumerable.Range(1, 3000).Select(i => $"new {i}"));

        var file = DiffBuilder.Build("huge.txt", old, updated);

        Assert.Equal(3000, file.Deletions);
        Assert.Equal(3000, file.Insertions);
    }

    [Fact]
    public void ToleratesCrlfInput()
    {
        var file = DiffBuilder.Build("a.txt", "one\r\ntwo\r\nthree", "one\r\nTWO\r\nthree");

        Assert.Single(file.Hunks);
        Assert.Equal(1, file.Insertions);
        Assert.DoesNotContain(file.Hunks[0].Lines, l => l.Content.Contains('\r'));
    }
}

public class DiffPatchAggregateTests
{
    [Fact]
    public void PatchTotalsSumItsFiles()
    {
        var patch = new DiffPatch
        {
            Files =
            {
                DiffBuilder.Build("a.txt", "one", "ONE"),
                DiffBuilder.Build("b.txt", "two\nthree", "two\nTHREE"),
            },
        };

        Assert.Equal(2, patch.Insertions);
        Assert.Equal(2, patch.Deletions);
        Assert.False(patch.IsEmpty);
    }

    [Fact]
    public void APatchOfUnchangedFilesReadsAsEmpty()
    {
        var patch = new DiffPatch { Files = { DiffBuilder.Build("a.txt", "same", "same") } };

        Assert.True(patch.IsEmpty);
    }
}
