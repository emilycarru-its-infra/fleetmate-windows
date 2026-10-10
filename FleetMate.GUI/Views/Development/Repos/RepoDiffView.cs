using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FleetMate.Core.Shared;
using FleetMate.GUI.Views.Shared;

namespace FleetMate.GUI.Views.Development.Repos;

/// <summary>
/// A structured diff: one card per file, one block per chunk. In the working
/// tree each chunk carries Stage, Unstage and Discard; a commit's diff is read
/// only. Lines render with the app's shared diff row template.
/// </summary>
public sealed class RepoDiffView : UserControl
{
    private readonly StackPanel _files = new() { Margin = new Thickness(0, 0, 8, 8) };
    private readonly TextBox _preamble;
    private readonly TextBlock _empty;

    /// <summary>Stage one chunk (<c>git apply --cached</c>).</summary>
    public Func<DiffFile, DiffHunk, Task>? StageChunk { get; set; }
    /// <summary>Unstage one chunk (<c>git apply --cached --reverse</c>).</summary>
    public Func<DiffFile, DiffHunk, Task>? UnstageChunk { get; set; }
    /// <summary>Discard one chunk from the working tree; the caller confirms first.</summary>
    public Func<DiffFile, DiffHunk, Task>? DiscardChunk { get; set; }

    public RepoDiffView()
    {
        _preamble = new TextBox
        {
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 8, 10),
            Visibility = Visibility.Collapsed,
        };
        _preamble.SetResourceReference(ForegroundProperty, "SystemControlForegroundBaseHighBrush");
        _empty = new TextBlock
        {
            Opacity = 0.65,
            Margin = new Thickness(4),
            TextWrapping = TextWrapping.Wrap,
        };
        var stack = new StackPanel();
        stack.Children.Add(_preamble);
        stack.Children.Add(_empty);
        stack.Children.Add(_files);
        Content = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    /// <summary>
    /// Shows <paramref name="text"/>: a unified diff, or <c>git show</c> output
    /// whose commit header comes before the first file.
    /// </summary>
    public void Show(string text, bool workingTree, bool anyStaged, string emptyMessage)
    {
        _files.Children.Clear();
        int start;
        if (text.StartsWith("diff --git", StringComparison.Ordinal)) start = 0;
        else
        {
            var index = text.IndexOf("\ndiff --git", StringComparison.Ordinal);
            start = index < 0 ? -1 : index + 1;
        }
        var preamble = start < 0 ? text.TrimEnd() : text[..start].TrimEnd();
        _preamble.Text = preamble;
        _preamble.Visibility = preamble.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var patch = start >= 0 ? DiffParser.Parse(text[start..]) : new DiffPatch();
        _empty.Text = emptyMessage;
        _empty.Visibility = patch.Files.Count == 0 && preamble.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var file in patch.Files) _files.Children.Add(FileCard(file, workingTree, anyStaged));
    }

    private UIElement FileCard(DiffFile file, bool workingTree, bool anyStaged)
    {
        var model = new DiffFileViewModel { File = file };
        var header = new DockPanel { Margin = new Thickness(10, 6, 10, 6) };
        var stat = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        stat.Inlines.Add(new System.Windows.Documents.Run($"+{file.Insertions}") { Foreground = RepoBrushes.Added });
        stat.Inlines.Add(new System.Windows.Documents.Run("  "));
        stat.Inlines.Add(new System.Windows.Documents.Run($"−{file.Deletions}") { Foreground = RepoBrushes.Removed });
        DockPanel.SetDock(stat, Dock.Right);
        header.Children.Add(stat);
        if (model.StatusLabel.Length > 0)
        {
            var status = new TextBlock { Text = model.StatusLabel, FontSize = 10, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            status.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
            DockPanel.SetDock(status, Dock.Right);
            header.Children.Add(status);
        }
        header.Children.Add(new TextBlock
        {
            Text = file.DisplayPath,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = file.DisplayPath,
        });
        var headerBorder = new Border { Child = header, CornerRadius = new CornerRadius(6, 6, 0, 0) };
        headerBorder.SetResourceReference(Border.BackgroundProperty, "SystemControlBackgroundChromeMediumLowBrush");

        var body = new StackPanel();
        body.Children.Add(headerBorder);
        if (file.Hunks.Count == 0)
        {
            var notice = new TextBlock { Text = model.EmptyNotice, Padding = new Thickness(10, 8, 10, 8), FontSize = 11, TextWrapping = TextWrapping.Wrap };
            notice.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
            body.Children.Add(notice);
        }
        var lineTemplate = TryFindResource("DiffLineTemplate") as DataTemplate;
        foreach (var hunk in file.Hunks)
        {
            var hunkHeader = new DockPanel { Margin = new Thickness(6, 2, 6, 2) };
            if (workingTree)
            {
                var actions = new StackPanel { Orientation = Orientation.Horizontal };
                actions.Children.Add(ChunkButton("Stage Chunk", "Stage this chunk", () => StageChunk?.Invoke(file, hunk)));
                if (anyStaged) actions.Children.Add(ChunkButton("Unstage Chunk", "Unstage this chunk", () => UnstageChunk?.Invoke(file, hunk)));
                actions.Children.Add(ChunkButton("Discard Chunk", "Throw away this chunk of your changes (asks first)", () => DiscardChunk?.Invoke(file, hunk), destructive: true));
                DockPanel.SetDock(actions, Dock.Right);
                hunkHeader.Children.Add(actions);
            }
            var label = new TextBlock { Text = hunk.Header, FontFamily = new FontFamily("Consolas"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
            hunkHeader.Children.Add(label);
            var hunkBorder = new Border { Child = hunkHeader };
            hunkBorder.SetResourceReference(Border.BackgroundProperty, "SystemControlBackgroundChromeMediumBrush");
            body.Children.Add(hunkBorder);
            body.Children.Add(new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new ItemsControl
                {
                    ItemsSource = new DiffHunkViewModel { Hunk = hunk }.Lines,
                    ItemTemplate = lineTemplate,
                },
            });
        }
        var card = new Border
        {
            Child = body,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 0, 12),
        };
        card.SetResourceReference(Border.BorderBrushProperty, "SystemControlForegroundBaseLowBrush");
        return card;
    }

    private static Button ChunkButton(string text, string tip, Func<Task?> action, bool destructive = false)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 10,
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = tip,
        };
        if (destructive) button.Foreground = RepoBrushes.Warning;
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { if (action() is { } task) await task; }
            finally { button.IsEnabled = true; }
        };
        return button;
    }
}
