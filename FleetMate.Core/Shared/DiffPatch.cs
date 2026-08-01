using System.Text;

namespace FleetMate.Core.Shared;

/// <summary>How a diff line relates the two sides.</summary>
public enum DiffLineKind
{
    Context,
    Addition,
    Deletion,

    /// <summary><c>\ No newline at end of file</c>.</summary>
    NoNewline,
}

/// <summary>One line of a hunk.</summary>
public sealed class DiffLine
{
    public DiffLineKind Kind { get; init; }

    /// <summary>1-based line number on the pre-image side, or null for additions.</summary>
    public int? OldLine { get; init; }

    /// <summary>1-based line number on the post-image side, or null for deletions.</summary>
    public int? NewLine { get; init; }

    /// <summary>Line content WITHOUT the leading +/- marker.</summary>
    public string Content { get; init; } = string.Empty;
}

/// <summary>One <c>@@ … @@</c> section.</summary>
public sealed class DiffHunk
{
    /// <summary>The raw <c>@@ -old,olen +new,nlen @@ trailing context</c> header.</summary>
    public string Header { get; init; } = string.Empty;

    public int OldStart { get; init; }
    public int OldCount { get; init; }
    public int NewStart { get; init; }
    public int NewCount { get; init; }
    public List<DiffLine> Lines { get; init; } = new();

    public int Insertions => Lines.Count(l => l.Kind == DiffLineKind.Addition);
    public int Deletions => Lines.Count(l => l.Kind == DiffLineKind.Deletion);
}

/// <summary>One file's worth of diff.</summary>
public sealed class DiffFile
{
    /// <summary>Lines from <c>diff --git</c> up to (not including) the first hunk.</summary>
    public List<string> HeaderLines { get; init; } = new();

    /// <summary>Path with its <c>a/</c> prefix, as it appears in the header.</summary>
    public string OldPath { get; init; } = string.Empty;

    /// <summary>Path with its <c>b/</c> prefix, as it appears in the header.</summary>
    public string NewPath { get; init; } = string.Empty;

    public List<DiffHunk> Hunks { get; init; } = new();

    public string Id => $"{OldPath}→{NewPath}";

    /// <summary>
    /// Name for display — prefers the new path, since that is the current state,
    /// and falls back to the old path for deletions where there is no new one.
    /// </summary>
    public string DisplayPath =>
        NewPath == "/dev/null" ? Clean(OldPath) : Clean(NewPath);

    /// <summary>True when the file was added in this change.</summary>
    public bool IsAddition => OldPath == "/dev/null";

    /// <summary>True when the file was removed in this change.</summary>
    public bool IsDeletion => NewPath == "/dev/null";

    public int Insertions => Hunks.Sum(h => h.Insertions);
    public int Deletions => Hunks.Sum(h => h.Deletions);

    private static string Clean(string path) =>
        path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal)
            ? path[2..]
            : path;
}

/// <summary>A parsed unified-diff payload — one entry per file.</summary>
public sealed class DiffPatch
{
    public List<DiffFile> Files { get; init; } = new();

    public bool IsEmpty => Files.Count == 0 || Files.All(f => f.Hunks.Count == 0);

    public int Insertions => Files.Sum(f => f.Insertions);
    public int Deletions => Files.Sum(f => f.Deletions);
}

/// <summary>
/// Parses <c>git diff</c>-style unified output.
///
/// Deliberately forgiving: anything outside a <c>diff --git</c> block is
/// dropped rather than treated as an error. Diff text arrives from several
/// providers with their own preambles and trailers, and refusing to render a
/// diff because of an unrecognised line would be worse than ignoring it.
/// </summary>
public static class DiffParser
{
    public static DiffPatch Parse(string? text)
    {
        var patch = new DiffPatch();
        if (string.IsNullOrEmpty(text)) return patch;

        var lines = SplitLines(text);
        var index = 0;

        while (index < lines.Length)
        {
            if (!lines[index].StartsWith("diff --git ", StringComparison.Ordinal))
            {
                index++;
                continue;
            }

            var firstLine = lines[index];
            var header = new List<string> { firstLine };
            var oldPath = "";
            var newPath = "";
            index++;

            // Header lines run until the first hunk, or the next file.
            while (index < lines.Length)
            {
                var candidate = lines[index];
                if (candidate.StartsWith("@@ ", StringComparison.Ordinal) ||
                    candidate.StartsWith("diff --git ", StringComparison.Ordinal))
                {
                    break;
                }

                header.Add(candidate);
                if (candidate.StartsWith("--- ", StringComparison.Ordinal)) oldPath = candidate[4..];
                else if (candidate.StartsWith("+++ ", StringComparison.Ordinal)) newPath = candidate[4..];

                index++;
            }

            if (oldPath.Length == 0)
            {
                // Fall back to the paths embedded in `diff --git a/x b/y` — a
                // rename or mode-only change carries no ---/+++ pair.
                var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4)
                {
                    oldPath = parts[2];
                    newPath = parts[3];
                }
            }

            var file = new DiffFile { HeaderLines = header, OldPath = oldPath, NewPath = newPath };

            while (index < lines.Length && lines[index].StartsWith("@@ ", StringComparison.Ordinal))
            {
                var (hunk, consumed) = ParseHunk(lines, index);
                file.Hunks.Add(hunk);
                index += consumed;
            }

            patch.Files.Add(file);
        }

        return patch;
    }

    /// <summary>
    /// Parse hunks that arrive with no <c>diff --git</c> header — the shape of
    /// GitHub's per-file <c>patch</c> strings — by synthesising one.
    /// </summary>
    public static DiffFile ParseBareHunks(string? patch, string fileName)
    {
        var synthetic =
            $"diff --git a/{fileName} b/{fileName}\n--- a/{fileName}\n+++ b/{fileName}\n{patch}";

        return Parse(synthetic).Files.FirstOrDefault()
               ?? new DiffFile { OldPath = fileName, NewPath = fileName };
    }

    private static (DiffHunk Hunk, int Consumed) ParseHunk(string[] lines, int start)
    {
        var header = lines[start];
        var (oldStart, oldCount, newStart, newCount) = ParseHunkRanges(header);

        var hunk = new DiffHunk
        {
            Header = header,
            OldStart = oldStart,
            OldCount = oldCount,
            NewStart = newStart,
            NewCount = newCount,
        };

        var oldCursor = oldStart;
        var newCursor = newStart;
        var consumed = 1;
        var index = start + 1;

        while (index < lines.Length)
        {
            var line = lines[index];
            if (line.StartsWith("@@ ", StringComparison.Ordinal) ||
                line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                break;
            }

            if (line.Length == 0)
            {
                // An empty line inside a hunk is a context line whose single
                // space marker was stripped in transit; a trailing empty line at
                // the very end is just the text's final newline.
                if (index == lines.Length - 1) break;

                hunk.Lines.Add(new DiffLine
                {
                    Kind = DiffLineKind.Context,
                    OldLine = oldCursor++,
                    NewLine = newCursor++,
                    Content = "",
                });
                index++;
                consumed++;
                continue;
            }

            var marker = line[0];
            var body = line[1..];

            switch (marker)
            {
                case '+':
                    hunk.Lines.Add(new DiffLine
                    {
                        Kind = DiffLineKind.Addition, NewLine = newCursor++, Content = body,
                    });
                    break;

                case '-':
                    hunk.Lines.Add(new DiffLine
                    {
                        Kind = DiffLineKind.Deletion, OldLine = oldCursor++, Content = body,
                    });
                    break;

                case ' ':
                    hunk.Lines.Add(new DiffLine
                    {
                        Kind = DiffLineKind.Context,
                        OldLine = oldCursor++,
                        NewLine = newCursor++,
                        Content = body,
                    });
                    break;

                case '\\':
                    hunk.Lines.Add(new DiffLine { Kind = DiffLineKind.NoNewline, Content = body });
                    break;

                default:
                    // Anything else ends the hunk — providers append trailers.
                    return (hunk, consumed);
            }

            index++;
            consumed++;
        }

        return (hunk, consumed);
    }

    /// <summary>
    /// Pull <c>(oldStart, oldCount, newStart, newCount)</c> out of a hunk header.
    /// A missing count means 1, which is git's own convention.
    /// </summary>
    internal static (int OldStart, int OldCount, int NewStart, int NewCount) ParseHunkRanges(string header)
    {
        var oldStart = 0;
        var oldCount = 1;
        var newStart = 0;
        var newCount = 1;

        var minus = header.IndexOf('-');
        var plus = header.IndexOf('+');

        if (minus >= 0) (oldStart, oldCount) = ReadRange(header, minus + 1);
        if (plus >= 0) (newStart, newCount) = ReadRange(header, plus + 1);

        return (oldStart, oldCount, newStart, newCount);

        static (int Start, int Count) ReadRange(string text, int at)
        {
            var start = ReadInt(text, ref at);
            var count = 1;

            if (at < text.Length && text[at] == ',')
            {
                at++;
                count = ReadInt(text, ref at);
            }

            return (start, count);
        }

        static int ReadInt(string text, ref int at)
        {
            var begin = at;
            while (at < text.Length && char.IsAsciiDigit(text[at])) at++;
            return at > begin && int.TryParse(text.AsSpan(begin, at - begin), out var value) ? value : 0;
        }
    }

    /// <summary>
    /// Split on newlines, keeping empty entries and tolerating CRLF.
    ///
    /// Empty lines carry meaning inside a hunk, so they cannot be dropped, and
    /// a stray CR would otherwise become part of the line content and show up as
    /// a spurious change.
    /// </summary>
    internal static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}

/// <summary>
/// Builds a <see cref="DiffFile"/> from two file contents, for providers that
/// serve before/after blobs rather than unified-diff text.
///
/// Azure DevOps has no diff endpoint at all, so its diffs have to be computed
/// here from the blobs at the pull request's merge-base and source commits.
/// </summary>
public static class DiffBuilder
{
    /// <summary>
    /// Above this many cells the LCS table is too large to hold, and the file is
    /// reported as a wholesale replacement instead.
    ///
    /// A quadratic table over two 10k-line files is 100M entries; refusing to
    /// allocate that is better than an OutOfMemoryException halfway through
    /// rendering a diff. Prefix/suffix trimming means real edits rarely reach it.
    /// </summary>
    private const long MaxLcsCells = 4_000_000;

    public static DiffFile Build(string fileName, string? old, string? @new, int context = 3)
    {
        var oldLines = DiffParser.SplitLines(old ?? "");
        var newLines = DiffParser.SplitLines(@new ?? "");

        var annotated = Annotate(oldLines, newLines);
        var hunks = GroupIntoHunks(annotated, context);

        return new DiffFile
        {
            HeaderLines = new List<string> { $"diff --git a/{fileName} b/{fileName}" },
            OldPath = $"a/{fileName}",
            NewPath = $"b/{fileName}",
            Hunks = hunks,
        };
    }

    /// <summary>Walk both sides and emit one flat annotated line list.</summary>
    private static List<DiffLine> Annotate(string[] oldLines, string[] newLines)
    {
        var result = new List<DiffLine>();

        // Trim the matching head and tail first. Most edits touch a small part
        // of a file, so this usually reduces the LCS problem to almost nothing.
        var prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length
               && oldLines[prefix] == newLines[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < oldLines.Length - prefix && suffix < newLines.Length - prefix
               && oldLines[^(suffix + 1)] == newLines[^(suffix + 1)])
        {
            suffix++;
        }

        for (var i = 0; i < prefix; i++)
        {
            result.Add(new DiffLine
            {
                Kind = DiffLineKind.Context,
                OldLine = i + 1,
                NewLine = i + 1,
                Content = oldLines[i],
            });
        }

        var oldMid = oldLines[prefix..(oldLines.Length - suffix)];
        var newMid = newLines[prefix..(newLines.Length - suffix)];

        result.AddRange(DiffMiddle(oldMid, newMid, prefix));

        for (var i = 0; i < suffix; i++)
        {
            var oldIndex = oldLines.Length - suffix + i;
            var newIndex = newLines.Length - suffix + i;
            result.Add(new DiffLine
            {
                Kind = DiffLineKind.Context,
                OldLine = oldIndex + 1,
                NewLine = newIndex + 1,
                Content = oldLines[oldIndex],
            });
        }

        return result;
    }

    private static List<DiffLine> DiffMiddle(string[] oldMid, string[] newMid, int offset)
    {
        var lines = new List<DiffLine>();

        if (oldMid.Length == 0 && newMid.Length == 0) return lines;

        // Too large to diff properly: report it as a full replacement rather
        // than allocating a table that will not fit.
        if ((long)oldMid.Length * newMid.Length > MaxLcsCells)
        {
            for (var i = 0; i < oldMid.Length; i++)
            {
                lines.Add(new DiffLine
                {
                    Kind = DiffLineKind.Deletion, OldLine = offset + i + 1, Content = oldMid[i],
                });
            }

            for (var i = 0; i < newMid.Length; i++)
            {
                lines.Add(new DiffLine
                {
                    Kind = DiffLineKind.Addition, NewLine = offset + i + 1, Content = newMid[i],
                });
            }

            return lines;
        }

        // Longest common subsequence, then walk it back into edits.
        var table = new int[oldMid.Length + 1, newMid.Length + 1];
        for (var i = oldMid.Length - 1; i >= 0; i--)
        {
            for (var j = newMid.Length - 1; j >= 0; j--)
            {
                table[i, j] = oldMid[i] == newMid[j]
                    ? table[i + 1, j + 1] + 1
                    : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        var x = 0;
        var y = 0;
        while (x < oldMid.Length && y < newMid.Length)
        {
            if (oldMid[x] == newMid[y])
            {
                lines.Add(new DiffLine
                {
                    Kind = DiffLineKind.Context,
                    OldLine = offset + x + 1,
                    NewLine = offset + y + 1,
                    Content = oldMid[x],
                });
                x++;
                y++;
            }
            else if (table[x + 1, y] >= table[x, y + 1])
            {
                lines.Add(new DiffLine
                {
                    Kind = DiffLineKind.Deletion, OldLine = offset + x + 1, Content = oldMid[x],
                });
                x++;
            }
            else
            {
                lines.Add(new DiffLine
                {
                    Kind = DiffLineKind.Addition, NewLine = offset + y + 1, Content = newMid[y],
                });
                y++;
            }
        }

        while (x < oldMid.Length)
        {
            lines.Add(new DiffLine
            {
                Kind = DiffLineKind.Deletion, OldLine = offset + x + 1, Content = oldMid[x],
            });
            x++;
        }

        while (y < newMid.Length)
        {
            lines.Add(new DiffLine
            {
                Kind = DiffLineKind.Addition, NewLine = offset + y + 1, Content = newMid[y],
            });
            y++;
        }

        return lines;
    }

    /// <summary>
    /// Group changed regions into hunks with <paramref name="context"/> lines
    /// around them, mirroring git's default of three.
    /// </summary>
    private static List<DiffHunk> GroupIntoHunks(List<DiffLine> annotated, int context)
    {
        var hunks = new List<DiffHunk>();
        var index = 0;

        while (index < annotated.Count)
        {
            if (annotated[index].Kind == DiffLineKind.Context)
            {
                index++;
                continue;
            }

            var start = Math.Max(0, index - context);
            var end = index;
            var sinceChange = 0;

            for (var cursor = index; cursor < annotated.Count; cursor++)
            {
                if (annotated[cursor].Kind == DiffLineKind.Context)
                {
                    sinceChange++;
                    // Once the gap exceeds twice the context the next change
                    // gets its own hunk; below that they would visually merge.
                    if (sinceChange > context * 2) break;
                }
                else
                {
                    sinceChange = 0;
                    end = cursor;
                }
            }

            var sliceEnd = Math.Min(annotated.Count, end + context + 1);
            var slice = annotated.GetRange(start, sliceEnd - start);

            var oldStart = slice.FirstOrDefault(l => l.OldLine.HasValue)?.OldLine ?? 1;
            var newStart = slice.FirstOrDefault(l => l.NewLine.HasValue)?.NewLine ?? 1;
            var oldCount = slice.Count(l => l.Kind != DiffLineKind.Addition);
            var newCount = slice.Count(l => l.Kind != DiffLineKind.Deletion);

            hunks.Add(new DiffHunk
            {
                Header = $"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@",
                OldStart = oldStart,
                OldCount = oldCount,
                NewStart = newStart,
                NewCount = newCount,
                Lines = slice,
            });

            index = sliceEnd;
        }

        return hunks;
    }
}
