using FleetMate.Core.Shared;
using FleetMate.WinUI.Pages.Development.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FleetMate.WinUI.Pages.Development.Repos;

/// <summary>
/// The Repos diff: git show's commit header, if any, then every file through
/// the shared <see cref="DiffFilesView"/>. In the working tree each chunk
/// carries Stage, Unstage and Discard; a commit's diff is read only.
/// </summary>
public sealed partial class RepoDiffView : UserControl
{
    private readonly TextBlock _preamble;
    private readonly TextBlock _empty;
    private readonly DiffFilesView _files = new();

    /// <summary>Stage one chunk (<c>git apply --cached</c>).</summary>
    public Func<DiffFile, DiffHunk, Task>? StageChunk { get; set; }
    /// <summary>Unstage one chunk (<c>git apply --cached --reverse</c>).</summary>
    public Func<DiffFile, DiffHunk, Task>? UnstageChunk { get; set; }
    /// <summary>Discard one chunk from the working tree; the model confirms first.</summary>
    public Func<DiffFile, DiffHunk, Task>? DiscardChunk { get; set; }

    public RepoDiffView()
    {
        _preamble = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Margin = new Thickness(0, 0, 0, 8),
            Visibility = Visibility.Collapsed,
        };
        _empty = new TextBlock
        {
            Foreground = RepoBrushes.Secondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4),
        };
        var stack = new StackPanel { Padding = new Thickness(0, 0, 12, 12) };
        stack.Children.Add(_preamble);
        stack.Children.Add(_empty);
        stack.Children.Add(_files);
        Content = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, "Diff");
    }

    /// <summary>
    /// Shows <paramref name="text"/>: a unified diff, or <c>git show</c> output
    /// whose commit header comes before the first file.
    /// </summary>
    public void Show(string text, bool workingTree, bool anyStaged, string emptyMessage)
    {
        int start;
        if (text.StartsWith("diff --git", StringComparison.Ordinal)) start = 0;
        else
        {
            var index = text.IndexOf("\ndiff --git", StringComparison.Ordinal);
            start = index < 0 ? -1 : index + 1;
        }
        var preamble = start < 0 ? text.TrimEnd() : text[..start].TrimEnd();
        _preamble.Text = preamble;
        _preamble.Visibility = DevelopmentUi.Show(preamble.Length > 0);

        var patch = start >= 0 ? DiffParser.Parse(text[start..]) : new DiffPatch();
        _empty.Text = emptyMessage;
        _empty.Visibility = DevelopmentUi.Show(patch.Files.Count == 0 && preamble.Length == 0);
        _files.HunkActions = workingTree ? Actions(anyStaged) : Array.Empty<DiffHunkAction>();
        _files.ShowFiles(patch.Files);
    }

    private IReadOnlyList<DiffHunkAction> Actions(bool anyStaged)
    {
        var actions = new List<DiffHunkAction>
        {
            new("Stage Chunk", "Stage this chunk", (f, h) => StageChunk?.Invoke(f, h) ?? Task.CompletedTask),
        };
        if (anyStaged) actions.Add(new("Unstage Chunk", "Unstage this chunk", (f, h) => UnstageChunk?.Invoke(f, h) ?? Task.CompletedTask));
        actions.Add(new("Discard Chunk", "Throw away this chunk of your changes (asks first)",
            (f, h) => DiscardChunk?.Invoke(f, h) ?? Task.CompletedTask, Destructive: true));
        return actions;
    }
}
