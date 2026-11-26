namespace FleetMate.Core.Shared;

/// <summary>What one row of a rendered diff is.</summary>
public enum DiffRowKind
{
    /// <summary>A file's path, status and +/- counts.</summary>
    FileHeader,

    /// <summary>A file with no hunks: binary, too large, or renamed without changes.</summary>
    EmptyNotice,

    /// <summary>An <c>@@</c> line.</summary>
    HunkHeader,

    /// <summary>One line of an inline diff.</summary>
    Line,

    /// <summary>One side-by-side row: old on the left, new on the right.</summary>
    Split,
}

/// <summary>
/// One row of a diff, flattened across files and hunks so a UI can draw a long
/// diff in one virtualizing list. Text only; each UI picks its own colours.
/// </summary>
public sealed record DiffRow(DiffRowKind Kind, DiffFile File)
{
    public DiffHunk? Hunk { get; init; }

    /// <summary>The line of an inline row.</summary>
    public DiffLine? Line { get; init; }

    /// <summary>The two halves of a side-by-side row; null where that side has no line.</summary>
    public DiffLine? Left { get; init; }
    public DiffLine? Right { get; init; }
}

/// <summary>The words on diff rows, shared by FleetMate.GUI and FleetMate.WinUI.</summary>
public static class DiffText
{
    public const string EmptyNotice = "No text diff available — binary, too large, or renamed without changes.";

    /// <summary>The +/-/space marker the parser strips from <see cref="DiffLine.Content"/>.</summary>
    public static string Marker(DiffLineKind kind) => kind switch
    {
        DiffLineKind.Addition => "+",
        DiffLineKind.Deletion => "-",
        DiffLineKind.NoNewline => "\\",
        _ => " ",
    };

    /// <summary>Blank rather than 0 where a side has no line, so the gutter reads as absence.</summary>
    public static string Number(int? line) => line?.ToString() ?? "";

    /// <summary>The line number one side of a split row shows: old on the left, new on the right.</summary>
    public static string SideNumber(DiffLine? line, bool isLeft) => Number(isLeft ? line?.OldLine : line?.NewLine);

    /// <summary>"+ code" for a split half; empty for no line.</summary>
    public static string SideContent(DiffLine? line) => line is null ? "" : $"{Marker(line.Kind)} {line.Content}";

    /// <summary>"Added line 12, code" — what a screen reader says for one line.</summary>
    public static string Accessible(DiffLine? line, string numberLabel)
    {
        if (line is null) return "No line";
        var kind = line.Kind switch
        {
            DiffLineKind.Addition => "Added",
            DiffLineKind.Deletion => "Removed",
            DiffLineKind.Context => "Unchanged",
            _ => "",
        };
        var number = numberLabel.Length > 0 ? $"line {numberLabel}, " : "";
        return $"{kind} {number}{line.Content}".Trim();
    }

    /// <summary>An inline line's spoken text, numbered by whichever side it has.</summary>
    public static string Accessible(DiffLine line) =>
        Accessible(line, Number(line.NewLine ?? line.OldLine));

    /// <summary>"added", "deleted", or empty.</summary>
    public static string FileStatus(DiffFile file) =>
        file.IsAddition ? "added" : file.IsDeletion ? "deleted" : "";

    public static string FileStats(DiffFile file) => $"+{file.Insertions}  −{file.Deletions}";
}

public static class DiffRows
{
    /// <summary>Every file as rows: its header, then each hunk's header and lines in <paramref name="layout"/>.</summary>
    public static List<DiffRow> Flatten(IEnumerable<DiffFile> files, DiffLayout layout)
    {
        var rows = new List<DiffRow>();
        foreach (var file in files)
        {
            rows.Add(new DiffRow(DiffRowKind.FileHeader, file));
            if (file.Hunks.Count == 0)
            {
                rows.Add(new DiffRow(DiffRowKind.EmptyNotice, file));
                continue;
            }
            foreach (var hunk in file.Hunks)
            {
                rows.Add(new DiffRow(DiffRowKind.HunkHeader, file) { Hunk = hunk });
                if (layout == DiffLayout.Split)
                    rows.AddRange(DiffSplit.Rows(hunk).Select(r =>
                        new DiffRow(DiffRowKind.Split, file) { Hunk = hunk, Left = r.Left, Right = r.Right }));
                else
                    rows.AddRange(hunk.Lines.Select(l => new DiffRow(DiffRowKind.Line, file) { Hunk = hunk, Line = l }));
            }
        }
        return rows;
    }
}
