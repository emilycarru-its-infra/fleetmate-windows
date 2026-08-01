using System.Windows;
using System.Windows.Media;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Shared;

namespace FleetMate.GUI.Views.Shared;

/// <summary>One entry in the conversation.</summary>
public sealed class PullRequestCommentViewModel
{
    public required PullRequestComment Comment { get; init; }

    public string AuthorName => Comment.AuthorName;

    /// <summary>
    /// Azure DevOps sends comment bodies as HTML. Stripping the tags is crude,
    /// but a wall of raw markup is worse than plain text, and the alternative is
    /// hosting a browser control for a two-line comment.
    /// </summary>
    public string Body => Strip(Comment.Body);

    public string DateLabel => Comment.Date?.ToLocalTime().ToString("d MMM, HH:mm") ?? "";

    /// <summary>
    /// Vote and status noise renders grey. It is context, not conversation, and
    /// giving it the same weight as a real comment buries what people said.
    /// </summary>
    public Brush BodyBrush => Comment.IsSystem
        ? Brushes.Gray
        : Application.Current?.TryFindResource("SystemControlForegroundBaseHighBrush") as Brush
          ?? Brushes.Black;

    internal static string Strip(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        if (!html.Contains('<')) return html.Trim();

        var text = System.Text.RegularExpressions.Regex.Replace(html, "<br\\s*/?>", "\n");
        text = System.Text.RegularExpressions.Regex.Replace(text, "</p>", "\n");
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "");

        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }
}

/// <summary>
/// One rendered diff row.
///
/// The colours are deliberately low-saturation tints rather than the saturated
/// red/green of a terminal diff: a file card is mostly context, and strong
/// fills over many rows make the changed lines harder to pick out, not easier.
/// They are also fixed rather than theme-resolved — a diff that inverts with
/// the app theme stops meaning what every other diff tool means.
/// </summary>
public sealed class DiffLineViewModel
{
    public required DiffLine Line { get; init; }

    public string Content => Line.Content;

    /// <summary>Blank rather than 0 where a side has no line, so the gutter reads as absence.</summary>
    public string OldLineLabel => Line.OldLine?.ToString() ?? "";
    public string NewLineLabel => Line.NewLine?.ToString() ?? "";

    /// <summary>The +/-/space marker, restored for display.</summary>
    public string Marker => Line.Kind switch
    {
        DiffLineKind.Addition => "+",
        DiffLineKind.Deletion => "-",
        DiffLineKind.NoNewline => "\\",
        _ => " ",
    };

    public Brush RowBackground => Line.Kind switch
    {
        DiffLineKind.Addition => AdditionFill,
        DiffLineKind.Deletion => DeletionFill,
        _ => Brushes.Transparent,
    };

    /// <summary>
    /// A saturated strip down the edge of changed rows. It carries the
    /// add/remove signal at a glance even where the row tint is too subtle,
    /// and it is what makes the diff readable to someone who cannot separate
    /// red from green.
    /// </summary>
    public Brush GutterStrip => Line.Kind switch
    {
        DiffLineKind.Addition => AdditionStrip,
        DiffLineKind.Deletion => DeletionStrip,
        _ => Brushes.Transparent,
    };

    public Brush ContentBrush => Line.Kind == DiffLineKind.NoNewline
        ? Brushes.Gray
        : NormalText;

    public FontStyle ContentStyle => Line.Kind == DiffLineKind.NoNewline
        ? FontStyles.Italic
        : FontStyles.Normal;

    private static readonly Brush AdditionFill = Frozen(Color.FromArgb(0x2A, 0x2D, 0xA4, 0x4E));
    private static readonly Brush DeletionFill = Frozen(Color.FromArgb(0x2A, 0xD1, 0x3A, 0x3A));
    private static readonly Brush AdditionStrip = Frozen(Color.FromRgb(0x2D, 0xA4, 0x4E));
    private static readonly Brush DeletionStrip = Frozen(Color.FromRgb(0xD1, 0x3A, 0x3A));

    /// <summary>
    /// Content colour follows the theme so code stays legible in dark mode,
    /// unlike the diff tints which must not invert.
    /// </summary>
    private static Brush NormalText =>
        Application.Current?.TryFindResource("SystemControlForegroundBaseHighBrush") as Brush
        ?? Brushes.Black;

    /// <summary>Frozen so one brush can serve every row without re-allocating.</summary>
    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>One <c>@@</c> section, with its rows.</summary>
public sealed class DiffHunkViewModel
{
    public required DiffHunk Hunk { get; init; }

    public string Header => Hunk.Header;

    public List<DiffLineViewModel> Lines =>
        Hunk.Lines.Select(l => new DiffLineViewModel { Line = l }).ToList();
}

/// <summary>One file card in the Changes section.</summary>
public sealed class DiffFileViewModel
{
    public required DiffFile File { get; init; }

    public string Path => File.DisplayPath;

    /// <summary>"+12 −3", or a status word where there is nothing to count.</summary>
    public string StatLabel => $"+{File.Insertions}  −{File.Deletions}";

    public Brush InsertionBrush => new SolidColorBrush(Color.FromRgb(0x2D, 0xA4, 0x4E));
    public Brush DeletionBrush => new SolidColorBrush(Color.FromRgb(0xD1, 0x3A, 0x3A));

    public string StatusLabel => File switch
    {
        { IsAddition: true } => "added",
        { IsDeletion: true } => "deleted",
        _ => "",
    };

    public Visibility StatusVisibility =>
        string.IsNullOrEmpty(StatusLabel) ? Visibility.Collapsed : Visibility.Visible;

    public List<DiffHunkViewModel> Hunks =>
        File.Hunks.Select(h => new DiffHunkViewModel { Hunk = h }).ToList();

    /// <summary>
    /// A file with no hunks is binary, too large, or a rename with no content
    /// change. The card still renders — the reader needs to know it changed —
    /// but says why there is nothing to show.
    /// </summary>
    public Visibility EmptyNoticeVisibility =>
        File.Hunks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public string EmptyNotice => "No text diff available — binary, too large, or renamed without changes.";
}
